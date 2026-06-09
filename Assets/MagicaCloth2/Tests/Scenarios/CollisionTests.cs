// MagicaCloth2 characterization test suite — Phase 5: collision.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class CollisionTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("collision.sphere_no_penetration", SphereNoPenetration));
        }

        // Creates a sphere collider GameObject at a world position with a given radius.
        static MagicaSphereCollider MakeSphere(List<GameObject> track, float3 center, float radius)
        {
            var go = new GameObject("SphereCollider");
            go.transform.position = (Vector3)center;
            track.Add(go);
            var col = go.AddComponent<MagicaSphereCollider>();
            col.SetSize(radius);
            return col;
        }

        // Min distance from any particle to a point.
        static float MinDistanceTo(float3[] p, float3 c)
        {
            float m = float.MaxValue;
            for (int i = 0; i < p.Length; i++) m = math.min(m, math.distance(p[i], c));
            return m;
        }

        // 5.1 A hanging chain draped against a sphere collider must not penetrate it: every particle's
        // centre stays at least (sphereRadius + particleRadius) from the sphere centre.
        static IEnumerator SphereNoPenetration(Check check)
        {
            float3 sphereCenter = new float3(0.1f, -0.5f, 0f);
            const float sphereRadius = 0.2f;
            const float particleRadius = 0.02f;
            var extra = new List<GameObject>();
            var collider = MakeSphere(extra, sphereCenter, sphereRadius);

            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 8f;
                sd.damping.SetValue(0.1f);
                sd.radius.SetValue(particleRadius);
                sd.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Point;
                sd.colliderCollisionConstraint.colliderList.Add(collider);
            });
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());

            yield return Build.Step(400);
            var p = ClothState.Positions(rig.cloth);
            float minDist = MinDistanceTo(p, sphereCenter);
            float surface = sphereRadius + particleRadius;

            check.NoNaN("no_nan", p);
            check.AtLeast("no_penetration", minDist, surface, 0.05f * particleRadius);

            check.Metric("minDistToCenter", minDist);
            check.Metric("requiredSurface", surface);

            rig.Dispose();
            foreach (var go in extra) if (go != null) Object.Destroy(go);
        }
    }
}
