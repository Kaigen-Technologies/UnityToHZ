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
            list.Add(new TestEntry("collision.capsule_no_penetration", CapsuleNoPenetration));
            list.Add(new TestEntry("collision.plane_keeps_above", PlaneKeepsAbove));
        }

        // 5.3 A MagicaPlaneCollider (faces +Y) stops a falling chain at the plane: no particle goes
        // below the plane height (minus particle radius). With collision off the chain falls through.
        static IEnumerator PlaneKeepsAbove(Check check)
        {
            const float planeY = -0.8f;
            const float particleRadius = 0.02f;
            var extra = new List<GameObject>();
            var planeGo = new GameObject("PlaneCollider");
            planeGo.transform.position = new Vector3(0, planeY, 0);
            extra.Add(planeGo);
            var plane = planeGo.AddComponent<MagicaPlaneCollider>();

            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 6f;
                sd.damping.SetValue(0.1f);
                sd.radius.SetValue(particleRadius);
                sd.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Point;
                sd.colliderCollisionConstraint.colliderList.Add(plane);
            });
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());
            yield return Build.Step(400);

            var p = ClothState.Positions(rig.cloth);
            float minY = float.MaxValue;
            for (int i = 0; i < p.Length; i++) minY = math.min(minY, p[i].y);

            check.NoNaN("no_nan", p);
            check.AtLeast("stays_above_plane", minY, planeY - particleRadius, 0.05f * particleRadius);

            check.Metric("minY", minY);
            check.Metric("planeY", planeY);

            rig.Dispose();
            foreach (var go in extra) if (go != null) Object.Destroy(go);
        }

        static float DistToSegment(float3 p, float3 a, float3 b)
        {
            float3 ab = b - a;
            float t = math.clamp(math.dot(p - a, ab) / math.max(math.dot(ab, ab), 1e-8f), 0f, 1f);
            return math.distance(p, a + t * ab);
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
                sd.gravity = 4f;
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

        // 5.2 A hanging chain draped over a horizontal capsule must not penetrate it: every particle stays
        // at least (capsuleRadius + particleRadius) from the capsule's central segment. Also verifies a
        // tapered capsule (separate start/end radii) builds and collides without NaN.
        static IEnumerator CapsuleNoPenetration(Check check)
        {
            const float particleRadius = 0.02f;
            const float capR = 0.15f;
            // offset in Z so the chain slides off and drapes tangentially rather than balancing on the axis
            float3 capCenter = new float3(0f, -0.5f, 0.08f);
            float3 segA = capCenter + new float3(-0.6f, 0, 0); // direction X, length 1.2, centered
            float3 segB = capCenter + new float3(0.6f, 0, 0);

            var extra = new List<GameObject>();
            var capGo = new GameObject("CapsuleCollider");
            capGo.transform.position = (Vector3)capCenter;
            extra.Add(capGo);
            var cap = capGo.AddComponent<MagicaCapsuleCollider>();
            cap.direction = MagicaCapsuleCollider.Direction.X;
            cap.alignedOnCenter = true;
            cap.SetSize(capR, capR, 1.2f); // uniform radius

            var rig = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 4f;
                sd.damping.SetValue(0.1f);
                sd.radius.SetValue(particleRadius);
                sd.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Point;
                sd.colliderCollisionConstraint.colliderList.Add(cap);
            });
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());
            yield return Build.Step(400);

            var p = ClothState.Positions(rig.cloth);
            float minDist = float.MaxValue;
            for (int i = 0; i < p.Length; i++) minDist = math.min(minDist, DistToSegment(p[i], segA, segB));
            check.NoNaN("no_nan", p);
            check.AtLeast("no_penetration", minDist, capR + particleRadius, 0.05f * particleRadius);
            check.Metric("minDistToSegment", minDist);
            check.Metric("requiredSurface", capR + particleRadius);
            rig.Dispose();
            foreach (var go in extra) if (go != null) Object.Destroy(go);

            // --- tapered capsule: builds and collides, no NaN, stays outside the thin-end radius ---
            var extra2 = new List<GameObject>();
            var taperGo = new GameObject("TaperedCapsule");
            taperGo.transform.position = (Vector3)capCenter;
            extra2.Add(taperGo);
            var taper = taperGo.AddComponent<MagicaCapsuleCollider>();
            taper.direction = MagicaCapsuleCollider.Direction.X;
            taper.alignedOnCenter = true;
            taper.SetSize(0.2f, 0.05f, 0.4f); // tapered: thick start, thin end

            var rig2 = Build.BoneChain(12, new float3(0, -1, 0), 0.1f, sd =>
            {
                sd.gravity = 4f;
                sd.damping.SetValue(0.1f);
                sd.radius.SetValue(particleRadius);
                sd.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Point;
                sd.colliderCollisionConstraint.colliderList.Add(taper);
            });
            yield return Build.WaitForBuild(rig2.cloth);
            yield return Build.Step(400);
            var p2 = ClothState.Positions(rig2.cloth);
            float minDist2 = float.MaxValue;
            for (int i = 0; i < p2.Length; i++) minDist2 = math.min(minDist2, DistToSegment(p2[i], segA, segB));
            check.IsTrue("tapered_valid", rig2.cloth.IsValid());
            check.NoNaN("tapered_no_nan", p2);
            check.AtLeast("tapered_outside_min_radius", minDist2, 0.05f + particleRadius, 0.05f * particleRadius);
            check.Metric("tapered_minDistToSegment", minDist2);
            rig2.Dispose();
            foreach (var go in extra2) if (go != null) Object.Destroy(go);
        }
    }
}
