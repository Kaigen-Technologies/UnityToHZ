// MagicaCloth2 characterization test suite.
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    /// <summary>
    /// Reads simulation/proxy state for a built cloth, using only public MagicaCloth2 API.
    ///
    /// The "true" simulation state is the proxy particle position buffer
    /// (MagicaManager.Simulation.nextPosArray), sliced per team by tdata.particleChunk. This is
    /// independent of mesh skinning / transform writeback and is the primary comparison surface.
    /// All positions are in WORLD space.
    /// </summary>
    public static class ClothState
    {
        public static bool TryParticleRange(MagicaCloth cloth, out int start, out int count)
        {
            start = 0; count = 0;
            if (cloth == null || !cloth.IsValid()) return false;
            ref var tdata = ref MagicaManager.Team.GetTeamDataRef(cloth.Process.TeamId);
            start = tdata.particleChunk.startIndex;
            count = tdata.particleChunk.dataLength;
            return count > 0;
        }

        /// <summary>Live world-space particle positions, or empty array if not built.</summary>
        public static float3[] Positions(MagicaCloth cloth)
        {
            if (!TryParticleRange(cloth, out int start, out int count)) return new float3[0];
            var arr = MagicaManager.Simulation.nextPosArray.GetNativeArray();
            var result = new float3[count];
            for (int i = 0; i < count; i++) result[i] = arr[start + i];
            return result;
        }

        public static int ParticleCount(MagicaCloth cloth)
        {
            return TryParticleRange(cloth, out _, out int count) ? count : 0;
        }

        /// <summary>
        /// Live world-space DISPLAY positions (dispPosArray) — the future-predicted, blendWeight-blended
        /// pose actually used for render/transform writeback. Differs from raw sim Positions() when
        /// blendWeight &gt; 0 (blend toward the animated pose).
        /// </summary>
        public static float3[] DisplayPositions(MagicaCloth cloth)
        {
            if (!TryParticleRange(cloth, out int start, out int count)) return new float3[0];
            var arr = MagicaManager.Simulation.dispPosArray.GetNativeArray();
            var result = new float3[count];
            for (int i = 0; i < count; i++) result[i] = arr[start + i];
            return result;
        }

        // --- proxy mesh geometry (for scale-relative tolerances) -----------------------------

        static VirtualMesh Proxy(MagicaCloth cloth) => cloth?.Process?.ProxyMeshContainer?.shareVirtualMesh;

        /// <summary>Characteristic length L = proxy bounding-box max side. Tolerances scale to this.</summary>
        public static float CharacteristicLength(MagicaCloth cloth)
        {
            var vm = Proxy(cloth);
            return (vm != null && vm.boundingBox.IsCreated) ? vm.boundingBox.Value.MaxSideLength : 1f;
        }

        /// <summary>Average particle spacing (= proxy averageVertexDistance).</summary>
        public static float Spacing(MagicaCloth cloth)
        {
            var vm = Proxy(cloth);
            return (vm != null && vm.averageVertexDistance.IsCreated) ? vm.averageVertexDistance.Value : 1f;
        }

        /// <summary>
        /// Centroid of the cloth's baked custom render mesh (in renderer-local space). This is the final
        /// blended output (blendWeight applied), so it reflects blendWeight where the raw particle buffers
        /// do not. Read at END OF FRAME (after the pre-render mesh write) to see the current pose.
        /// </summary>
        public static float3 RenderMeshCentroid(MagicaCloth cloth, UnityEngine.Renderer ren)
        {
            var mesh = cloth.GetCustomMesh(ren);
            if (mesh == null) return float3.zero;
            var vs = mesh.vertices;
            if (vs.Length == 0) return float3.zero;
            float3 sum = float3.zero;
            for (int i = 0; i < vs.Length; i++) sum += (float3)vs[i];
            return sum / vs.Length;
        }

        /// <summary>World-space vertices of the baked custom render mesh (empty if not built/skinned).</summary>
        public static float3[] RenderMeshPositions(MagicaCloth cloth, UnityEngine.Renderer ren)
        {
            var mesh = cloth.GetCustomMesh(ren);
            if (mesh == null) return new float3[0];
            var vs = mesh.vertices;
            var m = ren.localToWorldMatrix;
            var result = new float3[vs.Length];
            for (int i = 0; i < vs.Length; i++) result[i] = (float3)m.MultiplyPoint3x4(vs[i]);
            return result;
        }

        public static int ProxyVertexCount(MagicaCloth cloth) => Proxy(cloth)?.VertexCount ?? 0;
        public static int ProxyEdgeCount(MagicaCloth cloth) => Proxy(cloth)?.EdgeCount ?? 0;
        public static int ProxyTriangleCount(MagicaCloth cloth) => Proxy(cloth)?.TriangleCount ?? 0;

        public static VertexAttribute ProxyAttribute(MagicaCloth cloth, int index)
        {
            var vm = Proxy(cloth);
            return (vm != null && index < vm.attributes.Count) ? vm.attributes[index] : default;
        }

        public static float ProxyDepth(MagicaCloth cloth, int index)
        {
            var vm = Proxy(cloth);
            return (vm != null && vm.vertexDepths.IsCreated && index < vm.vertexDepths.Length) ? vm.vertexDepths[index] : 0f;
        }

        // --- aggregates ----------------------------------------------------------------------

        public static float3 Centroid(float3[] pts)
        {
            if (pts.Length == 0) return float3.zero;
            float3 sum = float3.zero;
            for (int i = 0; i < pts.Length; i++) sum += pts[i];
            return sum / pts.Length;
        }

        /// <summary>Max displacement between two same-length position sets.</summary>
        public static float MaxDisplacement(float3[] a, float3[] b)
        {
            int n = math.min(a.Length, b.Length);
            float max = 0f;
            for (int i = 0; i < n; i++) max = math.max(max, math.distance(a[i], b[i]));
            return max;
        }

        public static bool AnyNaN(float3[] pts)
        {
            for (int i = 0; i < pts.Length; i++)
                if (math.any(math.isnan(pts[i])) || math.any(math.isinf(pts[i]))) return true;
            return false;
        }

        /// <summary>Largest distance of any particle from a reference point (explosion detector).</summary>
        public static float MaxRadius(float3[] pts, float3 center)
        {
            float max = 0f;
            for (int i = 0; i < pts.Length; i++) max = math.max(max, math.distance(pts[i], center));
            return max;
        }
    }
}
