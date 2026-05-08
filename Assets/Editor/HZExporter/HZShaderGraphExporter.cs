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
    // Step 1 scope: unlit subtarget with no edges. BaseColor defaults are baked
    // into the fragment as a literal. A placeholder MaterialFS field is always
    // emitted because the engine's shader compiler requires at least one.
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
                default:
                    throw new NotImplementedException($"shader graph variant '{g.Variant}' not yet supported");
            }
            return sb.ToString();
        }

        static void EmitUnlit(SgGraph g, StringBuilder sb)
        {
            // ----- @vs ---------------------------------------------------------
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

            // ----- @fs ---------------------------------------------------------
            sb.AppendLine("@fs");
            sb.AppendLine("in vec2 v_uv;");
            sb.AppendLine("flat in uint v_material_offset;");
            sb.AppendLine("in vec3 v_normal;");
            sb.AppendLine("out vec4 frag_color;");
            sb.AppendLine();

            // MaterialFS — engine requires at least one field. No exposed graph
            // properties yet, so emit a placeholder.
            sb.AppendLine("@material");
            sb.AppendLine("struct MaterialFS {");
            sb.AppendLine("    vec4 _placeholder;");
            sb.AppendLine("};");
            sb.AppendLine();

            // BaseColor expression (constant from slot default for step 1).
            string baseColorExpr = ResolveSurfaceExpression(g, "BaseColor", "vec3", "vec3(0.5, 0.5, 0.5)");

            sb.AppendLine("void main() {");
            sb.AppendLine("    MaterialFS mat = MaterialFS_unpack(v_material_offset);");
            sb.AppendLine($"    vec3 base_color = {baseColorExpr};");
            sb.AppendLine("    frag_color = vec4(base_color, 1.0);");
            sb.AppendLine("}");
            sb.AppendLine("@end");
        }

        // Resolve a SurfaceDescription block's value as a GLSL expression of the
        // requested type. Step 1 only handles unconnected slots (= literal default).
        static string ResolveSurfaceExpression(SgGraph g, string blockName, string glslType, string fallback)
        {
            var block = g.FindFragmentBlock("SurfaceDescription." + blockName);
            if (block == null) return fallback;

            // Block has exactly one slot — its default value is the constant.
            string slotId = block.SlotIds.Count > 0 ? block.SlotIds[0] : null;
            if (slotId == null || !g.Slots.TryGetValue(slotId, out var slot))
                return fallback;

            // Edge connected? Step 1 doesn't traverse — bail to fallback so we
            // notice unhandled cases.
            if (g.IsSlotConnected(slotId))
                throw new NotImplementedException($"slot {blockName} has an incoming edge — not supported in step 1");

            return SgValue.LiteralAsGlsl(slot.DefaultValue, glslType, fallback);
        }
    }

    // ===== graph model =========================================================

    enum SgVariant { Unknown, Unlit, Lit }

    class SgBlock
    {
        public string Id;
        public string SerializedDescriptor; // e.g. "SurfaceDescription.BaseColor"
        public List<string> SlotIds = new List<string>();
    }

    class SgSlot
    {
        public string Id;
        public string Type;          // e.g. "ColorRGBMaterialSlot"
        public string OutputName;    // m_ShaderOutputName
        public object DefaultValue;  // Dictionary<string,object> for vec, double for scalar
    }

    class SgEdge
    {
        public string OutputNodeId;
        public string OutputSlotId; // numeric m_SlotId on the output node
        public string InputNodeId;
        public string InputSlotId;
    }

    class SgGraph
    {
        public SgVariant Variant;
        public List<string> VertexBlockIds = new List<string>();
        public List<string> FragmentBlockIds = new List<string>();
        public Dictionary<string, SgBlock> Blocks = new Dictionary<string, SgBlock>();
        public Dictionary<string, SgSlot> Slots = new Dictionary<string, SgSlot>();
        public List<SgEdge> Edges = new List<SgEdge>();

        public SgBlock FindFragmentBlock(string serializedDescriptor)
        {
            foreach (var id in FragmentBlockIds)
                if (Blocks.TryGetValue(id, out var b) && b.SerializedDescriptor == serializedDescriptor)
                    return b;
            return null;
        }

        public bool IsSlotConnected(string slotId)
        {
            foreach (var e in Edges)
                if (e.InputSlotId == slotId) return true;
            return false;
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

            // find GraphData (the only object with m_VertexContext)
            Dictionary<string, object> graphData = null;
            foreach (var kv in raw)
                if (kv.Value.ContainsKey("m_VertexContext")) { graphData = kv.Value; break; }
            if (graphData == null) throw new Exception("no GraphData object in shadergraph");

            // active subtarget -> variant
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

            // contexts
            CollectBlockContext(graphData, "m_VertexContext", g.VertexBlockIds);
            CollectBlockContext(graphData, "m_FragmentContext", g.FragmentBlockIds);

            // blocks + slots
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (type.EndsWith(".BlockNode"))
                {
                    var b = new SgBlock { Id = kv.Key };
                    if (kv.Value.TryGetValue("m_SerializedDescriptor", out var sd))
                        b.SerializedDescriptor = sd as string;
                    if (TryGetIdList(kv.Value, "m_Slots", out var sids))
                        b.SlotIds.AddRange(sids);
                    g.Blocks[b.Id] = b;
                }
                else if (type.Contains("MaterialSlot") || type.EndsWith(".DynamicValueMaterialSlot"))
                {
                    var s = new SgSlot { Id = kv.Key, Type = ShortTypeName(type) };
                    if (kv.Value.TryGetValue("m_ShaderOutputName", out var on)) s.OutputName = on as string;
                    if (kv.Value.TryGetValue("m_DefaultValue", out var dv)) s.DefaultValue = dv;
                    else if (kv.Value.TryGetValue("m_Value", out var v)) s.DefaultValue = v;
                    g.Slots[s.Id] = s;
                }
            }

            // edges
            if (graphData.TryGetValue("m_Edges", out var edgesObj) && edgesObj is List<object> edgesList)
            {
                foreach (var eo in edgesList)
                {
                    if (!(eo is Dictionary<string, object> ed)) continue;
                    var e = new SgEdge();
                    if (ed.TryGetValue("m_OutputSlot", out var os) && os is Dictionary<string, object> od)
                    {
                        if (od.TryGetValue("m_Node", out var nr) && nr is Dictionary<string, object> nd
                            && nd.TryGetValue("m_Id", out var nid)) e.OutputNodeId = nid as string;
                        if (od.TryGetValue("m_SlotId", out var sid)) e.OutputSlotId = Convert.ToString(sid, CultureInfo.InvariantCulture);
                    }
                    if (ed.TryGetValue("m_InputSlot", out var ins) && ins is Dictionary<string, object> id_)
                    {
                        if (id_.TryGetValue("m_Node", out var nr) && nr is Dictionary<string, object> nd
                            && nd.TryGetValue("m_Id", out var nid)) e.InputNodeId = nid as string;
                        if (id_.TryGetValue("m_SlotId", out var sid)) e.InputSlotId = Convert.ToString(sid, CultureInfo.InvariantCulture);
                    }
                    g.Edges.Add(e);
                }
            }

            return g;
        }

        static void CollectBlockContext(Dictionary<string, object> graphData, string key, List<string> outIds)
        {
            if (!graphData.TryGetValue(key, out var ctxObj)) return;
            if (!(ctxObj is Dictionary<string, object> ctx)) return;
            if (!ctx.TryGetValue("m_Blocks", out var blocksObj)) return;
            if (!(blocksObj is List<object> blocks)) return;
            foreach (var b in blocks)
            {
                if (b is Dictionary<string, object> bd && bd.TryGetValue("m_Id", out var id) && id is string s)
                    outIds.Add(s);
            }
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

        static string ShortTypeName(string fq)
        {
            int dot = fq.LastIndexOf('.');
            return dot >= 0 ? fq.Substring(dot + 1) : fq;
        }
    }

    // ===== value -> glsl literal ==============================================

    static class SgValue
    {
        public static string LiteralAsGlsl(object value, string glslType, string fallback)
        {
            if (value == null) return fallback;

            if (value is double d)
            {
                if (glslType == "float") return Fmt(d);
                if (glslType == "vec3") return $"vec3({Fmt(d)})";
                if (glslType == "vec4") return $"vec4({Fmt(d)})";
            }
            if (value is Dictionary<string, object> obj)
            {
                if (glslType == "vec3" && obj.ContainsKey("x"))
                    return $"vec3({V(obj, "x")}, {V(obj, "y")}, {V(obj, "z")})";
                if (glslType == "vec4" && obj.ContainsKey("x"))
                    return $"vec4({V(obj, "x")}, {V(obj, "y")}, {V(obj, "z")}, {V(obj, "w")})";
                if (glslType == "vec4" && obj.ContainsKey("r"))
                    return $"vec4({V(obj, "r")}, {V(obj, "g")}, {V(obj, "b")}, {V(obj, "a")})";
                if (glslType == "vec3" && obj.ContainsKey("r"))
                    return $"vec3({V(obj, "r")}, {V(obj, "g")}, {V(obj, "b")})";
            }
            return fallback;
        }

        static string V(Dictionary<string, object> o, string k) =>
            o.TryGetValue(k, out var v) && v is double d ? Fmt(d) : "0.0";

        static string Fmt(double d)
        {
            // always include a decimal so GLSL parses as float
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
