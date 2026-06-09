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
            list.Add(new TestEntry("constraint.angle_restoration", AngleRestoration));
            list.Add(new TestEntry("constraint.angle_limit", AngleLimit));
            list.Add(new TestEntry("constraint.triangle_bending", TriangleBending));
            list.Add(new TestEntry("constraint.motion_max_distance", MotionMaxDistance));
            list.Add(new TestEntry("constraint.bonespring", BoneSpring));
            // NOTE: Motion Backstop is intentionally not tested in isolation here. It requires a configured
            // normal-alignment direction (a body center, e.g. hair-on-head) to define which way the backstop
            // sphere sits; with synthetic flat/chain rigs the normals are degenerate and the backstop is inert
            // (verified: even a 0.5m backstop radius produced no shape change). The sibling clamp mechanism is
            // covered by constraint.motion_max_distance. Revisit with a realistic skinned-hair rig if needed.
        }

        static IEnumerator MeasureMaxDistFromBase(bool useMaxDistance, float maxDistance, float[] outMaxDist)
        {
            var rig = Build.BoneChain(12, new float3(1, 0, 0), 0.1f, sd =>
            {
                sd.gravity = 10f;
                sd.damping.SetValue(0.1f);
                sd.angleRestorationConstraint.useAngleRestoration = false;
                sd.motionConstraint.useMaxDistance = useMaxDistance;
                if (useMaxDistance) sd.motionConstraint.maxDistance.SetValue(maxDistance);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            outMaxDist[0] = ClothState.MaxDistanceFromBase(rig.cloth);
            rig.Dispose();
        }


        // Max inter-segment bend angle (degrees) along the chain.
        static float MaxBendDeg(float3[] p)
        {
            float max = 0f;
            for (int i = 1; i + 1 < p.Length; i++)
            {
                float3 v1 = math.normalizesafe(p[i] - p[i - 1]);
                float3 v2 = math.normalizesafe(p[i + 1] - p[i]);
                max = math.max(max, math.degrees(math.acos(math.clamp(math.dot(v1, v2), -1f, 1f))));
            }
            return max;
        }

        // Hang a chain (rest = straight down -Y), push it sideways with +X gravity, restoration OFF so the
        // angle-limit constraint is isolated; report the max per-joint bend and the tip's sideways swing.
        static IEnumerator MeasureAngleLimit(float limitAngle, float[] outMaxBend, float[] outTipX)
        {
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 10f;
                sd.gravityDirection = new float3(1, 0, 0); // sideways -> tries to fold the hanging chain
                sd.damping.SetValue(0.15f);
                sd.angleRestorationConstraint.useAngleRestoration = false;
                sd.angleLimitConstraint.useAngleLimit = true;
                sd.angleLimitConstraint.limitAngle.SetValue(limitAngle);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            var p = ClothState.Positions(rig.cloth);
            outMaxBend[0] = MaxBendDeg(p);
            outTipX[0] = p[p.Length - 1].x - p[0].x; // tip horizontal offset from root
            rig.Dispose();
        }

        static float MinY(float3[] p)
        {
            float m = float.MaxValue;
            for (int i = 0; i < p.Length; i++) m = math.min(m, p[i].y);
            return m;
        }

        // Settle a horizontal chain (rest shape = straight along +X) and report its lowest point. Angle
        // restoration pulls the chain back toward that horizontal rest, so it sags less when enabled.
        static IEnumerator MeasureSagMinY(bool restorationOn, float[] outMinY)
        {
            var rig = Build.BoneChain(12, new float3(1, 0, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.1f);
                sd.angleLimitConstraint.useAngleLimit = false;          // isolate restoration from limit
                sd.angleRestorationConstraint.useAngleRestoration = restorationOn;
                if (restorationOn) sd.angleRestorationConstraint.stiffness.SetValue(1.0f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            outMinY[0] = MinY(ClothState.Positions(rig.cloth));
            rig.Dispose();
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

        // 4.3 Angle restoration pulls the chain back toward its straight horizontal rest shape. With it
        // OFF the chain sags fully (hangs down); with it ON the chain stays much higher (less sag).
        static IEnumerator AngleRestoration(Check check)
        {
            const float L = 1.1f;
            var off = new float[1];
            var on = new float[1];
            yield return MeasureSagMinY(false, off);
            yield return MeasureSagMinY(true, on);

            check.Less("sags_fully_without_restoration", off[0], -0.3f * L); // clearly droops when off
            check.Greater("restoration_reduces_sag", on[0], off[0]);          // ON stays higher

            check.Metric("minY_off", off[0]);
            check.Metric("minY_on", on[0]);
        }

        // 4.4 The angle-limit constraint caps how far the chain can fold. A tighter limit keeps the chain
        // closer to its rest (less sideways swing) than a loose one. (Observe-then-assert ordinal.)
        static IEnumerator AngleLimit(Check check)
        {
            var tightBend = new float[1]; var tightTipX = new float[1];
            var looseBend = new float[1]; var looseTipX = new float[1];
            yield return MeasureAngleLimit(10f, tightBend, tightTipX);
            yield return MeasureAngleLimit(80f, looseBend, looseTipX);

            check.AtMost("tight_caps_joint_bend", tightBend[0], 10f, 3f); // bend held near the 10deg limit
            check.Less("tighter_limit_less_swing", tightTipX[0], looseTipX[0]);
            check.Greater("loose_limit_swings", looseTipX[0], 0.1f);

            check.Metric("maxBend_tight", tightBend[0]);
            check.Metric("maxBend_loose", looseBend[0]);
            check.Metric("tipX_tight", tightTipX[0]);
            check.Metric("tipX_loose", looseTipX[0]);
        }

        // 4.5 Triangle-bending constraint construction: a triangulated MeshCloth registers bending pairs
        // (interior edges shared by two triangles), while a pure line-topology BoneCloth registers none.
        // This deterministic data-construction property is what the engine port must replicate. (The
        // constraint's isolated quasi-static *shape* effect is below the simulation's cross-build noise
        // floor in simple settles, so it is characterized here at the data level rather than by droop.)
        static IEnumerator TriangleBending(Check check)
        {
            var mesh = Build.MeshGrid(6, 6, 0.1f, fixedTopV: 0.9f, disableReduction: true, configure: sd => sd.gravity = 0f);
            yield return Build.WaitForBuild(mesh.cloth);
            int meshTris = ClothState.ProxyTriangleCount(mesh.cloth);
            int meshPairs = ClothState.BendingPairCount(mesh.cloth);
            mesh.Dispose();

            var line = Build.BoneChain(10, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.connectionMode = RenderSetupData.BoneConnectionMode.Line;
                sd.gravity = 0f;
            });
            yield return Build.WaitForBuild(line.cloth);
            int linePairs = ClothState.BendingPairCount(line.cloth);
            line.Dispose();

            check.Greater("mesh_has_triangles", meshTris, 0);
            check.Greater("mesh_creates_bending_pairs", meshPairs, 0);
            check.Approx("line_creates_no_bending_pairs", linePairs, 0, 0);

            check.Metric("meshTriangles", meshTris);
            check.Metric("meshBendingPairs", meshPairs);
            check.Metric("lineBendingPairs", linePairs);
        }

        // 4.6 The motion MaxDistance constraint hard-caps how far any particle may move from its base
        // (animated) pose: with it ON no particle exceeds maxDistance; with it OFF the chain falls far.
        static IEnumerator MotionMaxDistance(Check check)
        {
            const float maxDistance = 0.05f;
            var on = new float[1];
            var off = new float[1];
            yield return MeasureMaxDistFromBase(true, maxDistance, on);
            yield return MeasureMaxDistFromBase(false, maxDistance, off);

            check.AtMost("on_caps_displacement", on[0], maxDistance, 0.01f); // within cap (+ small solver slack)
            check.Greater("off_moves_far", off[0], 0.3f);                    // unconstrained chain falls far

            check.Metric("maxDistFromBase_on", on[0]);
            check.Metric("maxDistFromBase_off", off[0]);
            check.Metric("maxDistanceLimit", maxDistance);
        }

        // 4.8 BoneSpring: gravity is forced to 0 (a pinned spring bone doesn't fall), and the spring
        // constraint bounds how far a bone may wobble from its base pose to limitDistance.
        static IEnumerator BoneSpring(Check check)
        {
            const float limitDistance = 0.06f;

            // (a) no force: despite gravity=10, BoneSpring forces gravity to 0 -> bones stay at base.
            var atRest = Build.BoneChain(8, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.clothType = ClothProcess.ClothType.BoneSpring;
                sd.gravity = 10f; // should be ignored by BoneSpring
                sd.springConstraint.useSpring = true;
                sd.springConstraint.limitDistance = limitDistance;
                sd.springConstraint.springPower = 0.05f;
                sd.damping.SetValue(0.05f);
            });
            yield return Build.WaitForBuild(atRest.cloth);
            yield return Build.Step(200);
            float restMove = ClothState.MaxDistanceFromBase(atRest.cloth);
            atRest.Dispose();

            // (b) strong impulse: the spring caps wobble to limitDistance.
            var kicked = Build.BoneChain(8, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.clothType = ClothProcess.ClothType.BoneSpring;
                sd.gravity = 0f;
                sd.springConstraint.useSpring = true;
                sd.springConstraint.limitDistance = limitDistance;
                sd.springConstraint.springPower = 0.05f;
                sd.damping.SetValue(0.02f);
            });
            yield return Build.WaitForBuild(kicked.cloth);
            kicked.cloth.AddForce(new UnityEngine.Vector3(1, 0, 0), 25f, ClothForceMode.VelocityChange);
            float peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                yield return null;
                peak = math.max(peak, ClothState.MaxDistanceFromBase(kicked.cloth));
            }
            kicked.Dispose();

            check.Less("gravity_forced_zero", restMove, 0.01f);          // no fall under BoneSpring
            check.AtMost("spring_caps_wobble", peak, limitDistance, 0.02f); // wobble bounded by limitDistance
            check.Greater("spring_actually_wobbles", peak, 0.005f);       // the impulse did move it

            check.Metric("restMove", restMove);
            check.Metric("peakWobble", peak);
            check.Metric("limitDistance", limitDistance);
        }
    }
}
