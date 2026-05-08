using System;
using System.Collections.Generic;

namespace HZExporter.ShaderGraph
{
    // ===== values & contexts ===================================================

    // A value flowing along an edge during lowering. The Rhs is either an
    // SgRhsRef (when the producer was emitted to a temp) or an inline literal /
    // raw expression (for slot defaults).
    struct SgValue
    {
        public SgRhs Rhs;
        public SgType Type;
        public bool IsTemp;
        public string Glsl => Rhs.ToGlsl();   // convenience for callers that want the rendered form

        public static SgValue Temp(string name, SgType t) =>
            new SgValue { Rhs = Sg.Ref(name, t), Type = t, IsTemp = true };
        public static SgValue Inline(SgRhs rhs, SgType t) =>
            new SgValue { Rhs = rhs, Type = t, IsTemp = false };
    }

    // Owns the running statement list and the per-output-slot temp cache. Each
    // node's output is emitted at most once — subsequent reads return the
    // cached temp.
    class LowerCtx
    {
        public readonly SgGraph Graph;
        public readonly List<SgStatement> Statements = new List<SgStatement>();
        public readonly List<SgFunctionDef> Functions = new List<SgFunctionDef>();

        readonly Dictionary<string, SgValue> _cache = new Dictionary<string, SgValue>();
        int _tempCounter;

        public LowerCtx(SgGraph g) { Graph = g; }

        public SgValue Output(string nodeId, int outSlotNum)
        {
            string key = nodeId + ":" + outSlotNum;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            if (!Graph.Nodes.TryGetValue(nodeId, out var node))
                throw new Exception($"unknown node '{nodeId}'");

            var handler = SgNodes.Lookup(node.Type);
            var v = handler.Lower(node, outSlotNum, this);
            _cache[key] = v;
            return v;
        }

        public SgValue Input(string nodeId, int inSlotNum)
        {
            var edge = Graph.FindEdgeIntoSlot(nodeId, inSlotNum);
            if (edge != null) return Output(edge.OutputNodeId, edge.OutputSlotIdNum);

            if (!Graph.Nodes.TryGetValue(nodeId, out var node)
                || !node.SlotByNum.TryGetValue(inSlotNum, out var slotId)
                || !Graph.Slots.TryGetValue(slotId, out var slot))
                throw new Exception($"slot {nodeId}:{inSlotNum} cannot be resolved");

            return SgLiterals.From(slot);
        }

        public SgValue InputByName(SgNode node, string slotName)
        {
            if (!node.SlotByName.TryGetValue(slotName, out int num))
                throw new Exception($"node '{node.Type}' has no input slot named '{slotName}'");
            return Input(node.Id, num);
        }

        public SgValue EmitTemp(SgType t, SgRhs rhs)
        {
            string name = "_n" + (_tempCounter++);
            Statements.Add(new SgStatement { Name = name, Type = t, Rhs = rhs });
            return SgValue.Temp(name, t);
        }

        // Emits a temp whose GLSL type is a custom name (e.g. struct type) —
        // bypasses the SgType→GLSL mapping. Returns an SgValue carrying a Ref
        // to the temp; downstream consumers swizzle into it via SgRhsSwizzle
        // (which formats `<base>.<field>`).
        public SgValue EmitStructTemp(string structTypeName, SgRhs rhs)
        {
            string name = "_n" + (_tempCounter++);
            Statements.Add(new SgStatement { Name = name, Type = SgType.Unknown, TypeName = structTypeName, Rhs = rhs });
            return SgValue.Temp(name, SgType.Unknown);
        }

        // Per-instance cache for SubGraphNode call sites — first output query
        // emits the call statement for the whole subgraph; subsequent queries
        // reuse the same call temp, just under a different field-access. The
        // cached SgFunctionDef tells us whether to swizzle into the temp
        // (struct-return) or use the temp directly (single-output).
        public readonly Dictionary<string, (SgValue temp, SgFunctionDef fn)> SubGraphCallTemps =
            new Dictionary<string, (SgValue, SgFunctionDef)>();
    }

    // ===== slot-default literals ==============================================

    // Convert a slot's stored m_Value into an SgValue. Foldable types (scalar/
    // vec) become SgRhsLit; non-foldable (mat4) fall back to SgRhsRaw with the
    // string form so codegen still works but the optimizer skips them.
    static class SgLiterals
    {
        public static SgValue From(SgSlot slot)
        {
            var t = slot.StaticType();
            if (t == SgType.Unknown)
            {
                // DynamicValue/DynamicVector default to scalar — see SgRhs.cs
                // commentary on the Matrix4x4 backing field.
                if (slot.Type == "DynamicValueMaterialSlot" || slot.Type == "DynamicVectorMaterialSlot")
                    t = SgType.Float;
                else
                    throw new NotImplementedException($"slot type '{slot.Type}' has no default-literal rule");
            }

            if (TryParseConst(slot.Value, t, out var c))
                return SgValue.Inline(Sg.LitConst(c), t);

            // Non-foldable fallback (currently mat4 only).
            string raw = LiteralAtAsString(slot.Value, t);
            return SgValue.Inline(Sg.Raw(raw), t);
        }

        static bool TryParseConst(object value, SgType t, out SgConst c)
        {
            c = default;
            if (value is double d)
            {
                switch (t)
                {
                    case SgType.Float: c = SgConst.Scalar(d); return true;
                    case SgType.Vec2:  c = SgConst.Vec(t, d, d); return true;
                    case SgType.Vec3:  c = SgConst.Vec(t, d, d, d); return true;
                    case SgType.Vec4:  c = SgConst.Vec(t, d, d, d, d); return true;
                }
                return false;
            }
            if (value is Dictionary<string, object> obj)
            {
                if (obj.ContainsKey("e00"))
                {
                    switch (t)
                    {
                        case SgType.Float: c = SgConst.Scalar(F(obj, "e00")); return true;
                        case SgType.Vec2:  c = SgConst.Vec(t, F(obj, "e00"), F(obj, "e01")); return true;
                        case SgType.Vec3:  c = SgConst.Vec(t, F(obj, "e00"), F(obj, "e01"), F(obj, "e02")); return true;
                        case SgType.Vec4:  c = SgConst.Vec(t, F(obj, "e00"), F(obj, "e01"), F(obj, "e02"), F(obj, "e03")); return true;
                    }
                    return false;   // mat4 → not const
                }
                if (obj.ContainsKey("x"))
                {
                    // x/y/z/w storage — usable as Float (read .x) or as a vec_n.
                    switch (t)
                    {
                        case SgType.Float: c = SgConst.Scalar(F(obj, "x")); return true;
                        case SgType.Vec2:  c = SgConst.Vec(t, F(obj, "x"), F(obj, "y")); return true;
                        case SgType.Vec3:  c = SgConst.Vec(t, F(obj, "x"), F(obj, "y"), F(obj, "z")); return true;
                        case SgType.Vec4:  c = SgConst.Vec(t, F(obj, "x"), F(obj, "y"), F(obj, "z"), F(obj, "w")); return true;
                    }
                    return false;
                }
                if (obj.ContainsKey("r"))
                {
                    switch (t)
                    {
                        case SgType.Float: c = SgConst.Scalar(F(obj, "r")); return true;
                        case SgType.Vec3:  c = SgConst.Vec(t, F(obj, "r"), F(obj, "g"), F(obj, "b")); return true;
                        case SgType.Vec4:  c = SgConst.Vec(t, F(obj, "r"), F(obj, "g"), F(obj, "b"), F(obj, "a")); return true;
                    }
                    return false;
                }
            }
            return false;
        }

        // Fallback for the rare non-foldable cases (mat4 slot defaults).
        static string LiteralAtAsString(object value, SgType t)
        {
            if (value is Dictionary<string, object> obj && obj.ContainsKey("e00") && t == SgType.Mat4)
            {
                var sb = new System.Text.StringBuilder("mat4(");
                bool first = true;
                for (int c = 0; c < 4; c++)
                    for (int r = 0; r < 4; r++)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(SgTypes.FormatFloat(F(obj, $"e{r}{c}")));
                    }
                sb.Append(")");
                return sb.ToString();
            }
            return Zero(t);
        }

        static string Zero(SgType t)
        {
            switch (t)
            {
                case SgType.Float: return "0.0";
                case SgType.Vec2: return "vec2(0.0)";
                case SgType.Vec3: return "vec3(0.0)";
                case SgType.Vec4: return "vec4(0.0)";
                case SgType.Mat4: return "mat4(0.0)";
                case SgType.Bool: return "false";
            }
            return "0.0";
        }

        static double F(Dictionary<string, object> o, string k) =>
            o.TryGetValue(k, out var v) && v is double d ? d : 0.0;
    }

    // ===== node handlers ======================================================

    abstract class NodeHandler
    {
        public abstract SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx);
    }

    // Pattern-driven handler: data + a builder lambda. Most arithmetic and
    // builtin nodes fit this — see the registry below.
    class PatternHandler : NodeHandler
    {
        public string[] InputNames;
        public Func<SgRhs[], SgRhs> Build;
        public Func<SgType[], SgType> OutType;

        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            var values = new SgValue[InputNames.Length];
            for (int i = 0; i < InputNames.Length; i++)
                values[i] = ctx.InputByName(node, InputNames[i]);

            var rhsArr = new SgRhs[values.Length];
            var tArr = new SgType[values.Length];
            for (int i = 0; i < values.Length; i++) { rhsArr[i] = values[i].Rhs; tArr[i] = values[i].Type; }

            var rhs = Build(rhsArr);
            var outT = OutType(tArr);
            return ctx.EmitTemp(outT, rhs);
        }
    }

    // ----- specific custom handlers ----------------------------------------

    class UVNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            int channel = 0;
            if (node.Raw.TryGetValue("m_OutputChannel", out var ch) && ch is double d) channel = (int)d;
            string varying;
            switch (channel)
            {
                case 0: varying = "v_uv"; break;
                case 1: varying = "v_uv1"; break;
                default: throw new NotImplementedException($"UV channel {channel} not wired");
            }
            // Varyings aren't constants — wrap as Raw so the optimizer leaves
            // them alone but DCE still sees no inbound refs.
            return ctx.EmitTemp(SgType.Vec4, Sg.Raw($"vec4({varying}, 0.0, 0.0)"));
        }
    }

    // Vector1Node forwards the literal in its X port; no statement of its own.
    class Vector1NodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            return ctx.InputByName(node, "X");
        }
    }

    // SubGraphNode: load the referenced .shadersubgraph, lower every declared
    // output into a single GLSL function on the same file, and emit one call
    // statement at the call site. Subsequent reads of any output of the same
    // SubGraphNode hit the cache and become field-access swizzles into the
    // call temp.
    //
    // Two output forms:
    //   • 1 output  → simple value-return function, call temp typed as the
    //                 output type. Consumers reference the temp directly.
    //   • 2+ outputs → struct-return function. Call temp has the struct type;
    //                  consumers reference fields via swizzle.
    class SubGraphNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (!node.SlotByNum.TryGetValue(outSlotNum, out var slotId))
                throw new System.Exception($"SubGraphNode: invalid output slot {outSlotNum}");
            var requestedSlot = ctx.Graph.Slots[slotId];
            var requestedType = requestedSlot.StaticType();
            if (requestedType == SgType.Unknown)
                throw new System.NotImplementedException($"SubGraphNode output '{requestedSlot.OutputName}' has unmapped slot type '{requestedSlot.Type}'");

            // First touch of this SubGraphNode → emit the call site once.
            if (!ctx.SubGraphCallTemps.TryGetValue(node.Id, out var entry))
            {
                entry = EmitCallSite(node, ctx);
                ctx.SubGraphCallTemps[node.Id] = entry;
            }

            // For single-output functions, the call temp IS the value.
            // For multi-output struct-return, swizzle into the field.
            if (entry.fn.StructName == null)
                return new SgValue { Rhs = entry.temp.Rhs, Type = requestedType, IsTemp = false };
            return new SgValue
            {
                Rhs = Sg.Swizzle(entry.temp.Rhs, requestedSlot.OutputName),
                Type = requestedType,
                IsTemp = false,
            };
        }

        (SgValue temp, SgFunctionDef fn) EmitCallSite(SgNode node, LowerCtx ctx)
        {
            string guid = ExtractGuid(node.Raw);
            if (string.IsNullOrEmpty(guid))
                throw new System.Exception("SubGraphNode missing m_SerializedSubGraph guid");

            var sg = SgSubGraphLoader.Load(guid);
            string fnName = SgSubGraphLoader.FunctionName(guid);

            // Reuse an already-emitted function for this subgraph (if some
            // other SubGraphNode instance already created it).
            SgFunctionDef fn = null;
            foreach (var f in ctx.Functions) if (f.Name == fnName) { fn = f; break; }
            if (fn == null)
                fn = BuildFunction(node, ctx, sg, guid, fnName);

            // Emit `<retType> _nN = fnName();` — struct-typed for multi-output,
            // primitive-typed for single-output.
            var callRhs = Sg.Call(fnName);
            var temp = fn.StructName != null
                ? ctx.EmitStructTemp(fn.StructName, callRhs)
                : ctx.EmitTemp(fn.Outputs[0].Type, callRhs);
            return (temp, fn);
        }

        // Lowers every declared output of `node` (in the order the
        // SubGraphNode declares them) into an SgFunctionOutput. The function
        // body comes from one shared LowerCtx so outputs share intermediate
        // statements when the optimizer runs over them.
        SgFunctionDef BuildFunction(SgNode node, LowerCtx ctx, SgGraph sg, string guid, string fnName)
        {
            SgNode sgOut = null;
            foreach (var n in sg.Nodes.Values) if (n.Type == "SubGraphOutputNode") { sgOut = n; break; }
            if (sgOut == null) throw new System.Exception($"subgraph {guid} has no SubGraphOutputNode");

            var fnCtx = new LowerCtx(sg);
            var outputs = new List<SgFunctionOutput>();

            // Iterate the SubGraphNode's slots in their numeric m_Id order so
            // the function's outputs/struct fields match the user's
            // declaration order.
            var slotNums = new List<int>(node.SlotByNum.Keys);
            slotNums.Sort();
            foreach (var num in slotNums)
            {
                var slot = ctx.Graph.Slots[node.SlotByNum[num]];
                if (!slot.IsOutput) continue;   // SubGraphNode property inputs (not yet supported)

                int sgInSlot = -1;
                foreach (var skv in sgOut.SlotByNum)
                    if (sg.Slots[skv.Value].OutputName == slot.OutputName) { sgInSlot = skv.Key; break; }
                if (sgInSlot == -1)
                    throw new System.Exception($"subgraph {guid} has no output named '{slot.OutputName}'");

                var v = fnCtx.Input(sgOut.Id, sgInSlot);
                var coerced = SgCodegen.CoerceRhsForReturn(v.Rhs, v.Type, slot.StaticType());
                outputs.Add(new SgFunctionOutput
                {
                    Name = slot.OutputName,
                    Type = slot.StaticType(),
                    Rhs = coerced,
                });
            }

            // Promote nested SubGraphs' functions into the parent shader.
            ctx.Functions.AddRange(fnCtx.Functions);

            var fn = new SgFunctionDef
            {
                Name = fnName,
                StructName = outputs.Count > 1 ? fnName + "_Outputs" : null,
                Outputs = outputs,
                Body = fnCtx.Statements,
            };
            ctx.Functions.Add(fn);
            return fn;
        }

        // m_SerializedSubGraph is a JSON string nested inside the outer JSON:
        //   { subGraph: { fileID, guid, type } }
        static string ExtractGuid(Dictionary<string, object> raw)
        {
            if (!raw.TryGetValue("m_SerializedSubGraph", out var v) || !(v is string s)) return null;
            int i = 0;
            try
            {
                var inner = TinyJson.ParseObject(s, ref i);
                if (inner.TryGetValue("subGraph", out var sub) && sub is Dictionary<string, object> sd
                    && sd.TryGetValue("guid", out var g) && g is string gs)
                    return gs;
            }
            catch { }
            return null;
        }
    }

    // Resolves Unity asset GUIDs to .shadersubgraph paths and parses them. Also
    // mints stable function names from the subgraph file name.
    static class SgSubGraphLoader
    {
        static readonly Dictionary<string, SgGraph> _graphs = new Dictionary<string, SgGraph>();
        static readonly Dictionary<string, string> _fnNames = new Dictionary<string, string>();

        public static SgGraph Load(string guid)
        {
            if (_graphs.TryGetValue(guid, out var g)) return g;
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                throw new System.Exception($"subgraph guid '{guid}' not resolvable to a path");
            string text = System.IO.File.ReadAllText(path);
            var parsed = SgParser.Parse(text);
            _graphs[guid] = parsed;
            return parsed;
        }

        public static string FunctionName(string guid)
        {
            if (_fnNames.TryGetValue(guid, out var n)) return n;
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            n = string.IsNullOrEmpty(path)
                ? "subgraph_" + guid.Substring(0, 8)
                : "subgraph_" + Sanitize(System.IO.Path.GetFileNameWithoutExtension(path));
            _fnNames[guid] = n;
            return n;
        }

        static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }
    }

    class SplitNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (outSlotNum < 1 || outSlotNum > 4)
                throw new Exception($"SplitNode: invalid output slot {outSlotNum}");
            var inV = ctx.Input(node.Id, 0);
            int rank = SgTypes.Rank(inV.Type);
            if (rank == 0) throw new NotImplementedException($"Split input type {inV.Type}");

            string suffix; switch (outSlotNum) { case 1: suffix = "x"; break; case 2: suffix = "y"; break;
                                                  case 3: suffix = "z"; break; case 4: suffix = "w"; break;
                                                  default: throw new Exception(); }

            if (outSlotNum > rank) return ctx.EmitTemp(SgType.Float, Sg.Lit(0.0));
            if (rank == 1)        return ctx.EmitTemp(SgType.Float, inV.Rhs);
            return ctx.EmitTemp(SgType.Float, Sg.Swizzle(inV.Rhs, suffix));
        }
    }

    // ===== registry ===========================================================

    static class SgNodes
    {
        static readonly Dictionary<string, NodeHandler> _handlers
            = new Dictionary<string, NodeHandler>();

        static SgNodes()
        {
            // ----- pattern-driven (data-only) ---------------------------------
            Pattern("MultiplyNode",    a => Sg.Bin("*", a[0], a[1]),                       PromoteAB, "A", "B");
            Pattern("AddNode",         a => Sg.Bin("+", a[0], a[1]),                       PromoteAB, "A", "B");
            Pattern("SubtractNode",    a => Sg.Bin("-", a[0], a[1]),                       PromoteAB, "A", "B");
            Pattern("DivideNode",      a => Sg.Bin("/", a[0], a[1]),                       PromoteAB, "A", "B");

            Pattern("AbsoluteNode",    a => Sg.Call("abs",   a[0]),                        SameAsIn,  "In");
            Pattern("FloorNode",       a => Sg.Call("floor", a[0]),                        SameAsIn,  "In");
            Pattern("CeilingNode",     a => Sg.Call("ceil",  a[0]),                        SameAsIn,  "In");
            Pattern("RoundNode",       a => Sg.Call("round", a[0]),                        SameAsIn,  "In");
            Pattern("FractionNode",    a => Sg.Call("fract", a[0]),                        SameAsIn,  "In");
            Pattern("SignNode",        a => Sg.Call("sign",  a[0]),                        SameAsIn,  "In");
            Pattern("SquareRootNode",  a => Sg.Call("sqrt",  a[0]),                        SameAsIn,  "In");
            Pattern("OneMinusNode",    a => Sg.Bin("-", Sg.Lit(1.0), a[0]),                SameAsIn,  "In");
            Pattern("NegateNode",      a => Sg.Neg(a[0]),                                  SameAsIn,  "In");
            Pattern("SaturateNode",    a => Sg.Call("clamp", a[0], Sg.Lit(0.0), Sg.Lit(1.0)), SameAsIn, "In");

            // ----- custom -----------------------------------------------------
            _handlers["UVNode"]       = new UVNodeHandler();
            _handlers["SplitNode"]    = new SplitNodeHandler();
            _handlers["Vector1Node"]  = new Vector1NodeHandler();
            _handlers["SubGraphNode"] = new SubGraphNodeHandler();
        }

        public static NodeHandler Lookup(string nodeType)
        {
            if (_handlers.TryGetValue(nodeType, out var h)) return h;
            throw new NotImplementedException($"no handler registered for '{nodeType}'");
        }

        static void Pattern(string nodeType, Func<SgRhs[], SgRhs> build,
                            Func<SgType[], SgType> outType, params string[] inputs)
        {
            _handlers[nodeType] = new PatternHandler
            {
                InputNames = inputs,
                Build = build,
                OutType = outType,
            };
        }

        static SgType PromoteAB(SgType[] inT) => SgTypes.Promote(inT[0], inT[1]);
        static SgType SameAsIn(SgType[] inT) => inT[0];
    }
}
