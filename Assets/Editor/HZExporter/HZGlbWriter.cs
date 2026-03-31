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
