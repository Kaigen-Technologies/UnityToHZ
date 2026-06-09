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
            list.Add(new TestEntry("collision.self_collision", SelfCollision));
            // NOTE: Collider friction and the Point-vs-Edge collision mode are not tested in isolation here.
            // With synthetic chain/sheet rigs a chain slides off a sphere regardless of friction, so the
            // friction ordinal lands below the cross-build noise floor (~0.004). Robustly demonstrating
            // either needs a clinging hair-on-curved-surface rig. Collider symmetry is likewise deferred
            // (needs a humanoid Animator or explicit symmetry target). The core push-out invariants are
            // covered by the sphere/capsule/plane tests above.
        }

        // Two parallel, unconnected bone chains (one cloth, Line connection so the columns don't link).
        // Self-collision should push the columns apart to the surface thickness; without it they stay at
        // their initial column spacing. Partition particles into the two columns by base-pose X.
        static IEnumerator MeasureColumnGap(bool selfCollision, float thickness, float[] outMinGap)
        {
            var rig = Build.BoneGrid(2, 8, 0.03f, sd =>
            {
                sd.connectionMode = RenderSetupData.BoneConnectionMode.Line;
                sd.gravity = 3f;
                sd.damping.SetValue(0.1f);
                sd.angleRestorationConstraint.useAngleRestoration = false;
                sd.selfCollisionConstraint.selfMode = selfCollision
                    ? SelfCollisionConstraint.SelfCollisionMode.FullMesh
                    : SelfCollisionConstraint.SelfCollisionMode.None;
                if (selfCollision) sd.selfCollisionConstraint.surfaceThickness.SetValue(thickness);
            });
            yield return Build.WaitForBuild(rig.cloth);

            // partition by base X, then find each column's tip (lowest base Y, a free particle)
            var baseP = ClothState.BasePositions(rig.cloth);
            int tip0 = -1, tip1 = -1;
            float y0 = float.MaxValue, y1 = float.MaxValue;
            for (int i = 0; i < baseP.Length; i++)
            {
                if (baseP[i].x < 0.015f) { if (baseP[i].y < y0) { y0 = baseP[i].y; tip0 = i; } }
                else { if (baseP[i].y < y1) { y1 = baseP[i].y; tip1 = i; } }
            }

            yield return Build.Step(400);
            var p = ClothState.Positions(rig.cloth);
            outMinGap[0] = math.distance(p[tip0], p[tip1]); // gap between the two free tips
            rig.Dispose();
        }

        // 5.7 Self-collision keeps two close, unconnected cloth columns from interpenetrating: with it ON
        // the minimum gap between the columns is held near the surface thickness; with it OFF they stay at
        // (or collapse below) their initial 0.03 spacing.
        static IEnumerator SelfCollision(Check check)
        {
            const float thickness = 0.05f;
            var on = new float[1];
            var off = new float[1];
            yield return MeasureColumnGap(true, thickness, on);
            yield return MeasureColumnGap(false, thickness, off);

            check.Greater("self_collision_separates", on[0], off[0] + 0.01f);

            check.Metric("minGap_selfCollisionOn", on[0]);
            check.Metric("minGap_selfCollisionOff", off[0]);
            check.Metric("thickness", thickness);
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
