using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace HZExporter
{
    // Translates Unity ShaderGraph (.shadergraph) files into hz-engine-v5 .hz.glsl
    // shader sources. Built up incrementally — see ShaderGraphExport/ for the
    // staged test files (1_*, 2_*, 3_*).
    //
    // Coverage so far:
    //   - Unlit / Lit URP subtargets (no transparency, no alpha clip yet).
    //   - Surface block defaults when unconnected.
    //   - Edge traversal with node handlers: UVNode, MultiplyNode.
    //   - vec/mat literal slot defaults; type coercion between scalar/vec/mat4.
    //
    // Anything outside that throws loudly so we add coverage explicitly when a
    // new graph hits the wall.
    public static class HZShaderGraphExporter
    {
        public static void ExportFile(string shadergraphPath, string outDir)
        {
            string text = File.ReadAllText(shadergraphPath);
            var graph = SgParser.Parse(text);
            string name = Path.GetFileNameWithoutExtension(shadergraphPath);
            string glsl = Emit(graph, name);
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, name + ".hz.glsl");
            File.WriteAllText(outPath, glsl);
            Debug.Log($"[HZ-SG] {name} -> {outPath} (variant={graph.Variant})");
        }

        // ----- emission --------------------------------------------------------

        static string Emit(SgGraph g, string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Generated from {name}.shadergraph by HZShaderGraphExporter");
            sb.AppendLine("#include \"renderer.glsl\"");
            sb.AppendLine();

            switch (g.Variant)
            {
                case SgVariant.Unlit: EmitUnlit(g, sb); break;
                case SgVariant.Lit:   EmitLit(g, sb); break;
                default:
                    throw new NotImplementedException($"shader graph variant '{g.Variant}' not yet supported");
            }
            return sb.ToString();
        }

        static void EmitUnlit(SgGraph g, StringBuilder sb)
        {
            sb.AppendLine("@vs");
            sb.AppendLine("out vec2 v_uv;");
            sb.AppendLine("flat out uint v_material_offset;");
            sb.AppendLine("out vec3 v_normal;");
            sb.AppendLine();
            sb.AppendLine("void main() {");
            sb.AppendLine("    VertexInstanceData v = unpack_instance_data();");
            sb.AppendLine("    vec4 world_pos = v.model * vec4(v.pos, 1.0);");
            sb.AppendLine("    gl_Position = hz_scene.view_proj * world_pos;");
            sb.AppendLine("    v_uv = v.uv;");
            sb.AppendLine("    v_material_offset = v.material_offset;");
            sb.AppendLine("    v_normal = normalize((v.model * vec4(v.normal, 0.0)).xyz);");
            sb.AppendLine("}");
            sb.AppendLine("@end");
            sb.AppendLine();

            sb.AppendLine("@fs");
            sb.AppendLine("in vec2 v_uv;");
            sb.AppendLine("flat in uint v_material_offset;");
            sb.AppendLine("in vec3 v_normal;");
            sb.AppendLine("out vec4 frag_color;");
            sb.AppendLine();
            sb.AppendLine("@material");
            sb.AppendLine("struct MaterialFS {");
            sb.AppendLine("    vec4 _placeholder;");
            sb.AppendLine("};");
            sb.AppendLine();

            var eval = new SgEvaluator(g);
            string baseColor = eval.ResolveSurface("BaseColor", "vec3", "vec3(0.5, 0.5, 0.5)");

            sb.AppendLine("void main() {");
            sb.AppendLine("    MaterialFS mat = MaterialFS_unpack(v_material_offset);");
            sb.AppendLine($"    vec3 base_color = {baseColor};");
            sb.AppendLine("    frag_color = vec4(base_color, 1.0);");
            sb.AppendLine("}");
            sb.AppendLine("@end");
        }

        static void EmitLit(SgGraph g, StringBuilder sb)
        {
            sb.AppendLine("@vs");
            sb.AppendLine("#define HZ_RENDERER_LIGHTING_CLUSTERS");
            sb.AppendLine("out vec3 v_world_pos;");
            sb.AppendLine("out vec3 v_normal;");
            sb.AppendLine("out vec2 v_uv;");
            sb.AppendLine("flat out uint v_material_offset;");
            sb.AppendLine("out vec3 v_tangent;");
            sb.AppendLine("out float v_bitangent_sign;");
            sb.AppendLine("out float v_view_depth;");
            sb.AppendLine();
            sb.AppendLine("void main() {");
            sb.AppendLine("    VertexInstanceData v = unpack_instance_data();");
            sb.AppendLine("    vec4 world_pos = v.model * vec4(v.pos, 1.0);");
            sb.AppendLine("    gl_Position = hz_scene.view_proj * world_pos;");
            sb.AppendLine("    v_world_pos = world_pos.xyz;");
            sb.AppendLine("    v_normal = normalize((v.model * vec4(v.normal, 0.0)).xyz);");
            sb.AppendLine("    v_tangent = normalize((v.model * vec4(v.tangent, 0.0)).xyz);");
            sb.AppendLine("    v_bitangent_sign = v.bitangent_sign;");
            sb.AppendLine("    v_uv = v.uv;");
            sb.AppendLine("    v_material_offset = v.material_offset;");
            sb.AppendLine("    v_view_depth = -(hz_scene.view * world_pos).z;");
            sb.AppendLine("}");
            sb.AppendLine("@end");
            sb.AppendLine();

            sb.AppendLine("@fs");
            sb.AppendLine("#define HZ_RENDERER_SHADOWS");
            sb.AppendLine("#define HZ_RENDERER_IBL");
            sb.AppendLine("#define HZ_RENDERER_LIGHTING_CLUSTERS");
            sb.AppendLine("in vec3 v_world_pos;");
            sb.AppendLine("in vec3 v_normal;");
            sb.AppendLine("in vec2 v_uv;");
            sb.AppendLine("flat in uint v_material_offset;");
            sb.AppendLine("in vec3 v_tangent;");
            sb.AppendLine("in float v_bitangent_sign;");
            sb.AppendLine("in float v_view_depth;");
            sb.AppendLine("out vec4 frag_color;");
            sb.AppendLine();
            sb.AppendLine("@material");
            sb.AppendLine("struct MaterialFS {");
            sb.AppendLine("    vec4 _placeholder;");
            sb.AppendLine("};");
            sb.AppendLine();

            var eval = new SgEvaluator(g);
            string baseColor = eval.ResolveSurface("BaseColor", "vec3", "vec3(0.5, 0.5, 0.5)");
            string metallic  = eval.ResolveSurface("Metallic",  "float", "0.0");
            string smoothness= eval.ResolveSurface("Smoothness","float", "0.5");

            sb.AppendLine("void main() {");
            sb.AppendLine("    MaterialFS mat = MaterialFS_unpack(v_material_offset);");
            sb.AppendLine($"    vec3 base_color = {baseColor};");
            sb.AppendLine($"    float metallic = {metallic};");
            sb.AppendLine($"    float smoothness = {smoothness};");
            sb.AppendLine("    vec3 N = normalize(v_normal);");
            sb.AppendLine();
            sb.AppendLine("    SurfaceInput surface = SurfaceInput(base_color, metallic, smoothness);");
            sb.AppendLine("    LightingInput li = LightingInput(v_world_pos, N, hz_scene.camera_pos);");
            sb.AppendLine("    float shadow_factor = calculate_shadow(v_world_pos);");
            sb.AppendLine("    vec3 lit = pbr_directional_lighting(surface, li, hz_scene.lights,");
            sb.AppendLine("                                        shadow_factor, hz_shadow.shadow_color);");
            sb.AppendLine();
            sb.AppendLine("    HZ_CLUSTER_LOOP_BEGIN(v_view_depth)");
            sb.AppendLine("        lit += pbr_point_light(surface, li, light);");
            sb.AppendLine("    HZ_CLUSTER_LOOP_END");
            sb.AppendLine();
            sb.AppendLine("    if (hz_scene.skybox_material_offset != 0xFFFFFFFFu) {");
            sb.AppendLine("        uint sky_off = hz_scene.skybox_material_offset;");
            sb.AppendLine("        float intensity = uintBitsToFloat(hz_materials.data[sky_off + 4u]);");
            sb.AppendLine("        uint cubemap_idx = hz_materials.data[sky_off + 5u];");
            sb.AppendLine("        lit += ibl_ambient(surface, li, hz_scene.lights, cubemap_idx, intensity);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    frag_color = vec4(lit, 1.0);");
            sb.AppendLine("}");
            sb.AppendLine("@end");
        }
    }

    // ===== expression evaluator ===============================================

    // Walks the graph backwards from a SurfaceDescription block slot, recursing
    // through node handlers, and returns a GLSL expression string of the requested
    // type. Unconnected slots fall back to their literal default value.
    class SgEvaluator
    {
        readonly SgGraph G;

        public SgEvaluator(SgGraph g) { G = g; }

        public string ResolveSurface(string blockName, string glslType, string fallback)
        {
            var block = G.FindFragmentBlock("SurfaceDescription." + blockName);
            if (block == null) return fallback;

            // Block has exactly one input slot — whatever feeds it is BaseColor/etc.
            if (block.SlotByNum.Count == 0) return fallback;
            int numId = 0;
            foreach (var k in block.SlotByNum.Keys) { numId = k; break; }
            return ResolveInput(block.Id, numId, glslType, fallback);
        }

        // Resolve the value flowing into (nodeId, numeric slot id). If an edge
        // feeds it, recurse into the producer. Otherwise emit the slot's literal
        // default.
        string ResolveInput(string nodeId, int numSlotId, string glslType, string fallback)
        {
            foreach (var e in G.Edges)
            {
                if (e.InputNodeId == nodeId && e.InputSlotIdNum == numSlotId)
                {
                    var srcExpr = EmitNodeOutput(e.OutputNodeId, e.OutputSlotIdNum);
                    return Coerce(srcExpr, glslType);
                }
            }

            // unconnected -> literal default
            if (!G.Nodes.TryGetValue(nodeId, out var node))
                return fallback;
            if (!node.SlotByNum.TryGetValue(numSlotId, out var slotId))
                return fallback;
            if (!G.Slots.TryGetValue(slotId, out var slot))
                return fallback;
            string lit = SgValue.LiteralAsGlsl(slot.DefaultValue, glslType, null);
            return lit ?? fallback;
        }

        // Emit a typed expression for an output slot of a non-block node.
        SgExpr EmitNodeOutput(string nodeId, int numSlotId)
        {
            if (!G.Nodes.TryGetValue(nodeId, out var node))
                throw new Exception($"unknown node {nodeId}");

            switch (node.Type)
            {
                case "UVNode": return EmitUV(node, numSlotId);
                case "MultiplyNode": return EmitMultiply(node, numSlotId);
                case "SplitNode": return EmitSplit(node, numSlotId);
                default:
                    throw new NotImplementedException($"node type '{node.Type}' not supported (id={nodeId})");
            }
        }

        SgExpr EmitSplit(SgNode node, int numSlotId)
        {
            // Split has slot 0 = In (vector), 1..4 = R/G/B/A scalars.
            if (numSlotId < 1 || numSlotId > 4)
                throw new Exception($"SplitNode: requested non-channel slot {numSlotId}");

            var inExpr = ResolveInputExpr(node.Id, 0);

            // For scalar input every channel is the value itself; otherwise pick
            // the .x/.y/.z/.w component (with zero-fill for missing dims).
            string suffix = numSlotId switch { 1 => "x", 2 => "y", 3 => "z", 4 => "w", _ => "x" };
            switch (inExpr.Type)
            {
                case "float": return new SgExpr(inExpr.Glsl, "float");
                case "vec2":
                    if (numSlotId <= 2) return new SgExpr($"({inExpr.Glsl}).{suffix}", "float");
                    return new SgExpr("0.0", "float"); // missing dims → 0
                case "vec3":
                    if (numSlotId <= 3) return new SgExpr($"({inExpr.Glsl}).{suffix}", "float");
                    return new SgExpr("0.0", "float");
                case "vec4":
                    return new SgExpr($"({inExpr.Glsl}).{suffix}", "float");
                default:
                    throw new NotImplementedException($"Split: unsupported input type {inExpr.Type}");
            }
        }

        SgExpr EmitUV(SgNode node, int numSlotId)
        {
            // UVNode has a single Vector4 output slot. m_OutputChannel selects
            // which UV set: 0=uv, 1=uv1, 2=uv2, 3=uv3. We currently only wire 0.
            int channel = 0;
            if (node.Raw.TryGetValue("m_OutputChannel", out var ch) && ch is double d)
                channel = (int)d;
            if (channel != 0)
                throw new NotImplementedException($"UVNode channel {channel} not wired (only uv0 supported)");
            return new SgExpr("vec4(v_uv, 0.0, 0.0)", "vec4");
        }

        SgExpr EmitMultiply(SgNode node, int numSlotId)
        {
            // Multiply has slots A=0, B=1, Out=2.
            if (numSlotId != 2)
                throw new Exception($"MultiplyNode: requested non-output slot {numSlotId}");

            // Resolve inputs without forcing a target type (use whatever the
            // upstream produces / the slot default declares).
            var a = ResolveInputExpr(node.Id, 0);
            var b = ResolveInputExpr(node.Id, 1);

            // GLSL: vec4 * mat4 = vec4 (row-vector * mat = vec4). We support a
            // few common cases here; expand as graphs demand.
            string outType;
            if (a.Type == "vec4" && b.Type == "mat4") outType = "vec4";
            else if (a.Type == b.Type) outType = a.Type; // componentwise
            else if (a.Type == "float") outType = b.Type;
            else if (b.Type == "float") outType = a.Type;
            else
                throw new NotImplementedException($"Multiply: unsupported combo {a.Type} * {b.Type}");

            return new SgExpr($"({a.Glsl} * {b.Glsl})", outType);
        }

        // Same as ResolveInput but returns an SgExpr (with type) instead of a
        // string already coerced to a target type.
        SgExpr ResolveInputExpr(string nodeId, int numSlotId)
        {
            foreach (var e in G.Edges)
                if (e.InputNodeId == nodeId && e.InputSlotIdNum == numSlotId)
                    return EmitNodeOutput(e.OutputNodeId, e.OutputSlotIdNum);

            // Unconnected -> read the slot default and infer its GLSL type.
            if (!G.Nodes.TryGetValue(nodeId, out var node)
                || !node.SlotByNum.TryGetValue(numSlotId, out var slotId)
                || !G.Slots.TryGetValue(slotId, out var slot))
                throw new Exception($"slot {nodeId}.{numSlotId}: cannot resolve default");

            string inferred = SgValue.InferType(slot);
            string lit = SgValue.LiteralAsGlsl(slot.DefaultValue, inferred, null);
            if (lit == null) throw new Exception($"slot {nodeId}.{numSlotId}: no literal for type {inferred}");
            return new SgExpr(lit, inferred);
        }

        // Coerce an expression into the requested GLSL type via swizzles or
        // constructor calls. Tight scope — extend as new combos arise.
        static string Coerce(SgExpr e, string target)
        {
            if (e.Type == target) return e.Glsl;
            if (target == "vec3" && e.Type == "vec4") return $"{e.Glsl}.rgb";
            if (target == "vec3" && e.Type == "float") return $"vec3({e.Glsl})";
            if (target == "vec4" && e.Type == "vec3") return $"vec4({e.Glsl}, 1.0)";
            if (target == "vec4" && e.Type == "float") return $"vec4({e.Glsl})";
            if (target == "float" && e.Type == "vec4") return $"{e.Glsl}.x";
            if (target == "float" && e.Type == "vec3") return $"{e.Glsl}.x";
            throw new NotImplementedException($"coerce {e.Type} -> {target}");
        }
    }

    struct SgExpr
    {
        public string Glsl;
        public string Type;
        public SgExpr(string g, string t) { Glsl = g; Type = t; }
    }

    // ===== graph model =========================================================

    enum SgVariant { Unknown, Unlit, Lit }

    class SgNode
    {
        public string Id;
        public string Type;                        // "BlockNode","UVNode","MultiplyNode",...
        public string SerializedDescriptor;        // BlockNode only
        public Dictionary<int, string> SlotByNum = new Dictionary<int, string>();
        public Dictionary<string, object> Raw;     // full raw object for type-specific fields
    }

    class SgBlock : SgNode { }

    class SgSlot
    {
        public string Id;
        public string Type;          // e.g. "ColorRGBMaterialSlot"
        public int NumId;            // node-local m_Id
        public string OutputName;
        public object DefaultValue;
    }

    class SgEdge
    {
        public string OutputNodeId;
        public int OutputSlotIdNum;
        public string InputNodeId;
        public int InputSlotIdNum;
    }

    class SgGraph
    {
        public SgVariant Variant;
        public List<string> VertexBlockIds = new List<string>();
        public List<string> FragmentBlockIds = new List<string>();
        public Dictionary<string, SgNode> Nodes = new Dictionary<string, SgNode>();
        public Dictionary<string, SgSlot> Slots = new Dictionary<string, SgSlot>();
        public List<SgEdge> Edges = new List<SgEdge>();

        public SgNode FindFragmentBlock(string serializedDescriptor)
        {
            foreach (var id in FragmentBlockIds)
                if (Nodes.TryGetValue(id, out var b) && b.SerializedDescriptor == serializedDescriptor)
                    return b;
            return null;
        }
    }

    // ===== parser ==============================================================

    static class SgParser
    {
        public static SgGraph Parse(string text)
        {
            var raw = new Dictionary<string, Dictionary<string, object>>();
            int i = 0;
            while (true)
            {
                TinyJson.SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] != '{')
                    throw new Exception($"expected '{{' at {i}, got '{text[i]}'");
                var obj = (Dictionary<string, object>)TinyJson.ParseValue(text, ref i);
                if (obj.TryGetValue("m_ObjectId", out var idObj) && idObj is string id)
                    raw[id] = obj;
            }

            var g = new SgGraph();

            // GraphData = the unique object with m_VertexContext.
            Dictionary<string, object> graphData = null;
            foreach (var kv in raw)
                if (kv.Value.ContainsKey("m_VertexContext")) { graphData = kv.Value; break; }
            if (graphData == null) throw new Exception("no GraphData object in shadergraph");

            // Active subtarget -> variant.
            string subTargetId = null;
            if (TryGetIdList(graphData, "m_ActiveTargets", out var targetIds))
            {
                foreach (var tid in targetIds)
                {
                    if (!raw.TryGetValue(tid, out var target)) continue;
                    if (target.TryGetValue("m_ActiveSubTarget", out var subRef) && subRef is Dictionary<string, object> subDict)
                        if (subDict.TryGetValue("m_Id", out var subIdObj) && subIdObj is string sid)
                            subTargetId = sid;
                }
            }
            if (subTargetId != null && raw.TryGetValue(subTargetId, out var subTarget))
            {
                string subType = subTarget.TryGetValue("m_Type", out var t) ? t as string : "";
                if (subType.Contains("UniversalUnlitSubTarget")) g.Variant = SgVariant.Unlit;
                else if (subType.Contains("UniversalLitSubTarget")) g.Variant = SgVariant.Lit;
            }

            CollectBlockContext(graphData, "m_VertexContext", g.VertexBlockIds);
            CollectBlockContext(graphData, "m_FragmentContext", g.FragmentBlockIds);

            // First pass: parse slots so we can resolve their numeric m_Id when
            // populating SlotByNum on each owning node.
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (IsSlotType(type))
                {
                    var s = new SgSlot { Id = kv.Key, Type = ShortTypeName(type) };
                    if (kv.Value.TryGetValue("m_Id", out var nid) && nid is double nidD) s.NumId = (int)nidD;
                    if (kv.Value.TryGetValue("m_ShaderOutputName", out var on)) s.OutputName = on as string;
                    // m_Value is the in-port literal the user typed in the
                    // inspector; m_DefaultValue is the slot's compile-time fallback
                    // (usually identity/zero). The graph's actual data lives in
                    // m_Value, so prefer it.
                    if (kv.Value.TryGetValue("m_Value", out var v)) s.DefaultValue = v;
                    else if (kv.Value.TryGetValue("m_DefaultValue", out var dv)) s.DefaultValue = dv;
                    g.Slots[s.Id] = s;
                }
            }

            // Second pass: nodes (including BlockNodes).
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (!IsNodeType(type)) continue;
                var node = type.EndsWith(".BlockNode") ? new SgBlock() : new SgNode();
                node.Id = kv.Key;
                node.Type = ShortTypeName(type);
                node.Raw = kv.Value;
                if (kv.Value.TryGetValue("m_SerializedDescriptor", out var sd))
                    node.SerializedDescriptor = sd as string;
                if (TryGetIdList(kv.Value, "m_Slots", out var sids))
                {
                    foreach (var sid in sids)
                        if (g.Slots.TryGetValue(sid, out var slot))
                            node.SlotByNum[slot.NumId] = sid;
                }
                g.Nodes[node.Id] = node;
            }

            // Edges.
            if (graphData.TryGetValue("m_Edges", out var edgesObj) && edgesObj is List<object> edgesList)
            {
                foreach (var eo in edgesList)
                {
                    if (!(eo is Dictionary<string, object> ed)) continue;
                    var e = new SgEdge();
                    ParseSlotRef(ed, "m_OutputSlot", out e.OutputNodeId, out e.OutputSlotIdNum);
                    ParseSlotRef(ed, "m_InputSlot",  out e.InputNodeId,  out e.InputSlotIdNum);
                    g.Edges.Add(e);
                }
            }

            return g;
        }

        static void ParseSlotRef(Dictionary<string, object> ed, string key, out string nodeId, out int slotNum)
        {
            nodeId = null; slotNum = 0;
            if (!ed.TryGetValue(key, out var v) || !(v is Dictionary<string, object> d)) return;
            if (d.TryGetValue("m_Node", out var nr) && nr is Dictionary<string, object> nd
                && nd.TryGetValue("m_Id", out var nid)) nodeId = nid as string;
            if (d.TryGetValue("m_SlotId", out var sid) && sid is double sd) slotNum = (int)sd;
        }

        static void CollectBlockContext(Dictionary<string, object> graphData, string key, List<string> outIds)
        {
            if (!graphData.TryGetValue(key, out var ctxObj)) return;
            if (!(ctxObj is Dictionary<string, object> ctx)) return;
            if (!ctx.TryGetValue("m_Blocks", out var blocksObj)) return;
            if (!(blocksObj is List<object> blocks)) return;
            foreach (var b in blocks)
                if (b is Dictionary<string, object> bd && bd.TryGetValue("m_Id", out var id) && id is string s)
                    outIds.Add(s);
        }

        static bool TryGetIdList(Dictionary<string, object> obj, string key, out List<string> ids)
        {
            ids = new List<string>();
            if (!obj.TryGetValue(key, out var v) || !(v is List<object> list)) return false;
            foreach (var item in list)
                if (item is Dictionary<string, object> d && d.TryGetValue("m_Id", out var idObj) && idObj is string s)
                    ids.Add(s);
            return true;
        }

        static bool IsSlotType(string fq) =>
            fq.EndsWith("MaterialSlot") || fq.EndsWith(".DynamicValueMaterialSlot");

        static bool IsNodeType(string fq)
        {
            if (string.IsNullOrEmpty(fq)) return false;
            // Anything UnityEditor.ShaderGraph.* ending in "Node" — heuristic but
            // works for the well-typed graph format.
            string sn = ShortTypeName(fq);
            return sn.EndsWith("Node");
        }

        static string ShortTypeName(string fq)
        {
            int dot = fq.LastIndexOf('.');
            return dot >= 0 ? fq.Substring(dot + 1) : fq;
        }
    }

    // ===== value -> glsl literal ==============================================

    static class SgValue
    {
        // Produce a GLSL literal of `glslType` for a slot value parsed from JSON.
        // Returns null if no clean conversion is available so the caller can
        // decide whether to use a fallback or error.
        public static string LiteralAsGlsl(object value, string glslType, string fallback)
        {
            if (value == null) return fallback;

            if (value is double d)
            {
                if (glslType == "float") return Fmt(d);
                if (glslType == "vec2")  return $"vec2({Fmt(d)})";
                if (glslType == "vec3")  return $"vec3({Fmt(d)})";
                if (glslType == "vec4")  return $"vec4({Fmt(d)})";
            }
            if (value is Dictionary<string, object> obj)
            {
                // DynamicValue inspector storage: Matrix4x4 used as a universal
                // backing field. When the slot resolves to a non-matrix type,
                // only the first row's entries are meaningful (e00, e01, e02,
                // e03) — the rest is stale leftover data and must be ignored.
                if (obj.ContainsKey("e00") && glslType != "mat4")
                {
                    if (glslType == "float") return F(obj, "e00");
                    if (glslType == "vec2")  return $"vec2({F(obj, "e00")}, {F(obj, "e01")})";
                    if (glslType == "vec3")  return $"vec3({F(obj, "e00")}, {F(obj, "e01")}, {F(obj, "e02")})";
                    if (glslType == "vec4")  return $"vec4({F(obj, "e00")}, {F(obj, "e01")}, {F(obj, "e02")}, {F(obj, "e03")})";
                }
                // mat4 (Unity stores row-major as e00..e33).
                if (glslType == "mat4" && obj.ContainsKey("e00"))
                {
                    var sb = new StringBuilder("mat4(");
                    bool first = true;
                    // GLSL mat4 ctor takes 16 floats in column-major order.
                    for (int c = 0; c < 4; c++)
                        for (int r = 0; r < 4; r++)
                        {
                            string key = $"e{r}{c}";
                            if (!first) sb.Append(", ");
                            first = false;
                            sb.Append(F(obj, key));
                        }
                    sb.Append(")");
                    return sb.ToString();
                }
                if (glslType == "vec3" && obj.ContainsKey("x"))
                    return $"vec3({F(obj, "x")}, {F(obj, "y")}, {F(obj, "z")})";
                if (glslType == "vec4" && obj.ContainsKey("x"))
                    return $"vec4({F(obj, "x")}, {F(obj, "y")}, {F(obj, "z")}, {F(obj, "w")})";
                if (glslType == "vec2" && obj.ContainsKey("x"))
                    return $"vec2({F(obj, "x")}, {F(obj, "y")})";
                if (glslType == "vec4" && obj.ContainsKey("r"))
                    return $"vec4({F(obj, "r")}, {F(obj, "g")}, {F(obj, "b")}, {F(obj, "a")})";
                if (glslType == "vec3" && obj.ContainsKey("r"))
                    return $"vec3({F(obj, "r")}, {F(obj, "g")}, {F(obj, "b")})";
            }
            return fallback;
        }

        // Best-effort GLSL type for an unconnected slot's default value.
        public static string InferType(SgSlot s)
        {
            switch (s.Type)
            {
                case "Vector1MaterialSlot": return "float";
                case "Vector2MaterialSlot": return "vec2";
                case "Vector3MaterialSlot":
                case "PositionMaterialSlot":
                case "NormalMaterialSlot":
                case "TangentMaterialSlot":
                case "ColorRGBMaterialSlot":
                    return "vec3";
                case "Vector4MaterialSlot":
                case "ColorRGBAMaterialSlot":
                    return "vec4";
                case "DynamicValueMaterialSlot":
                case "DynamicVectorMaterialSlot":
                    // DynamicValueMaterialSlot stores its inspector value in a
                    // Matrix4x4 regardless of the slot's *concrete* type — the
                    // real type is resolved at compile time from the connected
                    // neighbours. Without that resolver we default to scalar
                    // (read e00 only). The leftover entries in m_Value are stale
                    // and must not be baked. Revisit if a graph genuinely needs
                    // a vec/mat default here.
                    return "float";
            }
            throw new NotImplementedException($"slot type '{s.Type}' has no inferred GLSL type");
        }

        static string F(Dictionary<string, object> o, string k) =>
            o.TryGetValue(k, out var v) && v is double d ? Fmt(d) : "0.0";

        static string Fmt(double d)
        {
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("e") || s.Contains("E") ? s : s + ".0";
        }
    }

    // ===== tiny JSON parser ====================================================

    static class TinyJson
    {
        public static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new Exception("unexpected EOF");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        public static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new Exception($"expected ':' at {i}");
                i++;
                object value = ParseValue(s, ref i);
                d[key] = value;
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new Exception($"expected ',' or '}}' at {i}");
            }
        }

        public static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new Exception($"expected ',' or ']' at {i}");
            }
        }

        public static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new Exception($"expected '\"' at {i}");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            string hex = s.Substring(i, 4); i += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new Exception($"bad escape \\{esc}");
                    }
                }
                else sb.Append(c);
            }
            throw new Exception("unterminated string");
        }

        public static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-' || s[i] == '+') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-'))
                i++;
            string tok = s.Substring(start, i - start);
            return double.Parse(tok, CultureInfo.InvariantCulture);
        }

        public static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                throw new Exception($"expected '{lit}' at {i}");
            i += lit.Length;
        }
    }
}
