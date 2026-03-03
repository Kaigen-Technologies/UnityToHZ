using System.IO;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity Mesh as a ModelBlobAsset (.hza) with one MeshBlobAsset inside.
    /// Static meshes only (no skinning) for now.
    /// </summary>
    public static class HZMeshExporter
    {
        // C struct sizes (must match engine exactly)
        // ModelBlobAsset: 48 header + 4+4 + 8 padding (mat4 has 16-byte alignment via CGLM_ALIGN_MAT) + 64 mat4 + 16*3 BlobPtrs = 176
        const int SIZEOF_MODEL_BLOB_ASSET = 176;
        // MeshBlobAsset: 8+8+4+4+4+12+12 + 16*5 indices/pos/norm/tan/uv + 4 has_skin + 16*2 joint_idx/weights = 168
        const int SIZEOF_MESH_BLOB_ASSET = 168;
        const int SIZEOF_STRING_BLOB = 8;

        public static void Export(Mesh mesh, string materialPath, string outputPath)
        {
            // Extract mesh data
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var triangles = mesh.triangles;
            int vertexCount = mesh.vertexCount;
            int indexCount = triangles.Length;
            bool useU16 = vertexCount <= 65535;

            // Convert coordinate system: Unity LH Y-up → Engine RH Y-up (negate Z)
            // Also flip winding order (swap tri[1] and tri[2])
            float[] positions = new float[vertexCount * 3];
            float[] norms = new float[vertexCount * 3];
            float[] tans = new float[vertexCount * 4];
            float[] uv = new float[vertexCount * 2];

            for (int i = 0; i < vertexCount; i++)
            {
                positions[i * 3 + 0] = vertices[i].x;
                positions[i * 3 + 1] = vertices[i].y;
                positions[i * 3 + 2] = -vertices[i].z; // negate Z

                if (i < normals.Length)
                {
                    norms[i * 3 + 0] = normals[i].x;
                    norms[i * 3 + 1] = normals[i].y;
                    norms[i * 3 + 2] = -normals[i].z; // negate Z
                }

                if (i < tangents.Length)
                {
                    tans[i * 4 + 0] = tangents[i].x;
                    tans[i * 4 + 1] = tangents[i].y;
                    tans[i * 4 + 2] = -tangents[i].z; // negate Z
                    tans[i * 4 + 3] = tangents[i].w;  // keep handedness
                }

                if (i < uvs.Length)
                {
                    uv[i * 2 + 0] = uvs[i].x;
                    uv[i * 2 + 1] = uvs[i].y;
                }
            }

            // Flip winding: swap indices [1] and [2] per triangle
            int[] indices = new int[indexCount];
            for (int i = 0; i < indexCount; i += 3)
            {
                indices[i + 0] = triangles[i + 0];
                indices[i + 1] = triangles[i + 2]; // swapped
                indices[i + 2] = triangles[i + 1]; // swapped
            }

            // AABB with converted coords
            var bounds = mesh.bounds;
            float aabbMinX = bounds.min.x;
            float aabbMinY = bounds.min.y;
            float aabbMinZ = -bounds.max.z; // negate & swap
            float aabbMaxX = bounds.max.x;
            float aabbMaxY = bounds.max.y;
            float aabbMaxZ = -bounds.min.z; // negate & swap

            // Build index data
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

            // Calculate total size (mirrors exporter.c layout)
            string meshName = mesh.name ?? "mesh";
            if (materialPath == null) materialPath = "";

            int meshesOffset = SIZEOF_MODEL_BLOB_ASSET; // 176
            int stringsOffset = meshesOffset + SIZEOF_MESH_BLOB_ASSET; // 176 + 168 = 344

            // No joints, no deps for now
            int depsOffset = stringsOffset; // deps array goes here (0 deps)

            // Strings: mesh name + material path
            int meshNameAllocSize = Align4(meshName.Length + 1);
            int matPathAllocSize = Align4(materialPath.Length + 1);

            int dataOffset = Align16(stringsOffset + meshNameAllocSize + matPathAllocSize);

            int posSize = vertexCount * 3 * 4;
            int normSize = vertexCount * 3 * 4;
            int tanSize = vertexCount * 4 * 4;
            int uvSize = vertexCount * 2 * 4;

            int totalSize = dataOffset + indexData.Length + posSize + normSize + tanSize + uvSize;

            // Write blob
            var w = new HZBlobWriter(totalSize + 256);

            // --- ModelBlobAsset (168 bytes) ---
            int modelBase = w.Position; // = 0

            // Header (48 bytes)
            w.WriteHeader("ModelBlobAsset");
            // dependencies BlobPtr - no deps
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob");

            // mesh_count, joint_count
            w.WriteU32(1);  // mesh_count
            w.WriteU32(0);  // joint_count

            // 8 bytes padding for mat4 16-byte alignment (CGLM_ALIGN_MAT)
            // offset 56 needs to be padded to offset 64
            w.WriteZeros(8);

            // armature_transform (mat4 identity, 64 bytes)
            w.WriteMat4Identity();

            // meshes BlobPtr
            w.WriteBlobPtr(modelBase, meshesOffset, 1, SIZEOF_MESH_BLOB_ASSET, "MeshBlobAsset");
            // joints BlobPtr (empty)
            w.WriteEmptyBlobPtr(96, "JointBlobAsset"); // sizeof(JointBlobAsset) = 64 mat4 + 4 parent_index + 12 padding + 16 BlobPtr = 96
            // joint_names BlobPtr (empty)
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob");

            // Verify we're at the right position
            Debug.Assert(w.Position == SIZEOF_MODEL_BLOB_ASSET,
                $"ModelBlobAsset size mismatch: expected {SIZEOF_MODEL_BLOB_ASSET}, got {w.Position}");

            // --- MeshBlobAsset (172 bytes) ---
            int meshBase = w.Position;

            // name StringBlob (8 bytes) - will patch
            int namePos = w.ReserveStringBlob();
            // material StringBlob (8 bytes) - will patch
            int matPos = w.ReserveStringBlob();

            // index_format
            w.WriteU32(useU16 ? 0u : 1u); // INDEX_FORMAT_U16 = 0, U32 = 1
            w.WriteU32((uint)indexCount);
            w.WriteU32((uint)vertexCount);

            // aabb
            w.WriteVec3(aabbMinX, aabbMinY, aabbMinZ);
            w.WriteVec3(aabbMaxX, aabbMaxY, aabbMaxZ);

            // Reserve BlobPtrs for vertex data (will patch after writing data)
            int indicesPtrPos = w.ReserveBlobPtr();
            int positionsPtrPos = w.ReserveBlobPtr();
            int normalsPtrPos = w.ReserveBlobPtr();
            int tangentsPtrPos = w.ReserveBlobPtr();
            int uvsPtrPos = w.ReserveBlobPtr();

            // has_skin
            w.WriteU32(0); // false

            // joint_indices, joint_weights (empty)
            w.WriteEmptyBlobPtr(1, "u8");
            w.WriteEmptyBlobPtr(4, "f32");

            Debug.Assert(w.Position == meshBase + SIZEOF_MESH_BLOB_ASSET,
                $"MeshBlobAsset size mismatch: expected {SIZEOF_MESH_BLOB_ASSET}, got {w.Position - meshBase}");

            // --- String data ---
            w.WriteAndPatchStringBlob(namePos, meshBase, meshName);
            w.WriteAndPatchStringBlob(matPos, meshBase, materialPath);

            // --- Align to 16 before vertex data ---
            w.AlignTo(16);

            // --- Vertex/index data ---
            int indicesDataPos = w.Position;
            w.WriteBytes(indexData);
            w.PatchBlobPtr(indicesPtrPos, meshBase, indicesDataPos, indexData.Length / indexTypeSize, indexTypeSize, indexTypeName);

            int posDataPos = w.Position;
            WriteFloatArray(w, positions);
            w.PatchBlobPtr(positionsPtrPos, meshBase, posDataPos, posSize / 4, 4, "f32");

            int normDataPos = w.Position;
            WriteFloatArray(w, norms);
            w.PatchBlobPtr(normalsPtrPos, meshBase, normDataPos, normSize / 4, 4, "f32");

            int tanDataPos = w.Position;
            WriteFloatArray(w, tans);
            w.PatchBlobPtr(tangentsPtrPos, meshBase, tanDataPos, tanSize / 4, 4, "f32");

            int uvDataPos = w.Position;
            WriteFloatArray(w, uv);
            w.PatchBlobPtr(uvsPtrPos, meshBase, uvDataPos, uvSize / 4, 4, "f32");

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(modelBase + 8, (ulong)finalSize);

            // Write
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
            Debug.Log($"[HZMeshExporter] Exported '{meshName}' → {outputPath} ({finalSize} bytes, {vertexCount} verts, {indexCount} indices)");
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
