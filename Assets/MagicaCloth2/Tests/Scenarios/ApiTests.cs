// MagicaCloth2 characterization test suite — Phase 8: lifecycle / API / culling.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class ApiTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("api.add_force", AddForce));
            list.Add(new TestEntry("api.reset_cloth", ResetCloth));
            list.Add(new TestEntry("api.set_time_scale", SetTimeScale));
        }

        // 8.2 ResetCloth(false) snaps the simulation back to its initial (rest) pose: a chain that has
        // fallen far from its horizontal rest returns to it after a reset; without a reset it stays fallen.
        static IEnumerator MeasureReset(bool doReset, float[] outDispFromRest)
        {
            var rig = Build.BoneChain(12, new float3(1, 0, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.1f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            var rest = ClothState.Positions(rig.cloth); // horizontal rest, captured at build
            yield return Build.Step(200);                // chain falls and hangs
            if (doReset) rig.cloth.ResetCloth(false);
            yield return Build.Step(2);

            outDispFromRest[0] = ClothState.MaxDisplacement(ClothState.Positions(rig.cloth), rest);
            rig.Dispose();
        }

        static IEnumerator ResetCloth(Check check)
        {
            var reset = new float[1];
            var noReset = new float[1];
            yield return MeasureReset(true, reset);
            yield return MeasureReset(false, noReset);

            check.Less("reset_returns_to_rest", reset[0], 0.05f);
            check.Greater("no_reset_stays_fallen", noReset[0], 0.3f);

            check.Metric("dispFromRest_afterReset", reset[0]);
            check.Metric("dispFromRest_noReset", noReset[0]);
        }

        // 8.3 SetTimeScale scales how fast the simulation advances: timeScale 0 freezes the cloth,
        // timeScale 1 lets it fall normally.
        static IEnumerator MeasureTimeScaleActivity(float timeScale, float[] outActivity)
        {
            var rig = Build.BoneChain(12, new float3(1, 0, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.05f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            rig.cloth.SetTimeScale(timeScale);
            yield return null;

            var a = ClothState.Positions(rig.cloth);
            yield return Build.Step(60);
            outActivity[0] = ClothState.MaxDisplacement(a, ClothState.Positions(rig.cloth));
            rig.Dispose();
        }

        static IEnumerator SetTimeScale(Check check)
        {
            var full = new float[1];
            var frozen = new float[1];
            yield return MeasureTimeScaleActivity(1f, full);
            yield return MeasureTimeScaleActivity(0f, frozen);

            check.Greater("full_scale_moves", full[0], 0.1f);
            check.Less("zero_scale_frozen", frozen[0], 0.005f);

            check.Metric("activity_scale1", full[0]);
            check.Metric("activity_scale0", frozen[0]);
        }

        // Settle a hanging chain, optionally apply a sideways impulse via AddForce, and report the peak
        // tip displacement in X over the next several frames.
        static IEnumerator MeasureAddForce(bool applyForce, float[] outPeakX)
        {
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 3f;
                sd.damping.SetValue(0.05f);
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(120); // settle straight down

            float restX = ClothState.Positions(rig.cloth)[11].x;
            if (applyForce)
                rig.cloth.AddForce(Vector3.right, 30f, ClothForceMode.VelocityChange);

            float peak = 0f;
            for (int i = 0; i < 40; i++)
            {
                yield return null;
                peak = math.max(peak, math.abs(ClothState.Positions(rig.cloth)[11].x - restX));
            }
            outPeakX[0] = peak;
            rig.Dispose();
        }

        // 8.1 AddForce injects an external impulse: a settled chain given a sideways force swings, while
        // the same chain with no force stays put.
        static IEnumerator AddForce(Check check)
        {
            var forced = new float[1];
            var idle = new float[1];
            yield return MeasureAddForce(true, forced);
            yield return MeasureAddForce(false, idle);

            check.Greater("force_swings_cloth", forced[0], 0.1f);
            check.Less("no_force_stays_put", idle[0], 0.01f);

            check.Metric("peakX_forced", forced[0]);
            check.Metric("peakX_idle", idle[0]);
        }
    }
}
