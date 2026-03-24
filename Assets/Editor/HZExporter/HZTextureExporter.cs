using System.IO;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity Texture2D as a TextureBlobAsset (.hza) + raw PNG for hz-engine-v5.
    /// </summary>
    public static class HZTextureExporter
    {
        // TextureBlobAsset: header(48) + image BlobAssetRef(24) + format(4) + width(4) + height(4) + is_srgb(4) = 88
        const int SIZEOF_TEXTURE_BLOB_ASSET = 88;
        const int SIZEOF_STRING_BLOB = 8;

        // TextureFormat enum
        const uint TEX_FORMAT_RAW = 0;

        /// <summary>
        /// Export a texture as TextureBlobAsset + PNG image file.
        /// </summary>
        /// <param name="texture">Source texture</param>
        /// <param name="imagePath">Asset-relative path for the image (base, no extension for compressed; full path for raw)</param>
        /// <param name="isSrgb">Whether the texture is sRGB (albedo/emission) vs linear (normal/metallic)</param>
        /// <param name="outputHzaPath">Output .hza metadata file path</param>
        /// <param name="outputImagePath">Output image file path (PNG)</param>
        public static void Export(Texture2D texture, string imagePath, bool isSrgb,
            string outputHzaPath, string outputImagePath)
        {
            // Export the PNG image
            Directory.CreateDirectory(Path.GetDirectoryName(outputImagePath));

            // Make texture readable
            var readable = MakeReadable(texture);
            byte[] pngData = readable.EncodeToPNG();
            File.WriteAllBytes(outputImagePath, pngData);
            if (readable != texture)
                Object.DestroyImmediate(readable);

            // Write TextureBlobAsset
            var w = new HZBlobWriter(1024);
            int blobBase = w.Position;

            w.WriteHeader("TextureBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no header deps

            int imageRefPos = w.ReserveBlobAssetRef(); // image

            w.WriteU32(TEX_FORMAT_RAW); // format
            w.WriteU32((uint)texture.width); // width
            w.WriteU32((uint)texture.height); // height
            w.WriteU32(isSrgb ? 1u : 0u); // is_srgb

            Debug.Assert(w.Position == SIZEOF_TEXTURE_BLOB_ASSET,
                $"TextureBlobAsset size mismatch: expected {SIZEOF_TEXTURE_BLOB_ASSET}, got {w.Position}");

            // String data: image path
            // sizeof(u8) = 1, TYPE_HASH("u8") for the raw image blob
            w.WriteAndPatchBlobAssetRef(imageRefPos, imagePath, 1, "u8");

            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputHzaPath));
            w.WriteToFile(outputHzaPath);
            Debug.Log($"[HZTextureExporter] Exported → {outputHzaPath} ({finalSize} bytes, {texture.width}x{texture.height}, srgb={isSrgb})");
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
