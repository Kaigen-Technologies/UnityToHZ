// MagicaCloth2 characterization test suite — Phase 0 smoke test.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    /// <summary>
    /// Proves the whole pipeline works end to end: code-build a BoneCloth, it becomes valid, and the
    /// proxy/particle state is readable.
    /// </summary>
    public static class SmokeTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("smoke.bonechain_builds", BoneChainBuilds));
        }

        static IEnumerator BoneChainBuilds(Check check)
        {
            const int boneCount = 8;
            var rig = Build.BoneChain(boneCount, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 0f; // static: just verifying construction, not motion
            });

            yield return Build.WaitForBuild(rig.cloth);

            check.IsTrue("valid", rig.cloth.IsValid());
            check.Greater("teamId", rig.cloth.Process.TeamId, 0);

            int particles = ClothState.ParticleCount(rig.cloth);
            int proxyVerts = ClothState.ProxyVertexCount(rig.cloth);
            check.Approx("particle_count==bones", particles, boneCount, 0);
            check.Approx("proxy_verts==bones", proxyVerts, boneCount, 0);

            var pos = ClothState.Positions(rig.cloth);
            check.NoNaN("no_nan", pos);

            check.Metric("particles", particles);
            check.Metric("proxyVerts", proxyVerts);
            check.Metric("L", ClothState.CharacteristicLength(rig.cloth));
            check.Metric("spacing", ClothState.Spacing(rig.cloth));

            rig.Dispose();
        }
    }
}
