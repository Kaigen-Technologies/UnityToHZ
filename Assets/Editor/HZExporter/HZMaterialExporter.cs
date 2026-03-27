using System.IO;
using System.Text;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity Material as a MaterialBlobAsset (.hza) for hz-engine-v5.
    /// </summary>
    public static class HZMaterialExporter
    {
        // MaterialBlobAsset: header(48) + material_shader BlobAssetRef(24)
        //   + depth_test(4) + depth_write(4) + depth_compare(4) + cull_mode(4)
        //   + is_transparent(4) + alpha_cutoff(4) + properties BlobPtr(16) = 112
        const int SIZEOF_MATERIAL_BLOB_ASSET = 112;
        // MaterialPropertyBlob: name StringBlob(8) + type(4) + union(24) = 36
        const int SIZEOF_MATERIAL_PROPERTY_BLOB = 36;
        const int SIZEOF_STRING_BLOB = 8;
        const int SIZEOF_BLOB_ASSET_REF = 24;

        // MaterialPropertyType enum values
        const uint MAT_PROP_VEC4 = 3;
        const uint MAT_PROP_TEXTURE = 5;
        const uint MAT_PROP_COLOR4 = 7;

        // GpuCompareFunc
        const uint GPU_COMPARE_GREATER = 0;
        // GpuCullMode
        const uint GPU_CULL_BACK = 0;

        // sizeof(TextureBlobAsset) for BlobAssetRef typehash
        const int SIZEOF_TEXTURE_BLOB_ASSET = 88;
        // sizeof(MaterialShaderBlobAsset) = 112 (verified from engine)
        const int SIZEOF_MATERIAL_SHADER_BLOB_ASSET = 112;

        /// <summary>
        /// Export a Unity Material as MaterialBlobAsset.
        /// </summary>
        /// <param name="mat">Unity material</param>
        /// <param name="skinned">Whether this is for a skinned mesh</param>
        /// <param name="texturePath">Asset-relative path to exported texture .hza, or null if no texture</param>
        /// <param name="outputPath">Output .hza file path</param>
        public static void Export(Material mat, bool skinned, string texturePath, string outputPath)
        {
            // Get color from Unity material
            Color baseColor = Color.white;
            if (mat != null)
            {
                if (mat.HasProperty("_BaseColor"))
                    baseColor = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color"))
                    baseColor = mat.GetColor("_Color");
            }
            // Unity colors are already linear if in linear color space

            string shaderPath = skinned
                ? "shaders/standard_skinned_material.hza"
                : "shaders/standard_static_material.hza";

            // Count properties: always "color", optionally "albedo"
            int propCount = 1;
            bool hasAlbedo = !string.IsNullOrEmpty(texturePath);
            if (hasAlbedo) propCount++;

            var w = new HZBlobWriter(4096);
            int blobBase = w.Position;

            // === MaterialBlobAsset root (112 bytes) ===
            w.WriteHeader("MaterialBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no header deps

            int shaderRefPos = w.ReserveBlobAssetRef(); // material_shader

            w.WriteU32(1); // depth_test
            w.WriteU32(1); // depth_write
            w.WriteU32(GPU_COMPARE_GREATER); // depth_compare
            w.WriteU32(GPU_CULL_BACK); // cull_mode
            w.WriteU32(0); // is_transparent
            w.WriteU32(0); // alpha_cutoff

            int propsPtrPos = w.ReserveBlobPtr(); // properties

            Debug.Assert(w.Position == SIZEOF_MATERIAL_BLOB_ASSET,
                $"MaterialBlobAsset size mismatch: expected {SIZEOF_MATERIAL_BLOB_ASSET}, got {w.Position}");

            // === MaterialPropertyBlob array ===
            int propsArrayPos = w.Position;
            w.PatchBlobPtr(propsPtrPos, propsArrayPos, propCount,
                SIZEOF_MATERIAL_PROPERTY_BLOB, "MaterialPropertyBlob");

            // --- Property 0: "color" (MAT_PROP_COLOR4) ---
            int colorPropBase = w.Position;
            int colorNamePos = w.ReserveStringBlob(); // name
            w.WriteU32(MAT_PROP_COLOR4); // type
            // union: vec4 (16 bytes) + 8 bytes padding to fill 24-byte union
            w.WriteF32(baseColor.r);
            w.WriteF32(baseColor.g);
            w.WriteF32(baseColor.b);
            w.WriteF32(baseColor.a);
            w.WriteZeros(8); // padding to 24

            Debug.Assert(w.Position == colorPropBase + SIZEOF_MATERIAL_PROPERTY_BLOB,
                $"color property size mismatch");

            // --- Property 1: "albedo" (MAT_PROP_TEXTURE) --- (optional)
            int albedoNamePos = -1;
            int albedoTexRefPos = -1;
            if (hasAlbedo)
            {
                int albedoPropBase = w.Position;
                albedoNamePos = w.ReserveStringBlob(); // name
                w.WriteU32(MAT_PROP_TEXTURE); // type
                // union: BlobAssetRef(TextureBlobAsset) = 24 bytes (fills union exactly)
                albedoTexRefPos = w.ReserveBlobAssetRef();

                Debug.Assert(w.Position == albedoPropBase + SIZEOF_MATERIAL_PROPERTY_BLOB,
                    $"albedo property size mismatch");
            }

            // === String data ===
            // material_shader path
            w.WriteAndPatchBlobAssetRef(shaderRefPos, shaderPath,
                SIZEOF_MATERIAL_SHADER_BLOB_ASSET, "MaterialShaderBlobAsset");

            // property names
            w.WriteAndPatchStringBlob(colorNamePos, "color");
            if (hasAlbedo)
            {
                w.WriteAndPatchStringBlob(albedoNamePos, "albedo");
                w.WriteAndPatchBlobAssetRef(albedoTexRefPos, texturePath,
                    SIZEOF_TEXTURE_BLOB_ASSET, "TextureBlobAsset");
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
            Debug.Log($"[HZMaterialExporter] Exported → {outputPath} ({finalSize} bytes, {propCount} props, shader={shaderPath})");
        }

        static string F(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Export material as .hzt text file.</summary>
        public static void ExportHzt(Material mat, bool skinned, string texturePath, string outputPath)
        {
            Color baseColor = Color.white;
            if (mat != null)
            {
                if (mat.HasProperty("_BaseColor"))
                    baseColor = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color"))
                    baseColor = mat.GetColor("_Color");
            }

            string shaderPath = skinned
                ? "shaders/standard_skinned_material.hza"
                : "shaders/standard_static_material.hza";

            bool hasAlbedo = !string.IsNullOrEmpty(texturePath);
            int propCount = hasAlbedo ? 2 : 1;

            var sb = new StringBuilder();
            sb.AppendLine("type MaterialBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");
            sb.AppendLine($"material_shader {shaderPath} MaterialShaderBlobAsset");
            sb.AppendLine("depth_test true");
            sb.AppendLine("depth_write true");
            sb.AppendLine("depth_compare GPU_COMPARE_GREATER_EQUAL");
            sb.AppendLine("cull_mode GPU_CULL_BACK");
            sb.AppendLine("is_transparent false");
            sb.AppendLine("alpha_cutoff false");
            sb.AppendLine($"properties {propCount}");

            // color property
            sb.AppendLine($"  name color");
            sb.AppendLine($"  type MAT_PROP_VEC4");
            sb.AppendLine($"  v4 {F(baseColor.r)} {F(baseColor.g)} {F(baseColor.b)} {F(baseColor.a)}");

            if (hasAlbedo)
            {
                sb.AppendLine();
                sb.AppendLine($"  name albedo");
                sb.AppendLine($"  type MAT_PROP_TEXTURE");
                sb.AppendLine($"  texture {texturePath} TextureBlobAsset");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, sb.ToString());
            Debug.Log($"[HZMaterialExporter] Exported → {outputPath} ({propCount} props, shader={shaderPath})");
        }
    }
}
