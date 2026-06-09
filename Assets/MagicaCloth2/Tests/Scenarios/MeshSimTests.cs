// MagicaCloth2 characterization test suite — Phase 3: MeshCloth simulation.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    public static class MeshSimTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("meshsim.pinned_sheet_droops", PinnedSheetDroops));
            list.Add(new TestEntry("meshsim.blendweight_endpoints", BlendWeightEndpoints));
            list.Add(new TestEntry("meshsim.normal_split_equivalence", NormalSplitEquivalence));
        }

        // Settle the drooping sheet, forcing the Normal vs Split job path via the split-vertex threshold.
        static IEnumerator SettleSheetPositions(int splitThreshold, float3[][] outPos, float gravity = 5f)
        {
            MagicaManager.SetSplitProxyMeshVertexCount(splitThreshold);
            var rig = Build.MeshGrid(8, 8, 0.1f, fixedTopV: 0.9f, disableReduction: true, planeXZ: true, configure: sd =>
            {
                sd.gravity = gravity;
                sd.damping.SetValue(0.05f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(500);
            outPos[0] = ClothState.Positions(rig.cloth);
            rig.Dispose();
        }

        // Settle a drooping sheet at a given blendWeight; report the settled rendered-mesh centroid height.
        // blendWeight is applied to the proxy OUTPUT (lerp(animationPose, sim, blendWeight)), so it shows up
        // in the rendered mesh, not in the raw particle buffers. 0 = animated pose, 1 = full sim.
        static IEnumerator MeasureRenderDroop(float blendWeight, float[] outCentroidY)
        {
            var rig = Build.MeshGrid(8, 8, 0.1f, fixedTopV: 0.9f, disableReduction: true, planeXZ: true, configure: sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.05f);
                sd.blendWeight = blendWeight;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            yield return new UnityEngine.WaitForEndOfFrame(); // mesh write happens pre-render
            outCentroidY[0] = ClothState.RenderMeshCentroid(rig.cloth, rig.renderer).y;
            rig.Dispose();
        }

        // Collect the proxy indices flagged Fixed.
        static List<int> FixedIndices(MagicaCloth cloth, int count)
        {
            var fixedIdx = new List<int>();
            for (int i = 0; i < count; i++)
                if (ClothState.ProxyAttribute(cloth, i).IsFixed()) fixedIdx.Add(i);
            return fixedIdx;
        }

        // 3.1 A flat sheet pinned along one edge droops under gravity:
        //   - the pinned (Fixed) row never moves
        //   - the sheet's centroid drops below its initial flat height
        //   - it settles (motion decays) and never NaNs
        static IEnumerator PinnedSheetDroops(Check check)
        {
            const int nx = 8, ny = 8;
            var rig = Build.MeshGrid(nx, ny, 0.1f, fixedTopV: 0.9f, disableReduction: true, planeXZ: true, configure: sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.05f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());

            int count = ClothState.ParticleCount(rig.cloth);
            float L = ClothState.CharacteristicLength(rig.cloth);
            float spc = ClothState.Spacing(rig.cloth);

            var fixedIdx = FixedIndices(rig.cloth, count);
            var a0 = ClothState.Positions(rig.cloth);
            float centroidY0 = ClothState.Centroid(a0).y;

            var e0 = ClothState.Positions(rig.cloth);
            yield return Build.Step(10);
            float earlyActivity = ClothState.MaxDisplacement(e0, ClothState.Positions(rig.cloth));

            yield return Build.Step(400);

            var l0 = ClothState.Positions(rig.cloth);
            yield return Build.Step(10);
            var b = ClothState.Positions(rig.cloth);
            float lateActivity = ClothState.MaxDisplacement(l0, b);
            float centroidYEnd = ClothState.Centroid(b).y;

            // pinned row immovable
            float maxFixedMove = 0f;
            foreach (int i in fixedIdx) maxFixedMove = math.max(maxFixedMove, math.distance(a0[i], b[i]));

            check.Greater("has_fixed_row", fixedIdx.Count, 0);
            check.Approx("fixed_count==edge", fixedIdx.Count, nx, 0);
            check.NoNaN("no_nan", b);
            check.Less("pinned_row_immovable", maxFixedMove, 0.02f * spc);
            check.Less("sheet_drooped", centroidYEnd, centroidY0 - 0.1f * L);
            check.Less("motion_decays", lateActivity, 0.05f * earlyActivity);

            check.Metric("fixedCount", fixedIdx.Count);
            check.Metric("maxFixedMove", maxFixedMove);
            check.Metric("centroidY0", centroidY0);
            check.Metric("centroidYEnd", centroidYEnd);
            check.Metric("earlyActivity", earlyActivity);
            check.Metric("lateActivity", lateActivity);
            check.Metric("L", L);

            rig.Dispose();
        }

        // 3.2 blendWeight blends the rendered output between the animated (flat) pose and the full sim:
        //   - blend=0 keeps the sheet at its animated flat pose (centroid ~0)
        //   - blend=1 shows the full simulation (drooped, centroid well below)
        static IEnumerator BlendWeightEndpoints(Check check)
        {
            const float L = 0.7f;
            var animated = new float[1];
            var fullSim = new float[1];
            yield return MeasureRenderDroop(0f, animated);
            yield return MeasureRenderDroop(1f, fullSim);

            check.Approx("blend0_matches_animated_flat", animated[0], 0f, 0.03f * L);
            check.Less("blend1_full_sim_droops", fullSim[0], -0.1f * L);
            check.Less("blend1_droops_more_than_blend0", fullSim[0], animated[0]);

            check.Metric("centroidY_blend0", animated[0]);
            check.Metric("centroidY_blend1", fullSim[0]);
        }

        // 3.3 The Normal (single-job) and Split (multi-job) simulation paths are an optimization choice,
        // not a behavior change: the same sheet settles to the same shape within the loose band whether
        // forced through Normal (high threshold) or Split (threshold=1).
        static IEnumerator NormalSplitEquivalence(Check check)
        {
            const float spacing = 0.1f, L = 0.7f;
            var normal = new float3[1][];
            var split = new float3[1][];
            yield return SettleSheetPositions(100000, normal); // Normal path (64 verts < threshold)
            yield return SettleSheetPositions(1, split);        // Split path (64 verts >= 1)
            MagicaManager.SetSplitProxyMeshVertexCount(300);    // restore default

            float maxDisp = ClothState.MaxDisplacement(normal[0], split[0]);
            float3 cN = ClothState.Centroid(normal[0]);
            float3 cS = ClothState.Centroid(split[0]);
            float centroidDelta = math.distance(cN, cS);

            check.Greater("both_built", math.min(normal[0].Length, split[0].Length), 0);
            check.Less("both_drooped", math.max(cN.y, cS.y), -0.1f * L);
            check.Less("per_vertex_within_band", maxDisp, 0.15f * spacing);
            check.Less("centroid_within_band", centroidDelta, 0.03f * L);

            check.Metric("maxVertexDelta", maxDisp);
            check.Metric("maxVertexDelta_over_spacing", maxDisp / spacing);
            check.Metric("centroidDelta", centroidDelta);
            check.Metric("centroidYNormal", cN.y);
            check.Metric("centroidYSplit", cS.y);
        }
    }
}
