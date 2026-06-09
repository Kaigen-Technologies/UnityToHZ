// MagicaCloth2 characterization test suite — Phase 7: inertia / teleport / anchor.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class InertiaTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("inertia.world_inertia_lag", WorldInertiaLag));
        }

        // Settle a hanging chain, then translate its holder sideways over several frames; report how far
        // the tip lags behind the (pinned) root in X. Higher worldInertia => more lag.
        static IEnumerator MeasureLag(float worldInertia, float[] outLag)
        {
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 2f;
                sd.damping.SetValue(0.15f);
                sd.inertiaConstraint.worldInertia = worldInertia;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(60); // settle hanging

            // move BOTH the bone holder and the cloth component transform together, so the cloth's whole
            // reference frame translates (this is what world inertia responds to).
            var holder = rig.objects[0].transform;     // ChainHolder (bones)
            var clothTf = rig.cloth.transform;          // ClothTransform
            for (int i = 0; i < 25; i++)
            {
                var d = new Vector3(0.03f, 0, 0);
                holder.position += d;
                clothTf.position += d;
                yield return null;
            }

            var p = ClothState.Positions(rig.cloth);
            outLag[0] = p[0].x - p[p.Length - 1].x; // root has moved ahead; tip trails behind
            rig.Dispose();
        }

        // 7.1 World inertia controls how much whole-body movement is left behind: a chain with high
        // worldInertia lags much further behind a sideways-moving anchor than one with low worldInertia.
        static IEnumerator WorldInertiaLag(Check check)
        {
            var lowI = new float[1];
            var highI = new float[1];
            yield return MeasureLag(0.0f, lowI);
            yield return MeasureLag(1.0f, highI);

            check.Greater("more_inertia_more_lag", highI[0], lowI[0] + 0.05f);

            check.Metric("lag_worldInertia0", lowI[0]);
            check.Metric("lag_worldInertia1", highI[0]);
        }
    }
}
