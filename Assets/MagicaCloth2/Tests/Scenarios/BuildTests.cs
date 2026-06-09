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
            list.Add(new TestEntry("build.bonecloth_mesh_topology", BoneClothMeshTopology));
            list.Add(new TestEntry("build.meshcloth_paintmap_attributes", MeshClothPaintMapAttributes));
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

        // 1.2 A BoneCloth grid with a Mesh connection mode must synthesize triangle topology:
        //   - cols*rows vertices
        //   - triangles > 0 and more edges than a pure path (mesh, not line)
        //   - one Fixed vertex per column root
        static IEnumerator BoneClothMeshTopology(Check check)
        {
            const int cols = 4, rows = 5;
            var rig = Build.BoneGrid(cols, rows, 0.1f, sd =>
            {
                sd.connectionMode = RenderSetupData.BoneConnectionMode.SequentialNonLoopMesh;
                sd.gravity = 0f;
            });
            yield return Build.WaitForBuild(rig.cloth);

            check.IsTrue("valid", rig.cloth.IsValid());

            int verts = ClothState.ProxyVertexCount(rig.cloth);
            int edges = ClothState.ProxyEdgeCount(rig.cloth);
            int tris = ClothState.ProxyTriangleCount(rig.cloth);
            check.Approx("verts==cols*rows", verts, cols * rows, 0);
            check.Greater("has_triangles", tris, 0);
            check.Greater("mesh_has_more_edges_than_path", edges, verts - 1);

            int fixedCount = 0;
            for (int i = 0; i < verts; i++)
                if (ClothState.ProxyAttribute(rig.cloth, i).IsFixed()) fixedCount++;
            check.Approx("one_fixed_per_column", fixedCount, cols, 0);

            check.Metric("verts", verts);
            check.Metric("edges", edges);
            check.Metric("tris", tris);
            check.Metric("fixed", fixedCount);

            rig.Dispose();
        }

        // 1.3 MeshCloth import: a generated nx*ny grid with a Texture_Fixed_Move paint map (top row red
        // = Fixed, rest green = Move). With reduction disabled, the proxy keeps all grid vertices and the
        // attributes must match the painted regions: nx Fixed (top row), nx*(ny-1) Move.
        static IEnumerator MeshClothPaintMapAttributes(Check check)
        {
            const int nx = 6, ny = 6;
            var rig = Build.MeshGrid(nx, ny, 0.1f, fixedTopV: 0.9f, disableReduction: true, configure: sd =>
            {
                sd.gravity = 0f;
            });
            yield return Build.WaitForBuild(rig.cloth);

            check.IsTrue("valid", rig.cloth.IsValid());

            int verts = ClothState.ProxyVertexCount(rig.cloth);
            int tris = ClothState.ProxyTriangleCount(rig.cloth);
            check.Approx("verts==grid (no reduction)", verts, nx * ny, 0);
            check.Greater("has_triangles", tris, 0);

            int fixedCount = 0, moveCount = 0;
            for (int i = 0; i < verts; i++)
            {
                var a = ClothState.ProxyAttribute(rig.cloth, i);
                if (a.IsFixed()) fixedCount++;
                else if (a.IsMove()) moveCount++;
            }
            check.Approx("fixed==top_row", fixedCount, nx, 0);
            check.Approx("move==rest", moveCount, nx * (ny - 1), 0);

            check.Metric("verts", verts);
            check.Metric("tris", tris);
            check.Metric("fixed", fixedCount);
            check.Metric("move", moveCount);

            rig.Dispose();
        }
    }
}
