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
