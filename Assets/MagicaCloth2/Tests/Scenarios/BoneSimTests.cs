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
    }
}
