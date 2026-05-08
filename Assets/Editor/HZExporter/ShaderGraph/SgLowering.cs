using System;
using System.Collections.Generic;

namespace HZExporter.ShaderGraph
{
    // ===== values & contexts ===================================================

    // A value flowing along an edge during lowering. `Glsl` is either a temp
    // name (`_n3`) when the producer was emitted to a statement, or an inline
    // expression (`vec3(0.5, 0.5, 0.5)`) for slot-default literals.
    struct SgValue
    {
        public string Glsl;
        public SgType Type;
        public bool IsTemp;
        public SgValue(string g, SgType t, bool isTemp) { Glsl = g; Type = t; IsTemp = isTemp; }
        public static SgValue Literal(string g, SgType t) => new SgValue(g, t, false);
        public static SgValue Temp(string name, SgType t) => new SgValue(name, t, true);
    }

    // Owns the running statement list and the per-output-slot temp cache. Each
    // node's output is emitted at most once — subsequent reads return the cached
    // temp.
    class LowerCtx
    {
        public readonly SgGraph Graph;
        public readonly List<string> Statements = new List<string>();

        // key: "<nodeId>:<outSlotNum>" → cached SgValue (always a temp)
        readonly Dictionary<string, SgValue> _cache = new Dictionary<string, SgValue>();
        int _tempCounter;

        public LowerCtx(SgGraph g) { Graph = g; }

        // Get the value at the given output slot, lowering the producing node
        // (and its inputs) if necessary.
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

        // Get the value flowing *into* the given input slot. Either follows the
        // edge to its producer, or returns the slot's literal default.
        public SgValue Input(string nodeId, int inSlotNum)
        {
            var edge = Graph.FindEdgeIntoSlot(nodeId, inSlotNum);
            if (edge != null) return Output(edge.OutputNodeId, edge.OutputSlotIdNum);

            // unconnected → literal from slot's m_Value
            if (!Graph.Nodes.TryGetValue(nodeId, out var node)
                || !node.SlotByNum.TryGetValue(inSlotNum, out var slotId)
                || !Graph.Slots.TryGetValue(slotId, out var slot))
                throw new Exception($"slot {nodeId}:{inSlotNum} cannot be resolved");

            return SgLiterals.From(slot);
        }

        // Like Input, but for a slot identified by its display name on the node
        // (e.g. "A", "B", "In"). PatternHandler uses this.
        public SgValue InputByName(SgNode node, string slotName)
        {
            if (!node.SlotByName.TryGetValue(slotName, out int num))
                throw new Exception($"node '{node.Type}' has no input slot named '{slotName}'");
            return Input(node.Id, num);
        }

        // Append `<glsl-type> _nN = <expr>;` to the statement list and return a
        // temp value referencing the new variable.
        public SgValue EmitTemp(SgType t, string expr)
        {
            string name = "_n" + (_tempCounter++);
            Statements.Add($"{SgTypes.Glsl(t)} {name} = {expr};");
            return SgValue.Temp(name, t);
        }
    }

    // ===== slot-default literals ==============================================

    // Convert a slot's stored m_Value into an SgValue. We know the slot's
    // static type (Color → Vec3, Vector1 → Float, …) for typed slots; for
    // DynamicValue/DynamicVector we default to scalar (read e00 only) — see
    // commentary in SgValue.From below.
    static class SgLiterals
    {
        public static SgValue From(SgSlot slot)
        {
            var staticT = slot.StaticType();
            if (staticT != SgType.Unknown)
                return new SgValue(LiteralAt(slot.Value, staticT), staticT, false);

            // DynamicValue / DynamicVector: the inspector backing field is a
            // Matrix4x4 used as universal storage. Without an explicit
            // resolved-type hint we default to scalar (e00). The other rows
            // (e10..e33) are stale leftover entries from when the slot was a
            // different shape — they must be ignored.
            if (slot.Type == "DynamicValueMaterialSlot" || slot.Type == "DynamicVectorMaterialSlot")
                return new SgValue(LiteralAt(slot.Value, SgType.Float), SgType.Float, false);

            throw new NotImplementedException($"slot type '{slot.Type}' has no default-literal rule");
        }

        // Format `value` as a GLSL literal of `t`.
        public static string LiteralAt(object value, SgType t)
        {
            if (value == null) return Zero(t);

            if (value is double d)
            {
                if (t == SgType.Float) return SgTypes.FormatFloat(d);
                if (t == SgType.Vec2) return $"vec2({SgTypes.FormatFloat(d)})";
                if (t == SgType.Vec3) return $"vec3({SgTypes.FormatFloat(d)})";
                if (t == SgType.Vec4) return $"vec4({SgTypes.FormatFloat(d)})";
            }
            if (value is Dictionary<string, object> obj)
            {
                // Matrix4x4-style storage (eRC) — used by DynamicValue and by
                // mat4 slots. Read only the entries the resolved type calls
                // for; ignore stale leftovers.
                if (obj.ContainsKey("e00"))
                {
                    if (t == SgType.Float) return F(obj, "e00");
                    if (t == SgType.Vec2) return $"vec2({F(obj, "e00")}, {F(obj, "e01")})";
                    if (t == SgType.Vec3) return $"vec3({F(obj, "e00")}, {F(obj, "e01")}, {F(obj, "e02")})";
                    if (t == SgType.Vec4) return $"vec4({F(obj, "e00")}, {F(obj, "e01")}, {F(obj, "e02")}, {F(obj, "e03")})";
                    if (t == SgType.Mat4) return Mat4Literal(obj);
                }
                // Vector{2,3,4}-style storage (x,y,z,w).
                if (obj.ContainsKey("x"))
                {
                    if (t == SgType.Vec2) return $"vec2({F(obj, "x")}, {F(obj, "y")})";
                    if (t == SgType.Vec3) return $"vec3({F(obj, "x")}, {F(obj, "y")}, {F(obj, "z")})";
                    if (t == SgType.Vec4) return $"vec4({F(obj, "x")}, {F(obj, "y")}, {F(obj, "z")}, {F(obj, "w")})";
                }
                // Color-style storage (r,g,b,a).
                if (obj.ContainsKey("r"))
                {
                    if (t == SgType.Vec3) return $"vec3({F(obj, "r")}, {F(obj, "g")}, {F(obj, "b")})";
                    if (t == SgType.Vec4) return $"vec4({F(obj, "r")}, {F(obj, "g")}, {F(obj, "b")}, {F(obj, "a")})";
                }
            }
            return Zero(t);
        }

        static string Mat4Literal(Dictionary<string, object> obj)
        {
            // GLSL mat4 ctor takes 16 floats in column-major order.
            var sb = new System.Text.StringBuilder("mat4(");
            bool first = true;
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(F(obj, $"e{r}{c}"));
                }
            sb.Append(")");
            return sb.ToString();
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

        static string F(Dictionary<string, object> o, string k) =>
            o.TryGetValue(k, out var v) && v is double d ? SgTypes.FormatFloat(d) : "0.0";
    }

    // ===== node handlers ======================================================

    abstract class NodeHandler
    {
        // Lower a single output slot of this node to an SgValue. Implementations
        // typically resolve their inputs via ctx.Input(...) and append a
        // statement via ctx.EmitTemp(...).
        public abstract SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx);
    }

    // Most arithmetic/builtin nodes follow the same recipe: pull N inputs,
    // substitute them into a string template, emit a temp. The output type is
    // a function of the input types.
    class PatternHandler : NodeHandler
    {
        public string Pattern;                                  // e.g. "({A} + {B})"
        public string[] InputNames;                             // ["A","B"]
        public Func<SgType[], SgType> OutType;                  // produces output type from input types

        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            var values = new SgValue[InputNames.Length];
            for (int i = 0; i < InputNames.Length; i++)
                values[i] = ctx.InputByName(node, InputNames[i]);

            var inTypes = new SgType[values.Length];
            for (int i = 0; i < values.Length; i++) inTypes[i] = values[i].Type;
            var outT = OutType(inTypes);

            string expr = Pattern;
            for (int i = 0; i < InputNames.Length; i++)
                expr = expr.Replace("{" + InputNames[i] + "}", values[i].Glsl);

            return ctx.EmitTemp(outT, expr);
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
                // Engine VertexInstanceData only exposes uv0 and uv1 today —
                // uv2/uv3 would require a new vertex layout.
                default: throw new NotImplementedException($"UV channel {channel} not wired");
            }
            return ctx.EmitTemp(SgType.Vec4, $"vec4({varying}, 0.0, 0.0)");
        }
    }

    // Vector1Node is a constant-holder: its only behaviour is to expose the
    // value typed into its X input port at its Out slot. We forward the input
    // verbatim — no statement of our own — so consumers see the literal directly.
    class Vector1NodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            return ctx.InputByName(node, "X");
        }
    }

    class SplitNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            // Slots: 0=In, 1=R, 2=G, 3=B, 4=A.
            if (outSlotNum < 1 || outSlotNum > 4)
                throw new Exception($"SplitNode: invalid output slot {outSlotNum}");

            var inV = ctx.Input(node.Id, 0);
            string suffix;
            switch (outSlotNum) { case 1: suffix = "x"; break; case 2: suffix = "y"; break;
                                  case 3: suffix = "z"; break; case 4: suffix = "w"; break;
                                  default: throw new Exception(); }

            // Channels beyond the input's rank produce 0 (Unity behaviour).
            int rank = SgTypes.Rank(inV.Type);
            int channel = outSlotNum;
            if (rank == 0) throw new NotImplementedException($"Split input type {inV.Type}");
            if (channel > rank) return ctx.EmitTemp(SgType.Float, "0.0");
            if (rank == 1) return ctx.EmitTemp(SgType.Float, inV.Glsl);
            return ctx.EmitTemp(SgType.Float, $"{inV.Glsl}.{suffix}");
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
            // GLSL handles vec/scalar broadcasting natively, so most arithmetic
            // patterns are just the operator. Output type = vector promotion.
            Pattern("MultiplyNode", "({A} * {B})", PromoteAB, "A", "B");
            Pattern("AddNode",      "({A} + {B})", PromoteAB, "A", "B");
            Pattern("SubtractNode", "({A} - {B})", PromoteAB, "A", "B");
            Pattern("DivideNode",   "({A} / {B})", PromoteAB, "A", "B");

            // Unary math nodes — output type matches input.
            Pattern("AbsoluteNode", "abs({In})",                  SameAsIn, "In");
            Pattern("FloorNode",    "floor({In})",                SameAsIn, "In");
            Pattern("CeilingNode",  "ceil({In})",                 SameAsIn, "In");
            Pattern("RoundNode",    "round({In})",                SameAsIn, "In");
            Pattern("FractionNode", "fract({In})",                SameAsIn, "In");
            Pattern("SignNode",     "sign({In})",                 SameAsIn, "In");
            Pattern("OneMinusNode", "(1.0 - {In})",               SameAsIn, "In");
            Pattern("NegateNode",   "(-{In})",                    SameAsIn, "In");
            Pattern("SaturateNode", "clamp({In}, 0.0, 1.0)",      SameAsIn, "In");
            Pattern("SquareRootNode", "sqrt({In})",               SameAsIn, "In");

            // ----- custom -----------------------------------------------------
            _handlers["UVNode"]      = new UVNodeHandler();
            _handlers["SplitNode"]   = new SplitNodeHandler();
            _handlers["Vector1Node"] = new Vector1NodeHandler();
        }

        public static NodeHandler Lookup(string nodeType)
        {
            if (_handlers.TryGetValue(nodeType, out var h)) return h;
            throw new NotImplementedException($"no handler registered for '{nodeType}'");
        }

        static void Pattern(string nodeType, string pattern, Func<SgType[], SgType> outType, params string[] inputs)
        {
            _handlers[nodeType] = new PatternHandler
            {
                Pattern = pattern,
                InputNames = inputs,
                OutType = outType,
            };
        }

        // Output-type rules used by patterns.
        static SgType PromoteAB(SgType[] inT) => SgTypes.Promote(inT[0], inT[1]);
        static SgType SameAsIn(SgType[] inT) => inT[0];
    }
}
