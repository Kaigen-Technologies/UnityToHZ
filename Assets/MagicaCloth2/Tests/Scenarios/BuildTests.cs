// MagicaCloth2 characterization test suite — Phase 1: build & proxy generation.
// These inspect the deterministic proxy-mesh construction (no simulation stepping).
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    public static class BuildTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("build.bonecloth_line_topology", BoneClothLineTopology));
        }

        // 1.1 A single-chain BoneCloth (Line connection) of N bones must yield a proxy with:
        //   - N vertices, N-1 edges (a path), 0 triangles
        //   - exactly one Fixed vertex (the root) and N-1 Move vertices
        //   - normalized vertex depth spanning ~0 (root) to ~1 (tip)
        static IEnumerator BoneClothLineTopology(Check check)
        {
            const int n = 10;
            var rig = Build.BoneChain(n, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.connectionMode = RenderSetupData.BoneConnectionMode.Line;
                sd.gravity = 0f;
            });
            yield return Build.WaitForBuild(rig.cloth);

            check.IsTrue("valid", rig.cloth.IsValid());

            int verts = ClothState.ProxyVertexCount(rig.cloth);
            int edges = ClothState.ProxyEdgeCount(rig.cloth);
            int tris = ClothState.ProxyTriangleCount(rig.cloth);
            check.Approx("verts==n", verts, n, 0);
            check.Approx("edges==n-1", edges, n - 1, 0);
            check.Approx("tris==0", tris, 0, 0);

            int fixedCount = 0, moveCount = 0;
            float minDepth = float.MaxValue, maxDepth = float.MinValue;
            for (int i = 0; i < verts; i++)
            {
                var a = ClothState.ProxyAttribute(rig.cloth, i);
                if (a.IsFixed()) fixedCount++;
                else if (a.IsMove()) moveCount++;
                float d = ClothState.ProxyDepth(rig.cloth, i);
                minDepth = math.min(minDepth, d);
                maxDepth = math.max(maxDepth, d);
            }
            check.Approx("one_fixed_root", fixedCount, 1, 0);
            check.Approx("rest_move", moveCount, n - 1, 0);
            check.Less("min_depth~0", minDepth, 0.01f);
            check.Greater("max_depth~1", maxDepth, 0.9f);

            check.Metric("verts", verts);
            check.Metric("edges", edges);
            check.Metric("tris", tris);
            check.Metric("fixed", fixedCount);
            check.Metric("move", moveCount);
            check.Metric("minDepth", minDepth);
            check.Metric("maxDepth", maxDepth);

            rig.Dispose();
        }
    }
}
