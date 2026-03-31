using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Writes glTF 2.0 binary (.glb) files for hz-engine-v5.
    /// Engine auto-cooks .glb to compressed .hza at load time.
    /// </summary>
    public static class HZGlbWriter
    {
        const uint GLB_MAGIC = 0x46546C67;
        const uint GLB_VERSION = 2;
        const uint CHUNK_JSON = 0x4E4F534A;
        const uint CHUNK_BIN = 0x004E4942;

        // glTF component types
        const int BYTE = 5120;
        const int UNSIGNED_BYTE = 5121;
        const int SHORT = 5122;
        const int UNSIGNED_SHORT = 5123;
        const int UNSIGNED_INT = 5125;
        const int FLOAT = 5126;

        /// <summary>Export a single static mesh as .glb</summary>
        public static void ExportStaticMesh(Mesh mesh, Material material, string outputPath)
        {
            var bin = new MemoryStream();
            var bufferViews = new List<object>();
            var accessors = new List<object>();

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var triangles = mesh.triangles;
            int vertexCount = mesh.vertexCount;
            int indexCount = triangles.Length;
            bool useU16 = vertexCount <= 65535;

            // --- Convert and write binary data ---

            // Indices (flip winding: swap [1] and [2])
            int indicesOffset = (int)bin.Position;
            if (useU16)
            {
                for (int i = 0; i < indexCount; i += 3)
                {
                    WriteU16(bin, (ushort)triangles[i + 0]);
                    WriteU16(bin, (ushort)triangles[i + 2]);
                    WriteU16(bin, (ushort)triangles[i + 1]);
                }
            }
            else
            {
                for (int i = 0; i < indexCount; i += 3)
                {
                    WriteU32(bin, (uint)triangles[i + 0]);
                    WriteU32(bin, (uint)triangles[i + 2]);
                    WriteU32(bin, (uint)triangles[i + 1]);
                }
            }
            int indicesSize = (int)bin.Position - indicesOffset;
            PadTo4(bin);

            // Positions (negate X for LH→RH)
            int posOffset = (int)bin.Position;
            float[] posMin = { float.MaxValue, float.MaxValue, float.MaxValue };
            float[] posMax = { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < vertexCount; i++)
            {
                float x = -vertices[i].x, y = vertices[i].y, z = vertices[i].z;
                WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z);
                posMin[0] = Math.Min(posMin[0], x); posMin[1] = Math.Min(posMin[1], y); posMin[2] = Math.Min(posMin[2], z);
                posMax[0] = Math.Max(posMax[0], x); posMax[1] = Math.Max(posMax[1], y); posMax[2] = Math.Max(posMax[2], z);
            }
            int posSize = (int)bin.Position - posOffset;

            // Normals (negate X)
            int normOffset = (int)bin.Position;
            for (int i = 0; i < vertexCount; i++)
            {
                float x = (i < normals.Length) ? -normals[i].x : 0;
                float y = (i < normals.Length) ? normals[i].y : 0;
                float z = (i < normals.Length) ? normals[i].z : 1;
                WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z);
            }
            int normSize = (int)bin.Position - normOffset;

            // Tangents (negate X, keep W)
            int tanOffset = (int)bin.Position;
            bool hasTangents = tangents != null && tangents.Length > 0;
            if (hasTangents)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float x = (i < tangents.Length) ? -tangents[i].x : 1;
                    float y = (i < tangents.Length) ? tangents[i].y : 0;
                    float z = (i < tangents.Length) ? tangents[i].z : 0;
                    float w = (i < tangents.Length) ? tangents[i].w : 1;
                    WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z); WriteF32(bin, w);
                }
            }
            int tanSize = (int)bin.Position - tanOffset;

            // UVs
            int uvOffset = (int)bin.Position;
            bool hasUVs = uvs != null && uvs.Length > 0;
            if (hasUVs)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float u = (i < uvs.Length) ? uvs[i].x : 0;
                    float v = (i < uvs.Length) ? uvs[i].y : 0;
                    WriteF32(bin, u); WriteF32(bin, v);
                }
            }
            int uvSize = (int)bin.Position - uvOffset;

            // --- Embedded texture ---
            int imageOffset = -1;
            int imageSize = 0;
            byte[] pngData = null;
            Texture2D albedoTex = null;

            if (material != null)
            {
                if (material.HasProperty("_BaseMap"))
                    albedoTex = material.GetTexture("_BaseMap") as Texture2D;
                else if (material.HasProperty("_MainTex"))
                    albedoTex = material.GetTexture("_MainTex") as Texture2D;
            }

            if (albedoTex != null)
            {
                PadTo4(bin);
                imageOffset = (int)bin.Position;
                var readable = MakeReadable(albedoTex);
                pngData = readable.EncodeToPNG();
                bin.Write(pngData, 0, pngData.Length);
                imageSize = pngData.Length;
                if (readable != albedoTex)
                    UnityEngine.Object.DestroyImmediate(readable);
                PadTo4(bin);
            }

            byte[] binData = bin.ToArray();

            // --- Build buffer views and accessors ---
            int bvIdx = 0;

            // BV0: indices
            bufferViews.Add(new { buffer = 0, byteOffset = indicesOffset, byteLength = indicesSize });
            int indicesBV = bvIdx++;

            // BV1: positions
            bufferViews.Add(new { buffer = 0, byteOffset = posOffset, byteLength = posSize, byteStride = 12 });
            int posBV = bvIdx++;

            // BV2: normals
            bufferViews.Add(new { buffer = 0, byteOffset = normOffset, byteLength = normSize, byteStride = 12 });
            int normBV = bvIdx++;

            // BV3: tangents (optional)
            int tanBV = -1;
            if (hasTangents)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = tanOffset, byteLength = tanSize, byteStride = 16 });
                tanBV = bvIdx++;
            }

            // BV4: uvs (optional)
            int uvBV = -1;
            if (hasUVs)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = uvOffset, byteLength = uvSize, byteStride = 8 });
                uvBV = bvIdx++;
            }

            // BV5: image (optional)
            int imageBV = -1;
            if (imageOffset >= 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = imageOffset, byteLength = imageSize });
                imageBV = bvIdx++;
            }

            int accIdx = 0;

            // Acc0: indices
            int indicesAcc = accIdx++;
            // Acc1: positions
            int posAcc = accIdx++;
            // Acc2: normals
            int normAcc = accIdx++;
            // Acc3: tangents
            int tanAcc = hasTangents ? accIdx++ : -1;
            // Acc4: uvs
            int uvAcc = hasUVs ? accIdx++ : -1;

            // --- Build JSON ---
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"asset\": { \"version\": \"2.0\", \"generator\": \"HZExporter\" },\n");

            // Buffers
            json.Append($"  \"buffers\": [{{ \"byteLength\": {binData.Length} }}],\n");

            // Buffer views
            json.Append("  \"bufferViews\": [\n");
            for (int i = 0; i < bufferViews.Count; i++)
            {
                var bv = bufferViews[i];
                var t = bv.GetType();
                int bo = (int)t.GetProperty("byteOffset").GetValue(bv);
                int bl = (int)t.GetProperty("byteLength").GetValue(bv);
                var bsProp = t.GetProperty("byteStride");
                json.Append($"    {{ \"buffer\": 0, \"byteOffset\": {bo}, \"byteLength\": {bl}");
                if (bsProp != null) json.Append($", \"byteStride\": {bsProp.GetValue(bv)}");
                json.Append(" }");
                if (i < bufferViews.Count - 1) json.Append(",");
                json.Append("\n");
            }
            json.Append("  ],\n");

            // Accessors
            json.Append("  \"accessors\": [\n");
            // indices
            json.Append($"    {{ \"bufferView\": {indicesBV}, \"componentType\": {(useU16 ? UNSIGNED_SHORT : UNSIGNED_INT)}, \"count\": {indexCount}, \"type\": \"SCALAR\" }},\n");
            // positions
            json.Append($"    {{ \"bufferView\": {posBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC3\", ");
            json.Append($"\"min\": [{F(posMin[0])}, {F(posMin[1])}, {F(posMin[2])}], \"max\": [{F(posMax[0])}, {F(posMax[1])}, {F(posMax[2])}] }},\n");
            // normals
            json.Append($"    {{ \"bufferView\": {normBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC3\" }}");
            if (hasTangents || hasUVs) json.Append(",");
            json.Append("\n");
            // tangents
            if (hasTangents)
            {
                json.Append($"    {{ \"bufferView\": {tanBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC4\" }}");
                if (hasUVs) json.Append(",");
                json.Append("\n");
            }
            // uvs
            if (hasUVs)
            {
                json.Append($"    {{ \"bufferView\": {uvBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC2\" }}\n");
            }
            json.Append("  ],\n");

            // Images + textures (optional)
            if (imageBV >= 0)
            {
                json.Append($"  \"images\": [{{ \"bufferView\": {imageBV}, \"mimeType\": \"image/png\" }}],\n");
                json.Append("  \"textures\": [{ \"source\": 0 }],\n");
            }

            // Materials
            json.Append("  \"materials\": [{\n");
            json.Append("    \"pbrMetallicRoughness\": {\n");
            Color baseColor = Color.white;
            if (material != null)
            {
                if (material.HasProperty("_BaseColor")) baseColor = material.GetColor("_BaseColor");
                else if (material.HasProperty("_Color")) baseColor = material.GetColor("_Color");
            }
            json.Append($"      \"baseColorFactor\": [{F(baseColor.r)}, {F(baseColor.g)}, {F(baseColor.b)}, {F(baseColor.a)}]");
            if (imageBV >= 0)
            {
                json.Append(",\n      \"baseColorTexture\": { \"index\": 0 }");
            }
            json.Append("\n    }\n  }],\n");

            // Meshes
            json.Append("  \"meshes\": [{\n");
            json.Append($"    \"name\": \"{EscapeJson(mesh.name ?? "mesh")}\",\n");
            json.Append("    \"primitives\": [{\n");
            json.Append($"      \"indices\": {indicesAcc},\n");
            json.Append($"      \"attributes\": {{ \"POSITION\": {posAcc}, \"NORMAL\": {normAcc}");
            if (tanAcc >= 0) json.Append($", \"TANGENT\": {tanAcc}");
            if (uvAcc >= 0) json.Append($", \"TEXCOORD_0\": {uvAcc}");
            json.Append(" },\n");
            json.Append("      \"material\": 0\n");
            json.Append("    }]\n  }],\n");

            // Nodes
            json.Append("  \"nodes\": [{ \"mesh\": 0 }],\n");

            // Scene
            json.Append("  \"scene\": 0,\n");
            json.Append("  \"scenes\": [{ \"nodes\": [0] }]\n");
            json.Append("}");

            // --- Write GLB ---
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
            // Pad JSON to 4 bytes with spaces
            int jsonPadded = (jsonBytes.Length + 3) & ~3;
            byte[] jsonChunk = new byte[jsonPadded];
            Array.Copy(jsonBytes, jsonChunk, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) jsonChunk[i] = 0x20;

            // Pad BIN to 4 bytes with zeros
            int binPadded = (binData.Length + 3) & ~3;
            byte[] binChunk = new byte[binPadded];
            Array.Copy(binData, binChunk, binData.Length);

            uint totalLength = 12 + 8 + (uint)jsonChunk.Length + 8 + (uint)binChunk.Length;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                // Header
                w.Write(GLB_MAGIC);
                w.Write(GLB_VERSION);
                w.Write(totalLength);
                // JSON chunk
                w.Write((uint)jsonChunk.Length);
                w.Write(CHUNK_JSON);
                w.Write(jsonChunk);
                // BIN chunk
                w.Write((uint)binChunk.Length);
                w.Write(CHUNK_BIN);
                w.Write(binChunk);
            }

            Debug.Log($"[HZGlbWriter] Exported → {outputPath} ({totalLength} bytes, {vertexCount} verts, {indexCount} indices)");
        }

        /// <summary>Export multi-mesh skinned model as .glb with skeleton</summary>
        public static void ExportSkinnedMesh(SkinnedMeshRenderer[] smrs, Material[] materials,
            SkinnedMeshRenderer primarySmr, string outputPath)
        {
            var bones = primarySmr.bones;
            int jointCount = bones.Length;
            var bindPoses = primarySmr.sharedMesh.bindposes;

            // Build bone hierarchy
            var boneToIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < jointCount; i++)
                boneToIndex[bones[i]] = i;

            int[] parentIndices = new int[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                var parent = bones[i].parent;
                parentIndices[i] = (parent != null && boneToIndex.TryGetValue(parent, out int pi)) ? pi : -1;
            }

            var bin = new MemoryStream();
            var json = new StringBuilder();

            // Track buffer views and accessors as we go
            var bvList = new List<string>();  // JSON fragments for bufferViews
            var accList = new List<string>(); // JSON fragments for accessors

            int AddBV(int offset, int length, int stride = 0)
            {
                int idx = bvList.Count;
                string s = $"    {{ \"buffer\": 0, \"byteOffset\": {offset}, \"byteLength\": {length}";
                if (stride > 0) s += $", \"byteStride\": {stride}";
                s += " }";
                bvList.Add(s);
                return idx;
            }

            int AddAcc(int bv, int compType, int count, string type, float[] min = null, float[] max = null)
            {
                int idx = accList.Count;
                string s = $"    {{ \"bufferView\": {bv}, \"componentType\": {compType}, \"count\": {count}, \"type\": \"{type}\"";
                if (min != null && max != null)
                    s += $", \"min\": [{F(min[0])}, {F(min[1])}, {F(min[2])}], \"max\": [{F(max[0])}, {F(max[1])}, {F(max[2])}]";
                s += " }";
                accList.Add(s);
                return idx;
            }

            // --- Write per-mesh primitive data ---
            int meshCount = smrs.Length;
            int[][] meshAccIndices = new int[meshCount][]; // [indicesAcc, posAcc, normAcc, tanAcc, uvAcc, jointsAcc, weightsAcc]

            for (int mi = 0; mi < meshCount; mi++)
            {
                var smr = smrs[mi];
                var mesh = smr.sharedMesh;
                int vertexCount = mesh.vertexCount;
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uvs = mesh.uv;
                var triangles = mesh.triangles;
                int indexCount = triangles.Length;
                bool useU16 = vertexCount <= 65535;

                // Build bone remap for this SMR
                int[] boneRemap = new int[smr.bones.Length];
                for (int bi = 0; bi < smr.bones.Length; bi++)
                    boneRemap[bi] = (smr.bones[bi] != null && boneToIndex.TryGetValue(smr.bones[bi], out int pi)) ? pi : 0;

                // Indices
                int idxOff = (int)bin.Position;
                for (int i = 0; i < indexCount; i += 3)
                {
                    if (useU16) { WriteU16(bin, (ushort)triangles[i]); WriteU16(bin, (ushort)triangles[i+2]); WriteU16(bin, (ushort)triangles[i+1]); }
                    else { WriteU32(bin, (uint)triangles[i]); WriteU32(bin, (uint)triangles[i+2]); WriteU32(bin, (uint)triangles[i+1]); }
                }
                int idxSize = (int)bin.Position - idxOff;
                PadTo4(bin);

                // Positions
                int posOff = (int)bin.Position;
                float[] posMin = { float.MaxValue, float.MaxValue, float.MaxValue };
                float[] posMax = { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < vertexCount; i++)
                {
                    float x = -vertices[i].x, y = vertices[i].y, z = vertices[i].z;
                    WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z);
                    posMin[0] = Math.Min(posMin[0], x); posMin[1] = Math.Min(posMin[1], y); posMin[2] = Math.Min(posMin[2], z);
                    posMax[0] = Math.Max(posMax[0], x); posMax[1] = Math.Max(posMax[1], y); posMax[2] = Math.Max(posMax[2], z);
                }
                int posSize = (int)bin.Position - posOff;

                // Normals
                int normOff = (int)bin.Position;
                for (int i = 0; i < vertexCount; i++)
                {
                    float nx = (i < normals.Length) ? -normals[i].x : 0;
                    float ny = (i < normals.Length) ? normals[i].y : 0;
                    float nz = (i < normals.Length) ? normals[i].z : 1;
                    WriteF32(bin, nx); WriteF32(bin, ny); WriteF32(bin, nz);
                }
                int normSize = (int)bin.Position - normOff;

                // Tangents
                int tanOff = (int)bin.Position;
                bool hasTan = tangents != null && tangents.Length > 0;
                if (hasTan)
                    for (int i = 0; i < vertexCount; i++)
                    {
                        WriteF32(bin, (i < tangents.Length) ? -tangents[i].x : 1);
                        WriteF32(bin, (i < tangents.Length) ? tangents[i].y : 0);
                        WriteF32(bin, (i < tangents.Length) ? tangents[i].z : 0);
                        WriteF32(bin, (i < tangents.Length) ? tangents[i].w : 1);
                    }
                int tanSize = (int)bin.Position - tanOff;

                // UVs
                int uvOff = (int)bin.Position;
                bool hasUV = uvs != null && uvs.Length > 0;
                if (hasUV)
                    for (int i = 0; i < vertexCount; i++)
                    {
                        WriteF32(bin, (i < uvs.Length) ? uvs[i].x : 0);
                        WriteF32(bin, (i < uvs.Length) ? uvs[i].y : 0);
                    }
                int uvSize = (int)bin.Position - uvOff;

                // Joint indices (u8 x 4, remapped)
                int jiOff = (int)bin.Position;
                var boneWeights = mesh.boneWeights;
                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    bin.WriteByte((byte)boneRemap[bw.boneIndex0]);
                    bin.WriteByte((byte)boneRemap[bw.boneIndex1]);
                    bin.WriteByte((byte)boneRemap[bw.boneIndex2]);
                    bin.WriteByte((byte)boneRemap[bw.boneIndex3]);
                }
                int jiSize = (int)bin.Position - jiOff;

                // Joint weights (f32 x 4)
                int jwOff = (int)bin.Position;
                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    WriteF32(bin, bw.weight0); WriteF32(bin, bw.weight1);
                    WriteF32(bin, bw.weight2); WriteF32(bin, bw.weight3);
                }
                int jwSize = (int)bin.Position - jwOff;

                // Register buffer views and accessors
                int idxBV = AddBV(idxOff, idxSize);
                int posBV = AddBV(posOff, posSize, 12);
                int normBV = AddBV(normOff, normSize, 12);
                int tanBV = hasTan ? AddBV(tanOff, tanSize, 16) : -1;
                int uvBV = hasUV ? AddBV(uvOff, uvSize, 8) : -1;
                int jiBV = AddBV(jiOff, jiSize, 4);
                int jwBV = AddBV(jwOff, jwSize, 16);

                int idxAcc = AddAcc(idxBV, useU16 ? UNSIGNED_SHORT : UNSIGNED_INT, indexCount, "SCALAR");
                int posAcc = AddAcc(posBV, FLOAT, vertexCount, "VEC3", posMin, posMax);
                int normAcc = AddAcc(normBV, FLOAT, vertexCount, "VEC3");
                int tanAcc = hasTan ? AddAcc(tanBV, FLOAT, vertexCount, "VEC4") : -1;
                int uvAcc = hasUV ? AddAcc(uvBV, FLOAT, vertexCount, "VEC2") : -1;
                int jiAcc = AddAcc(jiBV, UNSIGNED_BYTE, vertexCount, "VEC4");
                int jwAcc = AddAcc(jwBV, FLOAT, vertexCount, "VEC4");

                meshAccIndices[mi] = new int[] { idxAcc, posAcc, normAcc, tanAcc, uvAcc, jiAcc, jwAcc };
            }

            // --- Inverse bind matrices ---
            PadTo4(bin);
            int ibmOff = (int)bin.Position;
            for (int i = 0; i < jointCount; i++)
            {
                // Convert bind pose: negate X row and X column
                var m = bindPoses[i];
                for (int col = 0; col < 4; col++)
                    for (int row = 0; row < 4; row++)
                    {
                        float v = m[row, col];
                        bool negRow = (row == 0), negCol = (col == 0);
                        if (negRow != negCol) v = -v;
                        WriteF32(bin, v);
                    }
            }
            int ibmSize = (int)bin.Position - ibmOff;
            int ibmBV = AddBV(ibmOff, ibmSize);
            int ibmAcc = AddAcc(ibmBV, FLOAT, jointCount, "MAT4");

            // --- Embedded images ---
            int[] imageBVs = new int[materials.Length];
            for (int i = 0; i < materials.Length; i++) imageBVs[i] = -1;

            for (int mi = 0; mi < materials.Length; mi++)
            {
                Texture2D albedo = null;
                if (materials[mi] != null)
                {
                    if (materials[mi].HasProperty("_BaseMap")) albedo = materials[mi].GetTexture("_BaseMap") as Texture2D;
                    else if (materials[mi].HasProperty("_MainTex")) albedo = materials[mi].GetTexture("_MainTex") as Texture2D;
                }
                if (albedo != null)
                {
                    PadTo4(bin);
                    int imgOff = (int)bin.Position;
                    var readable = MakeReadable(albedo);
                    byte[] png = readable.EncodeToPNG();
                    bin.Write(png, 0, png.Length);
                    if (readable != albedo) UnityEngine.Object.DestroyImmediate(readable);
                    PadTo4(bin);
                    imageBVs[mi] = AddBV(imgOff, png.Length);
                }
            }

            byte[] binData = bin.ToArray();

            // --- Build JSON ---
            json.Append("{\n");
            json.Append("  \"asset\": { \"version\": \"2.0\", \"generator\": \"HZExporter\" },\n");

            // Buffers
            json.Append($"  \"buffers\": [{{ \"byteLength\": {binData.Length} }}],\n");

            // Buffer views
            json.Append("  \"bufferViews\": [\n");
            json.Append(string.Join(",\n", bvList));
            json.Append("\n  ],\n");

            // Accessors
            json.Append("  \"accessors\": [\n");
            json.Append(string.Join(",\n", accList));
            json.Append("\n  ],\n");

            // Images + textures
            var imageIndices = new List<int>(); // material index -> image index
            bool hasAnyImage = false;
            for (int i = 0; i < materials.Length; i++)
                if (imageBVs[i] >= 0) hasAnyImage = true;

            if (hasAnyImage)
            {
                json.Append("  \"images\": [\n");
                int imgIdx = 0;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (imageBVs[i] >= 0)
                    {
                        if (imgIdx > 0) json.Append(",\n");
                        json.Append($"    {{ \"bufferView\": {imageBVs[i]}, \"mimeType\": \"image/png\" }}");
                        imageIndices.Add(imgIdx);
                        imgIdx++;
                    }
                    else
                    {
                        imageIndices.Add(-1);
                    }
                }
                json.Append("\n  ],\n");

                json.Append("  \"textures\": [\n");
                for (int i = 0; i < imgIdx; i++)
                {
                    if (i > 0) json.Append(",\n");
                    json.Append($"    {{ \"source\": {i} }}");
                }
                json.Append("\n  ],\n");
            }
            else
            {
                for (int i = 0; i < materials.Length; i++) imageIndices.Add(-1);
            }

            // Materials
            json.Append("  \"materials\": [\n");
            for (int mi = 0; mi < materials.Length; mi++)
            {
                if (mi > 0) json.Append(",\n");
                Color bc = Color.white;
                if (materials[mi] != null)
                {
                    if (materials[mi].HasProperty("_BaseColor")) bc = materials[mi].GetColor("_BaseColor");
                    else if (materials[mi].HasProperty("_Color")) bc = materials[mi].GetColor("_Color");
                }
                json.Append("    { \"pbrMetallicRoughness\": { ");
                json.Append($"\"baseColorFactor\": [{F(bc.r)}, {F(bc.g)}, {F(bc.b)}, {F(bc.a)}]");
                if (imageIndices[mi] >= 0)
                    json.Append($", \"baseColorTexture\": {{ \"index\": {imageIndices[mi]} }}");
                json.Append(" } }");
            }
            json.Append("\n  ],\n");

            // Meshes
            json.Append("  \"meshes\": [\n");
            for (int mi = 0; mi < meshCount; mi++)
            {
                if (mi > 0) json.Append(",\n");
                var acc = meshAccIndices[mi];
                string meshName = smrs[mi].sharedMesh.name ?? $"mesh_{mi}";
                json.Append($"    {{ \"name\": \"{EscapeJson(meshName)}\", \"primitives\": [{{ ");
                json.Append($"\"indices\": {acc[0]}, \"attributes\": {{ \"POSITION\": {acc[1]}, \"NORMAL\": {acc[2]}");
                if (acc[3] >= 0) json.Append($", \"TANGENT\": {acc[3]}");
                if (acc[4] >= 0) json.Append($", \"TEXCOORD_0\": {acc[4]}");
                json.Append($", \"JOINTS_0\": {acc[5]}, \"WEIGHTS_0\": {acc[6]}");
                int matIdx = Math.Min(mi, materials.Length - 1);
                json.Append($" }}, \"material\": {matIdx} }}] }}");
            }
            json.Append("\n  ],\n");

            // Nodes: skeleton bones + mesh nodes
            // Node 0..jointCount-1 = bones, jointCount..jointCount+meshCount-1 = mesh nodes
            json.Append("  \"nodes\": [\n");
            for (int i = 0; i < jointCount; i++)
            {
                var bone = bones[i];
                // Local transform relative to parent (converted)
                var lp = bone.localPosition;
                var lr = bone.localRotation;
                var ls = bone.localScale;
                float px = -lp.x, py = lp.y, pz = lp.z;
                float qx = lr.x, qy = -lr.y, qz = -lr.z, qw = lr.w;

                json.Append($"    {{ \"name\": \"{EscapeJson(bone.name)}\"");
                json.Append($", \"translation\": [{F(px)}, {F(py)}, {F(pz)}]");
                json.Append($", \"rotation\": [{F(qx)}, {F(qy)}, {F(qz)}, {F(qw)}]");
                json.Append($", \"scale\": [{F(ls.x)}, {F(ls.y)}, {F(ls.z)}]");

                // Children (other bones that have this as parent)
                var children = new List<int>();
                for (int c = 0; c < jointCount; c++)
                    if (parentIndices[c] == i) children.Add(c);
                if (children.Count > 0)
                    json.Append($", \"children\": [{string.Join(", ", children)}]");

                json.Append(" },\n");
            }

            // Mesh nodes
            for (int mi = 0; mi < meshCount; mi++)
            {
                int nodeIdx = jointCount + mi;
                json.Append($"    {{ \"mesh\": {mi}, \"skin\": 0 }}");
                if (mi < meshCount - 1) json.Append(",");
                json.Append("\n");
            }
            json.Append("  ],\n");

            // Skins
            json.Append("  \"skins\": [{\n");
            json.Append($"    \"inverseBindMatrices\": {ibmAcc},\n");
            json.Append("    \"joints\": [");
            for (int i = 0; i < jointCount; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append(i);
            }
            json.Append("]\n  }],\n");

            // Scene
            var sceneNodes = new List<int>();
            // Root bones (no parent)
            for (int i = 0; i < jointCount; i++)
                if (parentIndices[i] == -1) sceneNodes.Add(i);
            // Mesh nodes
            for (int mi = 0; mi < meshCount; mi++)
                sceneNodes.Add(jointCount + mi);

            json.Append("  \"scene\": 0,\n");
            json.Append($"  \"scenes\": [{{ \"nodes\": [{string.Join(", ", sceneNodes)}] }}]\n");
            json.Append("}");

            // --- Write GLB ---
            WriteGlb(json.ToString(), binData, outputPath);

            int totalVerts = 0;
            foreach (var s in smrs) totalVerts += s.sharedMesh.vertexCount;
            Debug.Log($"[HZGlbWriter] Exported skinned → {outputPath} ({meshCount} meshes, {jointCount} joints, {totalVerts} verts)");
        }

        static void WriteGlb(string jsonStr, byte[] binData, string outputPath)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonStr);
            int jsonPadded = (jsonBytes.Length + 3) & ~3;
            byte[] jsonChunk = new byte[jsonPadded];
            Array.Copy(jsonBytes, jsonChunk, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) jsonChunk[i] = 0x20;

            int binPadded = (binData.Length + 3) & ~3;
            byte[] binChunk = new byte[binPadded];
            Array.Copy(binData, binChunk, binData.Length);

            uint totalLength = 12 + 8 + (uint)jsonChunk.Length + 8 + (uint)binChunk.Length;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(GLB_MAGIC);
                w.Write(GLB_VERSION);
                w.Write(totalLength);
                w.Write((uint)jsonChunk.Length);
                w.Write(CHUNK_JSON);
                w.Write(jsonChunk);
                w.Write((uint)binChunk.Length);
                w.Write(CHUNK_BIN);
                w.Write(binChunk);
            }
        }

        static string F(float v) => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);

        static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static void WriteU16(MemoryStream s, ushort v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)(v >> 8)); }
        static void WriteU32(MemoryStream s, uint v) { var b = BitConverter.GetBytes(v); s.Write(b, 0, 4); }
        static void WriteF32(MemoryStream s, float v) { var b = BitConverter.GetBytes(v); s.Write(b, 0, 4); }

        static void PadTo4(MemoryStream s)
        {
            int r = (int)(s.Position % 4);
            if (r != 0) for (int i = 0; i < 4 - r; i++) s.WriteByte(0);
        }

        static Texture2D MakeReadable(Texture2D src)
        {
            if (src.isReadable) return src;
            RenderTexture tmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            Texture2D readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return readable;
        }
    }
}
