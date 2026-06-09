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
            list.Add(new TestEntry("inertia.teleport_reset_vs_none", TeleportResetVsNone));
            // NOTE: the inertia Anchor and the movement/rotation Speed Limits are not tested in isolation
            // here. Because the cloth simulates in its component-transform LOCAL space, translating the
            // whole rig is largely transparent, so the anchor's motion-cancellation and the speed-limit
            // anti-explosion behaviors don't produce a robust supra-noise signal with these synthetic rigs
            // (they need a parent-rig / animated-character setup). The core inertia behavior is covered by
            // inertia.world_inertia_lag and the teleport transient by inertia.teleport_reset_vs_none.
        }

        static float MaxEdgeStretch(float3[] p, float rest)
        {
            float max = 0f;
            for (int i = 0; i + 1 < p.Length; i++) max = math.max(max, math.distance(p[i], p[i + 1]) / rest);
            return max;
        }

        // Settle a chain, jump its whole frame +2m in X, then take ONE step; report the worst edge stretch.
        // TeleportMode.Reset re-bases the cloth so the world-movement transient produces no stretch; None
        // lets the one-frame world-inertia transient stretch the edges.
        static IEnumerator MeasureTeleportStretch(InertiaConstraint.TeleportMode mode, float[] outStretch)
        {
            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 2f;
                sd.damping.SetValue(0.1f);
                sd.inertiaConstraint.teleportMode = mode;
                sd.inertiaConstraint.teleportDistance = 0.5f;
            });
            yield return Build.WaitForBuild(rig.cloth);
            yield return Build.Step(60);

            rig.objects[0].transform.position += new Vector3(2f, 0, 0);
            rig.cloth.transform.position += new Vector3(2f, 0, 0);
            yield return null; // single step right after the jump

            outStretch[0] = MaxEdgeStretch(ClothState.Positions(rig.cloth), 0.1f);
            rig.Dispose();
        }

        // 7.3 A large one-frame teleport (+2m): TeleportMode.Reset re-bases the cloth so no stretch
        // transient appears; TeleportMode.None lets the world-inertia transient stretch the edges.
        static IEnumerator TeleportResetVsNone(Check check)
        {
            var reset = new float[1];
            var none = new float[1];
            yield return MeasureTeleportStretch(InertiaConstraint.TeleportMode.Reset, reset);
            yield return MeasureTeleportStretch(InertiaConstraint.TeleportMode.None, none);

            check.Less("reset_no_transient", reset[0], 1.02f);          // re-based: edges stay at rest
            check.Greater("none_has_transient", none[0], reset[0] + 0.02f); // not re-based: stretch spike

            check.Metric("maxStretch_reset", reset[0]);
            check.Metric("maxStretch_none", none[0]);
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
