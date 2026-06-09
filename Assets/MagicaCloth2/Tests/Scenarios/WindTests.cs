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
            list.Add(new TestEntry("wind.sphere_zone_containment", SphereZoneContainment));
            list.Add(new TestEntry("wind.turbulence_oscillation", TurbulenceOscillation));
        }

        static IEnumerator MeasureWindActivity(float turbulence, float[] outActivity, float[] outMaxR)
        {
            var extra = new List<GameObject>();
            var wz = MakeGlobalWind(extra, 15f);
            wz.turbulence = turbulence;
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.05f);
                sd.wind.influence = 2f;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(300);
            var a = ClothState.Positions(rig.cloth);
            yield return Build.Step(20);
            var b = ClothState.Positions(rig.cloth);
            outActivity[0] = ClothState.MaxDisplacement(a, b);
            outMaxR[0] = ClothState.MaxRadius(b, b[0]);
            rig.Dispose();
            foreach (var g in extra) if (g != null) Object.Destroy(g);
        }

        // 6.3 Turbulence: with turbulence=0 the wind-blown chain settles to a steady pose (near-zero
        // residual motion); with turbulence>0 it keeps oscillating, but bounded (no explosion).
        static IEnumerator TurbulenceOscillation(Check check)
        {
            const float L = 1.1f;
            var steady = new float[1]; var steadyR = new float[1];
            var turbo = new float[1]; var turboR = new float[1];
            yield return MeasureWindActivity(0f, steady, steadyR);
            yield return MeasureWindActivity(1f, turbo, turboR);

            check.Less("steady_settles", steady[0], 0.003f);
            check.Greater("turbulence_keeps_moving", turbo[0], steady[0] + 0.003f);
            check.Less("turbulence_bounded", turboR[0], 3f * L);

            check.Metric("activity_steady", steady[0]);
            check.Metric("activity_turbulent", turbo[0]);
            check.Metric("maxRadius_turbulent", turboR[0]);
        }

        static IEnumerator MeasureZoneDeflection(float3 zoneCenter, float radius, float[] outTipX)
        {
            var extra = new List<GameObject>();
            var go = new GameObject("SphereWindZone");
            go.transform.position = (Vector3)zoneCenter;
            extra.Add(go);
            var wz = go.AddComponent<MagicaWindZone>();
            wz.mode = MagicaWindZone.Mode.SphereDirection;
            wz.radius = radius;
            wz.main = 15f;
            wz.turbulence = 0f;
            wz.directionAngleY = 90f; // +X

            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 5f;
                sd.damping.SetValue(0.1f);
                sd.wind.influence = 2f;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(400);
            var p = ClothState.Positions(rig.cloth);
            outTipX[0] = p[p.Length - 1].x - p[0].x;
            rig.Dispose();
            foreach (var g in extra) if (g != null) Object.Destroy(g);
        }

        // 6.2 A SphereDirection wind zone only affects cloth inside its volume: a chain enclosed by the
        // zone deflects downwind, while the same chain with the zone placed far away does not.
        static IEnumerator SphereZoneContainment(Check check)
        {
            var inside = new float[1];
            var outside = new float[1];
            yield return MeasureZoneDeflection(new float3(0, -0.5f, 0), 2.0f, inside);    // encloses the chain
            yield return MeasureZoneDeflection(new float3(10, 0, 0), 1.0f, outside);      // far away

            check.Greater("inside_zone_deflects", inside[0], 0.1f);
            check.Less("outside_zone_unaffected", outside[0], 0.02f);

            check.Metric("tipX_inside", inside[0]);
            check.Metric("tipX_outside", outside[0]);
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
