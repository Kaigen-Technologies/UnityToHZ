// MagicaCloth2 characterization test suite — Phase 2: BoneCloth simulation.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    public static class BoneSimTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("bonesim.rest_stability", RestStability));
            list.Add(new TestEntry("bonesim.gravity_settle", GravitySettle));
            list.Add(new TestEntry("bonesim.damping_ordinal", DampingOrdinal));
        }

        // Build a horizontal chain with a given damping and measure how many frames it takes to settle.
        static IEnumerator MeasureSettleFrames(float damping, float threshold, int[] outFrames)
        {
            var rig = Build.BoneChain(12, new float3(1, 0, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(damping);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.StepUntilSettled(rig.cloth, threshold, 800, outFrames);
            rig.Dispose();
        }

        // 2.1 A chain already hanging straight down under aligned gravity is at equilibrium: after a
        // short settle it must barely drift (no creep), never explode, and never NaN.
        static IEnumerator RestStability(Check check)
        {
            const int n = 12;
            const float spacing = 0.1f;
            var rig = Build.BoneChain(n, new float3(0, -1, 0), spacing, sd =>
            {
                sd.gravity = 5f; // gravity points down by default, aligned with the chain
                sd.damping.SetValue(0.1f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());

            // settle ~1s, then measure drift over the next ~4s
            yield return Build.Step(60);
            var a = ClothState.Positions(rig.cloth);
            yield return Build.Step(240);
            var b = ClothState.Positions(rig.cloth);

            float spc = ClothState.Spacing(rig.cloth);
            float drift = ClothState.MaxDisplacement(a, b);
            float L = ClothState.CharacteristicLength(rig.cloth);
            float3 root = b.Length > 0 ? b[0] : float3.zero;
            float maxR = ClothState.MaxRadius(b, root);

            check.NoNaN("no_nan", b);
            check.Less("no_drift", drift, 0.02f * spc);         // equilibrium: negligible creep
            check.Less("no_explosion", maxR, 3f * L);            // stays within a few cloth-lengths

            check.Metric("drift", drift);
            check.Metric("drift_over_spacing", drift / spc);
            check.Metric("maxRadius", maxR);
            check.Metric("L", L);

            rig.Dispose();
        }

        // 2.2 A horizontal chain pinned at the root falls under gravity and settles hanging:
        //   - it ends below the root (tip well under, centroid below)
        //   - motion decays (late per-step activity << early): energy dissipates, no perpetual motion
        //   - no NaN
        static IEnumerator GravitySettle(Check check)
        {
            const int n = 12;
            const float spacing = 0.1f;
            var rig = Build.BoneChain(n, new float3(1, 0, 0), spacing, sd =>
            {
                sd.gravity = 5f; // down; chain starts horizontal (+X) so it must swing down
                sd.damping.SetValue(0.05f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());

            float L = ClothState.CharacteristicLength(rig.cloth);
            float3 root = ClothState.Positions(rig.cloth)[0];

            // early activity (chain is actively falling)
            var e0 = ClothState.Positions(rig.cloth);
            yield return Build.Step(10);
            float earlyActivity = ClothState.MaxDisplacement(e0, ClothState.Positions(rig.cloth));

            // settle
            yield return Build.Step(400);

            // late activity (should be ~still)
            var l0 = ClothState.Positions(rig.cloth);
            yield return Build.Step(10);
            var lEnd = ClothState.Positions(rig.cloth);
            float lateActivity = ClothState.MaxDisplacement(l0, lEnd);

            float3 tip = lEnd[lEnd.Length - 1];
            float3 centroid = ClothState.Centroid(lEnd);

            check.NoNaN("no_nan", lEnd);
            check.Less("tip_below_root", tip.y, root.y - 0.5f * L);   // hangs well below the pin
            check.Less("centroid_below_root", centroid.y, root.y);
            check.Less("motion_decays", lateActivity, 0.05f * earlyActivity);

            check.Metric("earlyActivity", earlyActivity);
            check.Metric("lateActivity", lateActivity);
            check.Metric("tipY", tip.y);
            check.Metric("rootY", root.y);
            check.Metric("L", L);

            rig.Dispose();
        }

        // 2.3 Ordinal: a more heavily damped chain settles in fewer (or equal) frames than a lightly
        // damped one. Tolerance-free ordering — catches sign/scale errors in the damping term.
        static IEnumerator DampingOrdinal(Check check)
        {
            const float threshold = 0.0003f;
            var low = new int[1];
            var high = new int[1];
            yield return MeasureSettleFrames(0.02f, threshold, low);
            yield return MeasureSettleFrames(0.4f, threshold, high);

            check.Greater("low_settles_at_all", low[0], 0);
            check.AtMost("higher_damping_settles_faster", high[0], low[0]);
            check.Less("high_strictly_faster", high[0], low[0]);

            check.Metric("framesLowDamping", low[0]);
            check.Metric("framesHighDamping", high[0]);
        }
    }
}
