// MagicaCloth2 characterization test suite — Phase 6: wind.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class WindTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("wind.global_directional_influence", GlobalDirectionalInfluence));
        }

        // Creates a global directional wind zone blowing +X (steady, no turbulence).
        static MagicaWindZone MakeGlobalWind(List<GameObject> track, float main)
        {
            var go = new GameObject("WindZone");
            track.Add(go);
            var wz = go.AddComponent<MagicaWindZone>();
            wz.mode = MagicaWindZone.Mode.GlobalDirection;
            wz.main = main;
            wz.turbulence = 0f;            // steady for a deterministic deflection
            wz.directionAngleX = 0f;
            wz.directionAngleY = 90f;      // Euler(0,90,0)*forward = +X
            return wz;
        }

        static IEnumerator MeasureWindDeflection(float influence, float[] outTipX)
        {
            var extra = new List<GameObject>();
            MakeGlobalWind(extra, 15f);
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.1f);
                sd.wind.influence = influence;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            var p = ClothState.Positions(rig.cloth);
            outTipX[0] = p[p.Length - 1].x - p[0].x; // tip horizontal offset (downwind = +X)
            rig.Dispose();
            foreach (var go in extra) if (go != null) Object.Destroy(go);
        }

        // 6.1 A global directional wind deflects a hanging chain downwind, and a higher per-cloth wind
        // influence produces more deflection than a near-zero influence.
        static IEnumerator GlobalDirectionalInfluence(Check check)
        {
            var lowInf = new float[1];
            var highInf = new float[1];
            yield return MeasureWindDeflection(0.05f, lowInf);
            yield return MeasureWindDeflection(2.0f, highInf);

            check.Greater("wind_deflects_downwind", highInf[0], 0.05f); // clearly blown +X
            check.Greater("more_influence_more_deflection", highInf[0], lowInf[0]);

            check.Metric("tipX_lowInfluence", lowInf[0]);
            check.Metric("tipX_highInfluence", highInf[0]);
        }
    }
}
