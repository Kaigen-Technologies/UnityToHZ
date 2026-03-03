using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity Mesh as a ModelBlobAsset (.hza) with one MeshBlobAsset inside.
    /// Supports both static and skinned meshes.
    /// </summary>
    public static class HZMeshExporter
    {
        // C struct sizes (must match engine exactly)
        const int SIZEOF_MODEL_BLOB_ASSET = 176; // 48 header + 4+4 + 8 pad + 64 mat4 + 16*3 BlobPtrs
        const int SIZEOF_MESH_BLOB_ASSET = 168;  // 8+8+4+4+4+12+12 + 16*5 + 4 + 16*2
        const int SIZEOF_JOINT_BLOB_ASSET = 96;  // 64 mat4 + 4 parent_index + 12 pad + 16 BlobPtr
        const int SIZEOF_STRING_BLOB = 8;

        /// <summary>Static mesh export (no skinning).</summary>
        public static void Export(Mesh mesh, string materialPath, string outputPath)
        {
            ExportInternal(mesh, materialPath, outputPath, null);
        }

        /// <summary>Skinned mesh export with skeleton data.</summary>
        public static void Export(SkinnedMeshRenderer smr, string materialPath, string outputPath)
        {
            var mesh = smr.sharedMesh;
            ExportInternal(mesh, materialPath, outputPath, smr);
        }

        static void ExportInternal(Mesh mesh, string materialPath, string outputPath, SkinnedMeshRenderer smr)
        {
            bool skinned = smr != null;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var triangles = mesh.triangles;
            int vertexCount = mesh.vertexCount;
            int indexCount = triangles.Length;
            bool useU16 = vertexCount <= 65535;
            string meshName = mesh.name ?? "mesh";
            if (materialPath == null) materialPath = "";

            // --- Convert vertex data (negate X for LH→RH) ---
            float[] positions = new float[vertexCount * 3];
            float[] norms = new float[vertexCount * 3];
            float[] tans = new float[vertexCount * 4];
            float[] uv = new float[vertexCount * 2];

            for (int i = 0; i < vertexCount; i++)
            {
                positions[i * 3 + 0] = -vertices[i].x;
                positions[i * 3 + 1] = vertices[i].y;
                positions[i * 3 + 2] = vertices[i].z;

                if (i < normals.Length)
                {
                    norms[i * 3 + 0] = -normals[i].x;
                    norms[i * 3 + 1] = normals[i].y;
                    norms[i * 3 + 2] = normals[i].z;
                }

                if (i < tangents.Length)
                {
                    tans[i * 4 + 0] = -tangents[i].x;
                    tans[i * 4 + 1] = tangents[i].y;
                    tans[i * 4 + 2] = tangents[i].z;
                    tans[i * 4 + 3] = tangents[i].w;
                }

                if (i < uvs.Length)
                {
                    uv[i * 2 + 0] = uvs[i].x;
                    uv[i * 2 + 1] = uvs[i].y;
                }
            }

            // Flip winding
            int[] indices = new int[indexCount];
            for (int i = 0; i < indexCount; i += 3)
            {
                indices[i + 0] = triangles[i + 0];
                indices[i + 1] = triangles[i + 2];
                indices[i + 2] = triangles[i + 1];
            }

            // AABB
            var bounds = mesh.bounds;
            float aabbMinX = -bounds.max.x;
            float aabbMinY = bounds.min.y;
            float aabbMinZ = bounds.min.z;
            float aabbMaxX = -bounds.min.x;
            float aabbMaxY = bounds.max.y;
            float aabbMaxZ = bounds.max.z;

            // Build index data bytes
            byte[] indexData;
            int indexTypeSize;
            string indexTypeName;
            if (useU16)
            {
                indexData = new byte[indexCount * 2];
                for (int i = 0; i < indexCount; i++)
                {
                    ushort v = (ushort)indices[i];
                    indexData[i * 2 + 0] = (byte)(v & 0xFF);
                    indexData[i * 2 + 1] = (byte)(v >> 8);
                }
                indexTypeSize = 2;
                indexTypeName = "u16";
            }
            else
            {
                indexData = new byte[indexCount * 4];
                for (int i = 0; i < indexCount; i++)
                {
                    uint v = (uint)indices[i];
                    indexData[i * 4 + 0] = (byte)(v & 0xFF);
                    indexData[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
                    indexData[i * 4 + 2] = (byte)((v >> 16) & 0xFF);
                    indexData[i * 4 + 3] = (byte)(v >> 24);
                }
                indexTypeSize = 4;
                indexTypeName = "u32";
            }

            // --- Skinning data ---
            Transform[] bones = null;
            int jointCount = 0;
            byte[] jointIndicesData = null;
            float[] jointWeightsData = null;
            Matrix4x4[] bindPoses = null;
            int[] parentIndices = null;
            List<int>[] childrenLists = null;

            if (skinned)
            {
                bones = smr.bones;
                jointCount = bones.Length;
                bindPoses = mesh.bindposes;

                // Bone weights: 4 indices (u8) + 4 weights (f32) per vertex
                var boneWeights = mesh.boneWeights;
                jointIndicesData = new byte[vertexCount * 4];
                jointWeightsData = new float[vertexCount * 4];

                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    jointIndicesData[i * 4 + 0] = (byte)bw.boneIndex0;
                    jointIndicesData[i * 4 + 1] = (byte)bw.boneIndex1;
                    jointIndicesData[i * 4 + 2] = (byte)bw.boneIndex2;
                    jointIndicesData[i * 4 + 3] = (byte)bw.boneIndex3;

                    jointWeightsData[i * 4 + 0] = bw.weight0;
                    jointWeightsData[i * 4 + 1] = bw.weight1;
                    jointWeightsData[i * 4 + 2] = bw.weight2;
                    jointWeightsData[i * 4 + 3] = bw.weight3;
                }

                // Build hierarchy: parent index + children list per bone
                var boneToIndex = new Dictionary<Transform, int>();
                for (int i = 0; i < jointCount; i++)
                    boneToIndex[bones[i]] = i;

                parentIndices = new int[jointCount];
                childrenLists = new List<int>[jointCount];
                for (int i = 0; i < jointCount; i++)
                    childrenLists[i] = new List<int>();

                for (int i = 0; i < jointCount; i++)
                {
                    var parent = bones[i].parent;
                    if (parent != null && boneToIndex.TryGetValue(parent, out int pi))
                    {
                        parentIndices[i] = pi;
                        childrenLists[pi].Add(i);
                    }
                    else
                    {
                        parentIndices[i] = -1;
                    }
                }
            }

            // --- Write blob ---
            var w = new HZBlobWriter(1024 * 1024);
            int modelBase = w.Position;

            // === ModelBlobAsset (176 bytes) ===
            w.WriteHeader("ModelBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no deps

            w.WriteU32(1);                    // mesh_count
            w.WriteU32((uint)jointCount);     // joint_count
            w.WriteZeros(8);                  // padding for mat4 alignment

            // armature_transform
            if (skinned)
            {
                // Use the root bone's local-to-world, converted
                var root = FindRootBone(bones);
                WriteMat4Converted(w, root != null ? root.localToWorldMatrix : Matrix4x4.identity);
            }
            else
            {
                w.WriteMat4Identity();
            }

            int meshesPtrPos = w.ReserveBlobPtr();    // meshes
            int jointsPtrPos = w.ReserveBlobPtr();    // joints
            int jointNamesPtrPos = w.ReserveBlobPtr(); // joint_names

            Debug.Assert(w.Position == SIZEOF_MODEL_BLOB_ASSET,
                $"ModelBlobAsset size mismatch: expected {SIZEOF_MODEL_BLOB_ASSET}, got {w.Position}");

            // === MeshBlobAsset (168 bytes) ===
            int meshBase = w.Position;
            w.PatchBlobPtr(meshesPtrPos, modelBase, meshBase, 1, SIZEOF_MESH_BLOB_ASSET, "MeshBlobAsset");

            int namePos = w.ReserveStringBlob();
            int matPos = w.ReserveStringBlob();

            w.WriteU32(useU16 ? 0u : 1u);
            w.WriteU32((uint)indexCount);
            w.WriteU32((uint)vertexCount);
            w.WriteVec3(aabbMinX, aabbMinY, aabbMinZ);
            w.WriteVec3(aabbMaxX, aabbMaxY, aabbMaxZ);

            int indicesPtrPos = w.ReserveBlobPtr();
            int positionsPtrPos = w.ReserveBlobPtr();
            int normalsPtrPos = w.ReserveBlobPtr();
            int tangentsPtrPos = w.ReserveBlobPtr();
            int uvsPtrPos = w.ReserveBlobPtr();

            w.WriteU32(skinned ? 1u : 0u); // has_skin

            int jointIdxPtrPos = w.ReserveBlobPtr();
            int jointWtPtrPos = w.ReserveBlobPtr();

            if (!skinned)
            {
                // Patch empty BlobPtrs for non-skinned
                w.PatchBlobPtr(jointIdxPtrPos, meshBase, 0, 0, 1, "u8");
                w.PatchBlobPtr(jointWtPtrPos, meshBase, 0, 0, 4, "f32");
                // Write empty with correct format (offset=0, size=0)
                PatchEmptyBlobPtr(w, jointIdxPtrPos, 1, "u8");
                PatchEmptyBlobPtr(w, jointWtPtrPos, 4, "f32");
            }

            Debug.Assert(w.Position == meshBase + SIZEOF_MESH_BLOB_ASSET,
                $"MeshBlobAsset size mismatch: expected {SIZEOF_MESH_BLOB_ASSET}, got {w.Position - meshBase}");

            // === JointBlobAsset array ===
            int jointsArrayPos = w.Position;
            int[] jointChildrenPtrPositions = null;

            if (skinned)
            {
                w.PatchBlobPtr(jointsPtrPos, modelBase, jointsArrayPos, jointCount, SIZEOF_JOINT_BLOB_ASSET, "JointBlobAsset");

                jointChildrenPtrPositions = new int[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    int jointBase = w.Position;

                    // inverse_bind_matrix (64 bytes) — convert coordinate system
                    WriteMat4Converted(w, bindPoses[i]);

                    // parent_index (4 bytes)
                    w.WriteI32(parentIndices[i]);

                    // children BlobPtr (16 bytes) — reserve, patch later
                    jointChildrenPtrPositions[i] = w.ReserveBlobPtr();

                    // 12 bytes padding (struct padded to 96 for mat4 alignment)
                    w.WriteZeros(12);

                    Debug.Assert(w.Position == jointBase + SIZEOF_JOINT_BLOB_ASSET,
                        $"JointBlobAsset[{i}] size mismatch: expected {SIZEOF_JOINT_BLOB_ASSET}, got {w.Position - jointBase}");
                }
            }
            else
            {
                PatchEmptyBlobPtr(w, jointsPtrPos, SIZEOF_JOINT_BLOB_ASSET, "JointBlobAsset");
            }

            // === Joint name StringBlobs (8 bytes each, just the structs) ===
            int jointNamesArrayPos = w.Position;
            int[] jointNameSBPositions = null;

            if (skinned)
            {
                w.PatchBlobPtr(jointNamesPtrPos, modelBase, jointNamesArrayPos, jointCount, SIZEOF_STRING_BLOB, "StringBlob");

                jointNameSBPositions = new int[jointCount];
                for (int i = 0; i < jointCount; i++)
                    jointNameSBPositions[i] = w.ReserveStringBlob();
            }
            else
            {
                PatchEmptyBlobPtr(w, jointNamesPtrPos, SIZEOF_STRING_BLOB, "StringBlob");
            }

            // === String data (mesh name, material path, joint names) ===
            w.WriteAndPatchStringBlob(namePos, meshBase, meshName);
            w.WriteAndPatchStringBlob(matPos, meshBase, materialPath);

            if (skinned)
            {
                for (int i = 0; i < jointCount; i++)
                    w.WriteAndPatchStringBlob(jointNameSBPositions[i], jointNamesArrayPos + i * SIZEOF_STRING_BLOB, bones[i].name);
            }

            // === Joint children data ===
            if (skinned)
            {
                for (int i = 0; i < jointCount; i++)
                {
                    var children = childrenLists[i];
                    if (children.Count > 0)
                    {
                        int childDataPos = w.Position;
                        int jointBase = jointsArrayPos + i * SIZEOF_JOINT_BLOB_ASSET;
                        w.PatchBlobPtr(jointChildrenPtrPositions[i], jointBase, childDataPos, children.Count, 4, "u32");

                        for (int c = 0; c < children.Count; c++)
                            w.WriteU32((uint)children[c]);
                    }
                    else
                    {
                        int jointBase = jointsArrayPos + i * SIZEOF_JOINT_BLOB_ASSET;
                        PatchEmptyBlobPtr(w, jointChildrenPtrPositions[i], 4, "u32");
                    }
                }
            }

            // === Align to 16 before vertex data ===
            w.AlignTo(16);

            // === Vertex/index data ===
            int indicesDataPos = w.Position;
            w.WriteBytes(indexData);
            w.PatchBlobPtr(indicesPtrPos, meshBase, indicesDataPos, indexData.Length / indexTypeSize, indexTypeSize, indexTypeName);

            int posDataPos = w.Position;
            WriteFloatArray(w, positions);
            w.PatchBlobPtr(positionsPtrPos, meshBase, posDataPos, vertexCount * 3, 4, "f32");

            int normDataPos = w.Position;
            WriteFloatArray(w, norms);
            w.PatchBlobPtr(normalsPtrPos, meshBase, normDataPos, vertexCount * 3, 4, "f32");

            int tanDataPos = w.Position;
            WriteFloatArray(w, tans);
            w.PatchBlobPtr(tangentsPtrPos, meshBase, tanDataPos, vertexCount * 4, 4, "f32");

            int uvDataPos = w.Position;
            WriteFloatArray(w, uv);
            w.PatchBlobPtr(uvsPtrPos, meshBase, uvDataPos, vertexCount * 2, 4, "f32");

            // === Skinning vertex data ===
            if (skinned)
            {
                int jiDataPos = w.Position;
                w.WriteBytes(jointIndicesData);
                w.PatchBlobPtr(jointIdxPtrPos, meshBase, jiDataPos, vertexCount * 4, 1, "u8");

                int jwDataPos = w.Position;
                WriteFloatArray(w, jointWeightsData);
                w.PatchBlobPtr(jointWtPtrPos, meshBase, jwDataPos, vertexCount * 4, 4, "f32");
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(modelBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);

            string skinLabel = skinned ? $", skinned with {jointCount} joints" : "";
            Debug.Log($"[HZMeshExporter] Exported '{meshName}' → {outputPath} ({finalSize} bytes, {vertexCount} verts, {indexCount} indices{skinLabel})");
        }

        /// <summary>
        /// Write a mat4 with X-axis coordinate conversion (negate X row and column).
        /// Column-major order matching cglm.
        /// </summary>
        static void WriteMat4Converted(HZBlobWriter w, Matrix4x4 m)
        {
            // Unity Matrix4x4 is column-major: m[row, col]
            // cglm mat4 is column-major: mat4[col][row]
            // Negate X: negate row 0 and col 0, but double-negate [0,0] cancels out
            // Row 0 = X row, Col 0 = X column
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    float v = m[row, col];
                    bool negateRow = (row == 0);
                    bool negateCol = (col == 0);
                    // XOR: negate if exactly one of row/col is 0
                    if (negateRow != negateCol)
                        v = -v;
                    w.WriteF32(v);
                }
            }
        }

        static Transform FindRootBone(Transform[] bones)
        {
            var boneSet = new HashSet<Transform>(bones);
            foreach (var bone in bones)
            {
                if (bone.parent == null || !boneSet.Contains(bone.parent))
                    return bone;
            }
            return bones.Length > 0 ? bones[0] : null;
        }

        static void PatchEmptyBlobPtr(HZBlobWriter w, int pos, int typeSize, string typeName)
        {
            long saved = w.Position;
            w.PatchU32(pos, 0);
            w.PatchU32(pos + 4, 0);
            w.PatchU32(pos + 8, (uint)typeSize);
            w.PatchU32(pos + 12, (uint)HZBlobWriter.TypeHash(typeName, typeSize));
        }

        static void WriteFloatArray(HZBlobWriter w, float[] data)
        {
            for (int i = 0; i < data.Length; i++)
                w.WriteF32(data[i]);
        }

        static int Align4(int size) => (size + 3) & ~3;
        static int Align16(int size) => (size + 15) & ~15;
    }
}
