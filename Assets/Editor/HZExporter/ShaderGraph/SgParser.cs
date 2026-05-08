using System;
using System.Collections.Generic;

namespace HZExporter.ShaderGraph
{
    // .shadergraph (multi-object JSON) → SgGraph. The parser is the *only*
    // thing that touches the raw JSON shape; every downstream stage operates on
    // the typed model in SgGraph.cs.
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
            var graphData = FindGraphData(raw);

            ResolveVariant(g, raw, graphData);
            CollectBlockContext(graphData, "m_VertexContext", g.VertexBlockIds);
            CollectBlockContext(graphData, "m_FragmentContext", g.FragmentBlockIds);
            ParseSlots(g, raw);
            ParseNodes(g, raw);
            ParseEdges(g, graphData);

            return g;
        }

        // ----- helpers -------------------------------------------------------

        // GraphData is the unique top-level object that owns the vertex/fragment
        // contexts.
        static Dictionary<string, object> FindGraphData(Dictionary<string, Dictionary<string, object>> raw)
        {
            foreach (var kv in raw)
                if (kv.Value.ContainsKey("m_VertexContext")) return kv.Value;
            throw new Exception("no GraphData object in shadergraph");
        }

        static void ResolveVariant(SgGraph g,
                                   Dictionary<string, Dictionary<string, object>> raw,
                                   Dictionary<string, object> graphData)
        {
            string subTargetId = null;
            if (TryGetIdList(graphData, "m_ActiveTargets", out var targetIds))
            {
                foreach (var tid in targetIds)
                {
                    if (!raw.TryGetValue(tid, out var target)) continue;
                    if (target.TryGetValue("m_ActiveSubTarget", out var subRef)
                        && subRef is Dictionary<string, object> subDict
                        && subDict.TryGetValue("m_Id", out var subIdObj)
                        && subIdObj is string sid)
                    {
                        subTargetId = sid;
                    }
                }
            }
            if (subTargetId == null || !raw.TryGetValue(subTargetId, out var subTarget)) return;
            string subType = subTarget.TryGetValue("m_Type", out var t) ? t as string : "";
            if (subType.Contains("UniversalUnlitSubTarget")) g.Variant = SgVariant.Unlit;
            else if (subType.Contains("UniversalLitSubTarget")) g.Variant = SgVariant.Lit;
        }

        static void ParseSlots(SgGraph g, Dictionary<string, Dictionary<string, object>> raw)
        {
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (!IsSlotType(type)) continue;

                var s = new SgSlot
                {
                    Id = kv.Key,
                    Type = ShortTypeName(type),
                };
                if (kv.Value.TryGetValue("m_Id", out var nid) && nid is double nidD) s.NumId = (int)nidD;
                if (kv.Value.TryGetValue("m_ShaderOutputName", out var on)) s.OutputName = on as string;
                if (kv.Value.TryGetValue("m_DisplayName", out var dn)) s.DisplayName = dn as string;
                if (kv.Value.TryGetValue("m_SlotType", out var st) && st is double stD) s.IsOutput = (int)stD == 1;

                // m_Value is the user-typed inspector literal; m_DefaultValue is
                // the slot's compile-time fallback (usually identity/zero). We
                // read m_Value — that's where the actual graph data lives.
                if (kv.Value.TryGetValue("m_Value", out var v)) s.Value = v;
                else if (kv.Value.TryGetValue("m_DefaultValue", out var dv)) s.Value = dv;

                g.Slots[s.Id] = s;
            }
        }

        static void ParseNodes(SgGraph g, Dictionary<string, Dictionary<string, object>> raw)
        {
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (!IsNodeType(type)) continue;

                var node = new SgNode
                {
                    Id = kv.Key,
                    Type = ShortTypeName(type),
                    Raw = kv.Value,
                };
                if (kv.Value.TryGetValue("m_SerializedDescriptor", out var sd))
                    node.SerializedDescriptor = sd as string;

                if (TryGetIdList(kv.Value, "m_Slots", out var sids))
                {
                    foreach (var sid in sids)
                    {
                        if (!g.Slots.TryGetValue(sid, out var slot)) continue;
                        node.SlotByNum[slot.NumId] = sid;
                        if (!string.IsNullOrEmpty(slot.DisplayName))
                            node.SlotByName[slot.DisplayName] = slot.NumId;
                    }
                }
                g.Nodes[node.Id] = node;
            }
        }

        static void ParseEdges(SgGraph g, Dictionary<string, object> graphData)
        {
            if (!graphData.TryGetValue("m_Edges", out var edgesObj)) return;
            if (!(edgesObj is List<object> edgesList)) return;
            foreach (var eo in edgesList)
            {
                if (!(eo is Dictionary<string, object> ed)) continue;
                var e = new SgEdge();
                ParseSlotRef(ed, "m_OutputSlot", out e.OutputNodeId, out e.OutputSlotIdNum);
                ParseSlotRef(ed, "m_InputSlot",  out e.InputNodeId,  out e.InputSlotIdNum);
                g.Edges.Add(e);
            }
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
            fq.EndsWith("MaterialSlot");

        static bool IsNodeType(string fq) =>
            !string.IsNullOrEmpty(fq) && ShortTypeName(fq).EndsWith("Node");

        static string ShortTypeName(string fq)
        {
            int dot = fq.LastIndexOf('.');
            return dot >= 0 ? fq.Substring(dot + 1) : fq;
        }
    }
}
