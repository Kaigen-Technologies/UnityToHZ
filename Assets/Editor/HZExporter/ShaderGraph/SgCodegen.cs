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

        // Walk the surface block we care about for this variant, lower their
        // graphs into one shared LowerCtx (so subgraphs are deduped across
        // blocks), then substitute the resulting statements + final assigns
        // into the template.
        static string EmitFromTemplate(SgGraph g, string template, bool isLit)
        {
            var ctx = new LowerCtx(g);
            var assigns = new List<string>();

            assigns.Add(LowerSurface(g, ctx, "BaseColor", "base_color", SgType.Vec3, "vec3(0.5, 0.5, 0.5)"));
            if (isLit)
            {
                assigns.Add(LowerSurface(g, ctx, "Metallic",   "metallic",   SgType.Float, "0.0"));
                assigns.Add(LowerSurface(g, ctx, "Smoothness", "smoothness", SgType.Float, "0.5"));
            }

            string body = string.Join("\n", FormatLines(ctx.Statements, assigns, indent: "    "));
            return template.Replace("${MAIN_BODY}", body);
        }

        // Lower one SurfaceDescription block into an assignment: `<glslType>
        // <symbol> = <expr>;`. Falls back to the literal default if the block
        // isn't present (some subtargets don't emit every block).
        static string LowerSurface(SgGraph g, LowerCtx ctx, string blockName,
                                   string symbol, SgType targetT, string fallback)
        {
            string glslT = SgTypes.Glsl(targetT);
            var block = g.FindFragmentBlock("SurfaceDescription." + blockName);
            if (block == null || block.SlotByNum.Count == 0)
                return $"{glslT} {symbol} = {fallback};";

            // The block has exactly one input slot — its first.
            int numId = 0;
            foreach (var k in block.SlotByNum.Keys) { numId = k; break; }

            var v = ctx.Input(block.Id, numId);
            string rhs = SgTypes.Coerce(v.Glsl, v.Type, targetT);
            return $"{glslT} {symbol} = {rhs};";
        }

        static IEnumerable<string> FormatLines(List<string> statements, List<string> assigns, string indent)
        {
            foreach (var s in statements) yield return indent + s;
            foreach (var a in assigns) yield return indent + a;
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
flat out uint v_material_offset;
out vec3 v_normal;

void main() {
    VertexInstanceData v = unpack_instance_data();
    vec4 world_pos = v.model * vec4(v.pos, 1.0);
    gl_Position = hz_scene.view_proj * world_pos;
    v_uv = v.uv;
    v_material_offset = v.material_offset;
    v_normal = normalize((v.model * vec4(v.normal, 0.0)).xyz);
}
@end

@fs
in vec2 v_uv;
flat in uint v_material_offset;
in vec3 v_normal;
out vec4 frag_color;

@material
struct MaterialFS {
    vec4 _placeholder;
};

void main() {
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
flat in uint v_material_offset;
in vec3 v_tangent;
in float v_bitangent_sign;
in float v_view_depth;
out vec4 frag_color;

@material
struct MaterialFS {
    vec4 _placeholder;
};

void main() {
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
