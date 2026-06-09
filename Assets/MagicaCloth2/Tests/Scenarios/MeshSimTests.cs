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
    }
}
