using System;
using System.Collections.Generic;
using System.Globalization;

namespace HZExporter.ShaderGraph
{
    // ===== variants & target ===================================================

    enum SgVariant { Unknown, Unlit, Lit }

    // ===== concrete types =====================================================

    // Engine-side GLSL types we care about. Shader Graph's static slot types map
    // to these directly; DynamicValue/DynamicVector slots resolve to one of
    // these during lowering based on what flows in.
    enum SgType
    {
        Unknown,
        Float,
        Vec2,
        Vec3,
        Vec4,
        Mat4,
        Bool,
    }

    static class SgTypes
    {
        public static string Glsl(SgType t)
        {
            switch (t)
            {
                case SgType.Float: return "float";
                case SgType.Vec2:  return "vec2";
                case SgType.Vec3:  return "vec3";
                case SgType.Vec4:  return "vec4";
                case SgType.Mat4:  return "mat4";
                case SgType.Bool:  return "bool";
            }
            throw new Exception($"no GLSL name for type {t}");
        }

        // Numeric rank for vector promotion. Mat4/Bool are excluded — promotion
        // involving them is handled by callers.
        public static int Rank(SgType t)
        {
            switch (t)
            {
                case SgType.Float: return 1;
                case SgType.Vec2:  return 2;
                case SgType.Vec3:  return 3;
                case SgType.Vec4:  return 4;
            }
            return 0;
        }

        public static SgType FromRank(int r)
        {
            switch (r)
            {
                case 1: return SgType.Float;
                case 2: return SgType.Vec2;
                case 3: return SgType.Vec3;
                case 4: return SgType.Vec4;
            }
            throw new Exception($"no scalar/vector type for rank {r}");
        }

        // Unity Shader Graph promotion rule for arithmetic with dynamic slots:
        // pick the higher-rank vector. Mat4 only valid in matrix-aware ops
        // (handled by the node, not here).
        public static SgType Promote(SgType a, SgType b)
        {
            if (a == SgType.Mat4 || b == SgType.Mat4) return SgType.Mat4;
            int ra = Rank(a), rb = Rank(b);
            if (ra == 0 || rb == 0) throw new Exception($"cannot promote {a} and {b}");
            return FromRank(Math.Max(ra, rb));
        }

        // Convert an expression of `from` type into `to` type. Handles the
        // conversions we actually hit; throws on anything novel so we add it
        // explicitly.
        public static string Coerce(string expr, SgType from, SgType to)
        {
            if (from == to) return expr;

            // scalar → vec2/3/4: broadcast
            if (from == SgType.Float)
            {
                switch (to)
                {
                    case SgType.Vec2: return $"vec2({expr})";
                    case SgType.Vec3: return $"vec3({expr})";
                    case SgType.Vec4: return $"vec4({expr})";
                }
            }
            // vec_n → smaller via swizzle, → larger via constructor
            int rf = Rank(from), rt = Rank(to);
            if (rf > 0 && rt > 0)
            {
                if (rt < rf) return $"({expr}).{Swizzle(rt)}";
                if (rt > rf)
                {
                    // pad missing channels with zeros (or 1 for alpha?). Use 0;
                    // alpha=1 is shader-specific and best decided by the caller.
                    string pad = string.Join(", ", PadZeros(rt - rf));
                    return $"{Glsl(to)}({expr}, {pad})";
                }
            }
            throw new NotImplementedException($"coerce {from} -> {to}");
        }

        static string Swizzle(int rank)
        {
            switch (rank)
            {
                case 1: return "x";
                case 2: return "xy";
                case 3: return "xyz";
                case 4: return "xyzw";
            }
            throw new Exception($"bad swizzle rank {rank}");
        }

        static IEnumerable<string> PadZeros(int n)
        {
            for (int i = 0; i < n; i++) yield return "0.0";
        }

        // ----- formatting helpers -------------------------------------------

        public static string FormatFloat(double d)
        {
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("e") || s.Contains("E") ? s : s + ".0";
        }
    }

    // ===== graph nodes/slots/edges/properties =================================

    class SgNode
    {
        public string Id;
        public string Type;                              // "MultiplyNode","UVNode","BlockNode",...
        public string SerializedDescriptor;              // BlockNode only ("SurfaceDescription.BaseColor")
        public Dictionary<int, string> SlotByNum         // numeric m_Id → slot ObjectId
            = new Dictionary<int, string>();
        public Dictionary<string, int> SlotByName        // m_DisplayName → numeric m_Id
            = new Dictionary<string, int>();
        public Dictionary<string, object> Raw;           // raw JSON for handler-specific fields
    }

    class SgSlot
    {
        public string Id;
        public string Type;          // raw Unity slot type ("ColorRGBMaterialSlot","DynamicValueMaterialSlot",...)
        public int NumId;            // node-local m_Id
        public string OutputName;    // m_ShaderOutputName
        public string DisplayName;   // m_DisplayName (used to look up slots by name from handlers)
        public bool IsOutput;        // m_SlotType: 0=input, 1=output
        public object Value;         // m_Value (preferred over m_DefaultValue — see SgParser)

        // Best-effort mapping from a static slot type to an SgType. Returns
        // Unknown for dynamic slots — those resolve at lowering time.
        public SgType StaticType()
        {
            switch (Type)
            {
                case "Vector1MaterialSlot": return SgType.Float;
                case "Vector2MaterialSlot": return SgType.Vec2;
                case "Vector3MaterialSlot":
                case "PositionMaterialSlot":
                case "NormalMaterialSlot":
                case "TangentMaterialSlot":
                case "ColorRGBMaterialSlot":
                    return SgType.Vec3;
                case "Vector4MaterialSlot":
                case "ColorRGBAMaterialSlot":
                    return SgType.Vec4;
                case "BooleanMaterialSlot":
                    return SgType.Bool;
            }
            return SgType.Unknown;
        }
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

        public SgEdge FindEdgeIntoSlot(string nodeId, int numSlotId)
        {
            foreach (var e in Edges)
                if (e.InputNodeId == nodeId && e.InputSlotIdNum == numSlotId) return e;
            return null;
        }
    }
}
