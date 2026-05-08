using System.Collections.Generic;
using System.Text;

namespace HZExporter.ShaderGraph
{
    // Picks a stage template per (variant, …) and substitutes the per-block
    // expressions. Templates are kept as embedded constants here so the
    // skeleton lives in one place and isn't smeared across the orchestrator.
    static class SgCodegen
    {
        public static string Emit(SgGraph g, string sourceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Generated from {sourceName}.shadergraph by HZShaderGraphExporter");
            sb.AppendLine("#include \"renderer.glsl\"");
            sb.AppendLine();

            switch (g.Variant)
            {
                case SgVariant.Unlit: sb.Append(EmitFromTemplate(g, UNLIT_TEMPLATE, isLit: false)); break;
                case SgVariant.Lit:   sb.Append(EmitFromTemplate(g, LIT_TEMPLATE,   isLit: true));  break;
                default:
                    throw new System.NotImplementedException($"variant '{g.Variant}' not supported");
            }
            return sb.ToString();
        }

        // A surface assignment we'll emit at the end of main() — base_color =
        // …; metallic = …; etc. Carries an SgRhs so the optimizer's DCE can
        // count temp uses across both the statement list and these assigns.
        class Assign : SgOptimize.IAssignView
        {
            public string Symbol;
            public SgType Type;
            public SgRhs Rhs { get; set; }
            public string Format() => $"{SgTypes.Glsl(Type)} {Symbol} = {Rhs.ToGlsl()};";
        }

        static string EmitFromTemplate(SgGraph g, string template, bool isLit)
        {
            var ctx = new LowerCtx(g);
            var assigns = new List<Assign>();

            assigns.Add(LowerSurface(g, ctx, "BaseColor", "base_color", SgType.Vec3, Sg.LitConst(SgConst.Vec(SgType.Vec3, 0.5, 0.5, 0.5))));
            if (isLit)
            {
                assigns.Add(LowerSurface(g, ctx, "Metallic",   "metallic",   SgType.Float, Sg.Lit(0.0)));
                assigns.Add(LowerSurface(g, ctx, "Smoothness", "smoothness", SgType.Float, Sg.Lit(0.5)));
            }

            // Optimizer runs on each function body (with every output's Rhs
            // as a live root) and on main's body (with surface assigns as
            // roots).
            foreach (var fn in ctx.Functions)
            {
                var roots = new List<SgOptimize.IAssignView>(fn.Outputs.Count);
                foreach (var o in fn.Outputs) roots.Add(new FunctionOutputView(o));
                SgOptimize.Run(fn.Body, roots);
            }
            SgOptimize.Run(ctx.Statements, assigns);

            // FormatFunction returns "...}\n"; the extra "\n" makes a blank
            // line between functions and between the last function and main().
            // Empty when no functions, leaving the template's existing
            // newline before void main() intact.
            var functions = new StringBuilder();
            foreach (var fn in ctx.Functions) functions.Append(FormatFunction(fn)).Append('\n');

            var body = new StringBuilder();
            foreach (var s in ctx.Statements) body.Append("    ").Append(s.Format()).Append('\n');
            foreach (var a in assigns)        body.Append("    ").Append(a.Format()).Append('\n');
            string bodyStr = body.ToString().TrimEnd('\n');

            return template
                .Replace("${MATERIAL_STRUCT}", FormatMaterialStruct(g))
                .Replace("${FUNCTIONS}", functions.ToString())
                .Replace("${MAIN_BODY}", bodyStr);
        }

        // Wraps a function output so SgOptimize can treat its Rhs as a live
        // root (DCE keeps anything reachable from it).
        class FunctionOutputView : SgOptimize.IAssignView
        {
            readonly SgFunctionOutput _o;
            public FunctionOutputView(SgFunctionOutput o) { _o = o; }
            public SgRhs Rhs { get => _o.Rhs; set => _o.Rhs = value; }
        }

        // MaterialFS struct: one field per declared property, in declaration
        // order. Texture properties get the @texture2D annotation. Graphs
        // with no exposed properties still get a single placeholder field
        // (the engine compiler requires at least one).
        static string FormatMaterialStruct(SgGraph g)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@material");
            sb.AppendLine("struct MaterialFS {");
            if (g.Properties.Count == 0)
            {
                sb.AppendLine("    vec4 _placeholder;");
            }
            else
            {
                foreach (var p in g.Properties)
                {
                    if (p.Type == SgType.Texture2D)
                    {
                        sb.AppendLine("    @texture2D");
                        sb.Append("    uint ").Append(p.FieldName).AppendLine(";");
                    }
                    else if (p.Type == SgType.SamplerState)
                    {
                        // engine uses a default sampler — skip
                    }
                    else
                    {
                        sb.Append("    ").Append(SgTypes.Glsl(p.Type)).Append(' ')
                          .Append(p.FieldName).AppendLine(";");
                    }
                }
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        static string FormatFunction(SgFunctionDef fn)
        {
            var sb = new StringBuilder();
            if (fn.StructName != null)
            {
                // struct definition + struct-returning function
                sb.Append("struct ").Append(fn.StructName).Append(" {\n");
                foreach (var o in fn.Outputs)
                    sb.Append("    ").Append(SgTypes.Glsl(o.Type)).Append(' ').Append(o.Name).Append(";\n");
                sb.Append("};\n\n");

                sb.Append(fn.StructName).Append(' ').Append(fn.Name).Append("() {\n");
                foreach (var s in fn.Body) sb.Append("    ").Append(s.Format()).Append('\n');
                var args = new string[fn.Outputs.Count];
                for (int i = 0; i < fn.Outputs.Count; i++) args[i] = fn.Outputs[i].Rhs.ToGlsl();
                sb.Append("    return ").Append(fn.StructName).Append('(').Append(string.Join(", ", args)).Append(");\n");
                sb.Append("}\n");
            }
            else
            {
                // single value-returning function
                var o = fn.Outputs[0];
                sb.Append(SgTypes.Glsl(o.Type)).Append(' ').Append(fn.Name).Append("() {\n");
                foreach (var s in fn.Body) sb.Append("    ").Append(s.Format()).Append('\n');
                sb.Append("    return ").Append(o.Rhs.ToGlsl()).Append(";\n");
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        // Lower a SurfaceDescription block into an Assign. Falls back to the
        // provided literal RHS when the block has no input.
        static Assign LowerSurface(SgGraph g, LowerCtx ctx, string blockName,
                                   string symbol, SgType targetT, SgRhs fallbackRhs)
        {
            var block = g.FindFragmentBlock("SurfaceDescription." + blockName);
            if (block == null || block.SlotByNum.Count == 0)
                return new Assign { Symbol = symbol, Type = targetT, Rhs = fallbackRhs };

            int numId = 0;
            foreach (var k in block.SlotByNum.Keys) { numId = k; break; }
            var v = ctx.Input(block.Id, numId);

            // Coerce at the SgRhs level when we can; fall back to a Raw wrap.
            var rhs = CoerceRhs(v.Rhs, v.Type, targetT);
            return new Assign { Symbol = symbol, Type = targetT, Rhs = rhs };
        }

        // Public entry point used by SubGraphNodeHandler when coercing a
        // subgraph output to its declared SubGraphOutputNode slot type.
        public static SgRhs CoerceRhsForReturn(SgRhs r, SgType from, SgType to) => CoerceRhs(r, from, to);

        // Type-coercion at the structured level. Mirrors SgTypes.Coerce but
        // produces an SgRhs — so the optimizer can keep folding through.
        static SgRhs CoerceRhs(SgRhs r, SgType from, SgType to)
        {
            if (from == to) return r;
            // float → vec_n: broadcast via constructor.
            if (from == SgType.Float)
            {
                if (to == SgType.Vec2 || to == SgType.Vec3 || to == SgType.Vec4)
                    return Sg.Call(SgTypes.Glsl(to), r);
            }
            // vec_n → smaller via swizzle, → larger via constructor with 0 padding.
            int rf = SgTypes.Rank(from), rt = SgTypes.Rank(to);
            if (rf > 0 && rt > 0)
            {
                if (rt < rf) return Sg.Swizzle(r, "xyzw".Substring(0, rt));
                if (rt > rf)
                {
                    var args = new SgRhs[1 + (rt - rf)];
                    args[0] = r;
                    for (int i = 1; i < args.Length; i++) args[i] = Sg.Lit(0.0);
                    return Sg.Call(SgTypes.Glsl(to), args);
                }
            }
            // Fallback — let the GLSL coercer produce a string and wrap.
            return Sg.Raw(SgTypes.Coerce(r.ToGlsl(), from, to));
        }

        // ===== templates =======================================================

        // The body block is what changes per shader; everything around it (vs
        // outputs, fs inputs, lighting calls, MaterialFS) is fixed for a given
        // variant. As we add features (alpha clip, transparency, skinned, …)
        // we pick from a small set of templates rather than branching the
        // single one.
        const string UNLIT_TEMPLATE =
@"@vs
out vec2 v_uv;
out vec2 v_uv1;
flat out uint v_material_offset;
out vec3 v_normal;

void main() {
    VertexInstanceData v = unpack_instance_data();
    vec4 world_pos = v.model * vec4(v.pos, 1.0);
    gl_Position = hz_scene.view_proj * world_pos;
    v_uv = v.uv;
    v_uv1 = v.uv1;
    v_material_offset = v.material_offset;
    v_normal = normalize((v.model * vec4(v.normal, 0.0)).xyz);
}
@end

@fs
in vec2 v_uv;
in vec2 v_uv1;
flat in uint v_material_offset;
in vec3 v_normal;
out vec4 frag_color;

${MATERIAL_STRUCT}
${FUNCTIONS}void main() {
    MaterialFS mat = MaterialFS_unpack(v_material_offset);
${MAIN_BODY}
    frag_color = vec4(base_color, 1.0);
}
@end
";

        const string LIT_TEMPLATE =
@"@vs
#define HZ_RENDERER_LIGHTING_CLUSTERS
out vec3 v_world_pos;
out vec3 v_normal;
out vec2 v_uv;
out vec2 v_uv1;
flat out uint v_material_offset;
out vec3 v_tangent;
out float v_bitangent_sign;
out float v_view_depth;

void main() {
    VertexInstanceData v = unpack_instance_data();
    vec4 world_pos = v.model * vec4(v.pos, 1.0);
    gl_Position = hz_scene.view_proj * world_pos;
    v_world_pos = world_pos.xyz;
    v_normal = normalize((v.model * vec4(v.normal, 0.0)).xyz);
    v_tangent = normalize((v.model * vec4(v.tangent, 0.0)).xyz);
    v_bitangent_sign = v.bitangent_sign;
    v_uv = v.uv;
    v_uv1 = v.uv1;
    v_material_offset = v.material_offset;
    v_view_depth = -(hz_scene.view * world_pos).z;
}
@end

@fs
#define HZ_RENDERER_SHADOWS
#define HZ_RENDERER_IBL
#define HZ_RENDERER_LIGHTING_CLUSTERS
in vec3 v_world_pos;
in vec3 v_normal;
in vec2 v_uv;
in vec2 v_uv1;
flat in uint v_material_offset;
in vec3 v_tangent;
in float v_bitangent_sign;
in float v_view_depth;
out vec4 frag_color;

${MATERIAL_STRUCT}
${FUNCTIONS}void main() {
    MaterialFS mat = MaterialFS_unpack(v_material_offset);
${MAIN_BODY}
    vec3 N = normalize(v_normal);

    SurfaceInput surface = SurfaceInput(base_color, metallic, smoothness);
    LightingInput li = LightingInput(v_world_pos, N, hz_scene.camera_pos);
    float shadow_factor = calculate_shadow(v_world_pos);
    vec3 lit = pbr_directional_lighting(surface, li, hz_scene.lights,
                                        shadow_factor, hz_shadow.shadow_color);

    HZ_CLUSTER_LOOP_BEGIN(v_view_depth)
        lit += pbr_point_light(surface, li, light);
    HZ_CLUSTER_LOOP_END

    if (hz_scene.skybox_material_offset != 0xFFFFFFFFu) {
        uint sky_off = hz_scene.skybox_material_offset;
        float intensity = uintBitsToFloat(hz_materials.data[sky_off + 4u]);
        uint cubemap_idx = hz_materials.data[sky_off + 5u];
        lit += ibl_ambient(surface, li, hz_scene.lights, cubemap_idx, intensity);
    }

    frag_color = vec4(lit, 1.0);
}
@end
";
    }
}
