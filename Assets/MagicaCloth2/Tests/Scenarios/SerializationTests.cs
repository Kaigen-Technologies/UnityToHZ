// MagicaCloth2 characterization test suite — Phase 9: PreBuild & serialization.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class SerializationTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("serialize.json_preset_preserves_rig", JsonPresetPreservesRig));
            list.Add(new TestEntry("serialize.proxy_mesh_roundtrip", ProxyMeshRoundtrip));
        }

        // 9.1 The proxy-mesh serialization (used by PreBuild) round-trips losslessly: serializing a built
        // proxy and deserializing it reproduces the same vertex/edge/triangle counts and vertex positions.
        // This is the binary-format guarantee the engine's PreBuild loader must match.
        static IEnumerator ProxyMeshRoundtrip(Check check)
        {
            var rig = Build.MeshGrid(6, 6, 0.1f, fixedTopV: 0.9f, disableReduction: true, configure: sd => sd.gravity = 0f);
            yield return Build.WaitForBuild(rig.cloth);
            check.IsTrue("valid", rig.cloth.IsValid());

            var proxy = rig.cloth.Process.ProxyMeshContainer.shareVirtualMesh;
            int v0 = proxy.VertexCount, e0 = proxy.EdgeCount, t0 = proxy.TriangleCount;

            var sdata = proxy.ShareSerialize();
            var proxy2 = VirtualMesh.ShareDeserialize(sdata);

            check.Approx("vertex_count_roundtrip", proxy2.VertexCount, v0, 0);
            check.Approx("edge_count_roundtrip", proxy2.EdgeCount, e0, 0);
            check.Approx("triangle_count_roundtrip", proxy2.TriangleCount, t0, 0);

            float maxPosDiff = 0f;
            int n = math.min(proxy.localPositions.Count, proxy2.localPositions.Count);
            for (int i = 0; i < n; i++)
                maxPosDiff = math.max(maxPosDiff, math.distance(proxy.localPositions[i], proxy2.localPositions[i]));
            check.Less("vertex_positions_roundtrip", maxPosDiff, 1e-5f);

            check.Metric("vertexCount", v0);
            check.Metric("edgeCount", e0);
            check.Metric("triangleCount", t0);
            check.Metric("maxPosDiff", maxPosDiff);

            proxy2.Dispose();
            rig.Dispose();
        }

        // 9.2 ImportJson applies preset PARAMETERS only: it overwrites tunables (gravity, stiffness, ...)
        // but preserves structural references (clothType, rootBones, colliders) via the TempBuffer. This
        // is the guarantee the engine's preset/serialization path must replicate.
        static IEnumerator JsonPresetPreservesRig(Check check)
        {
            var extra = new List<GameObject>();

            // target cloth A: a BoneCloth with one root bone and one collider, gravity 5
            var goA = new GameObject("ClothA"); extra.Add(goA);
            var clothA = goA.AddComponent<MagicaCloth>();
            var sdA = clothA.SerializeData;
            sdA.clothType = ClothProcess.ClothType.BoneCloth;
            var boneGo = new GameObject("Bone"); extra.Add(boneGo);
            sdA.rootBones.Add(boneGo.transform);
            var colGo = new GameObject("Col"); extra.Add(colGo);
            var col = colGo.AddComponent<MagicaSphereCollider>();
            sdA.colliderCollisionConstraint.colliderList.Add(col);
            sdA.gravity = 5f;
            sdA.distanceConstraint.stiffness.SetValue(0.3f);

            // a "preset" exported from a differently-tuned cloth B (gravity 9, distance stiffness 0.8)
            var goB = new GameObject("ClothB"); extra.Add(goB);
            var clothB = goB.AddComponent<MagicaCloth>();
            var sdB = clothB.SerializeData;
            sdB.gravity = 9f;
            sdB.distanceConstraint.stiffness.SetValue(0.8f);
            string presetJson = sdB.ExportJson();

            // import the preset onto A
            sdA.ImportJson(presetJson);

            check.Approx("gravity_imported", sdA.gravity, 9f, 0.001f);
            check.Approx("stiffness_imported", sdA.distanceConstraint.stiffness.value, 0.8f, 0.001f);
            check.Approx("clothType_preserved", (int)sdA.clothType, (int)ClothProcess.ClothType.BoneCloth, 0);
            check.Approx("rootBones_preserved", sdA.rootBones.Count, 1, 0);
            check.IsTrue("rootBone_identity_preserved", sdA.rootBones.Count == 1 && sdA.rootBones[0] == boneGo.transform);
            check.Approx("colliders_preserved", sdA.colliderCollisionConstraint.colliderList.Count, 1, 0);

            check.Metric("gravityAfter", sdA.gravity);
            check.Metric("stiffnessAfter", sdA.distanceConstraint.stiffness.value);
            check.Metric("rootBones", sdA.rootBones.Count);
            check.Metric("colliders", sdA.colliderCollisionConstraint.colliderList.Count);

            foreach (var g in extra) if (g != null) Object.Destroy(g);
            yield break;
        }
    }
}
