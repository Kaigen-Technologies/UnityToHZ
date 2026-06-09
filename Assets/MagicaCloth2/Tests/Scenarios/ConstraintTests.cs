// MagicaCloth2 characterization test suite — Phase 4: isolated constraints.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    public static class ConstraintTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("constraint.distance_length_preservation", DistanceLengthPreservation));
            list.Add(new TestEntry("constraint.tether_compression_limit", TetherCompressionLimit));
        }

        // Compress a chain axially (gravity toward the fixed root) and report settled tip-to-root distance.
        static IEnumerator MeasureTipToRoot(float distanceCompression, float[] outDist)
        {
            var rig = Build.BoneChain(12, new float3(1, 0, 0), 0.1f, sd =>
            {
                sd.gravity = 10f;
                sd.gravityDirection = new float3(-1, 0, 0); // pull tip back toward the root => compression
                sd.damping.SetValue(0.2f);
                sd.angleRestorationConstraint.useAngleRestoration = false;
                sd.angleLimitConstraint.useAngleLimit = false;
                sd.tetherConstraint.distanceCompression = distanceCompression;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            var p = ClothState.Positions(rig.cloth);
            outDist[0] = math.distance(p[0], p[p.Length - 1]);
            rig.Dispose();
        }

        // Max ratio of any consecutive-particle distance to the rest spacing (1.0 = perfectly preserved).
        static float MaxStretchRatio(float3[] p, float rest)
        {
            float max = 0f;
            for (int i = 0; i + 1 < p.Length; i++) max = math.max(max, math.distance(p[i], p[i + 1]) / rest);
            return max;
        }

        static IEnumerator MeasureStretch(float distanceStiffness, float gravity, float[] outMaxRatio)
        {
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = gravity;
                sd.damping.SetValue(0.1f);
                sd.angleRestorationConstraint.useAngleRestoration = false; // isolate length behavior
                sd.angleLimitConstraint.useAngleLimit = false;
                sd.distanceConstraint.stiffness.SetValue(distanceStiffness);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            outMaxRatio[0] = MaxStretchRatio(ClothState.Positions(rig.cloth), 0.1f);
            rig.Dispose();
        }

        // 4.1 The distance constraint preserves edge lengths under load: even a heavy hanging chain keeps
        // every consecutive edge close to its rest length (the cloth does not visibly stretch), and a
        // stiffer constraint preserves length at least as well as a slack one.
        static IEnumerator DistanceLengthPreservation(Check check)
        {
            var slack = new float[1];
            var stiff = new float[1];
            yield return MeasureStretch(0.0f, 15f, slack);
            yield return MeasureStretch(1.0f, 15f, stiff);

            check.Less("stiff_bounded", stiff[0], 1.15f);          // bounded stretch under heavy load
            check.Less("stiffer_stretches_less", stiff[0], slack[0]); // distance-constraint signal

            check.Metric("maxStretch_slack", slack[0]);
            check.Metric("maxStretch_stiff", stiff[0]);
        }

        // 4.2 The tether constraint limits how far a chain may compress toward its root. A higher
        // distanceCompression permits more compression (tip ends closer to the root); a low value holds
        // the chain out. Ordinal + the held-out chain stays a meaningful fraction of its rest length.
        static IEnumerator TetherCompressionLimit(Check check)
        {
            const float restTip = 1.1f; // 11 segments * 0.1
            var lowComp = new float[1];
            var highComp = new float[1];
            yield return MeasureTipToRoot(0.1f, lowComp);  // little compression allowed -> tip stays far
            yield return MeasureTipToRoot(0.9f, highComp); // much compression allowed -> tip near root

            check.Greater("more_compression_allows_closer", lowComp[0], highComp[0]);
            check.Greater("low_compression_holds_chain_out", lowComp[0], 0.5f * restTip);

            check.Metric("tipToRoot_lowComp", lowComp[0]);
            check.Metric("tipToRoot_highComp", highComp[0]);
        }
    }
}
