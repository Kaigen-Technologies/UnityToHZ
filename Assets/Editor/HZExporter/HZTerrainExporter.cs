using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity <see cref="Terrain"/> as a TerrainBlobAsset (.hzt) alongside its heightmap
    /// (R16 raw), splatmaps (RGBA PNG), and per-layer albedo/normal/mask PNGs. Mirrors the engine
    /// layout expected by <c>hz-engine-v5-ios</c>'s TerrainBlobAsset loader.
    ///
    /// Output goes to a single folder per terrain. Caller controls the path so the scene exporter
    /// can dedup (shared TerrainData → exported once).
    /// </summary>
    public static class HZTerrainExporter
    {
        const int MAX_LAYERS = 32;     // matches TERRAIN_MAX_LAYERS in engine terrain.h
        const int MAX_SPLATMAPS = 8;   // matches TERRAIN_MAX_SPLATMAPS in engine terrain.h
        const int DEFAULT_TILE_SIZE = 32;
        const int DEFAULT_MAX_LOD = 6;

        /// <summary>
        /// Export a single <see cref="Terrain"/>.
        /// </summary>
        /// <param name="terrain">Source Unity terrain. Must have a non-null TerrainData.</param>
        /// <param name="outDir">Absolute or project-relative directory; created if missing.</param>
        /// <param name="assetPrefix">
        /// Asset-relative prefix the engine will see (e.g. "unity/terrain/MyTerrain"). Used inside
        /// the .hzt files to reference sibling assets.
        /// </param>
        /// <returns>Asset-relative path to terrain.hzt, or null on failure.</returns>
        public static string Export(Terrain terrain, string outDir, string assetPrefix)
        {
            if (terrain == null) { Debug.LogError("[HZTerrain] null terrain"); return null; }
            var td = terrain.terrainData;
            if (td == null)
            {
                Debug.LogError($"[HZTerrain] '{terrain.name}': no TerrainData; skipping");
                return null;
            }

            Directory.CreateDirectory(outDir);

            int layerCount = (td.terrainLayers != null) ? Math.Min(td.terrainLayers.Length, MAX_LAYERS) : 0;
            if (td.terrainLayers != null && td.terrainLayers.Length > MAX_LAYERS)
                Debug.LogWarning($"[HZTerrain] '{td.name}' has {td.terrainLayers.Length} layers; engine caps at {MAX_LAYERS}, truncating.");

            int neededSplats = (layerCount + 3) / 4;
            int splatmapCount = Math.Min(neededSplats, MAX_SPLATMAPS);
            if (neededSplats > MAX_SPLATMAPS)
                Debug.LogWarning($"[HZTerrain] '{td.name}' needs {neededSplats} splatmaps; engine caps at {MAX_SPLATMAPS}, truncating.");

            // Authoring knobs — defaults for now; add an HZTerrainAuthoring MonoBehaviour later
            // if scenes need per-terrain overrides.
            int tileSize = DEFAULT_TILE_SIZE;
            int maxLod = DEFAULT_MAX_LOD;

            int hmRes = td.heightmapResolution;
            int amRes = td.alphamapResolution;

            Debug.Log($"[HZTerrain] '{td.name}': hm={hmRes}x{hmRes} am={amRes}x{amRes} layers={layerCount} splatmaps={splatmapCount} tile={tileSize} maxLod={maxLod} size={td.size}");

            WriteHeightmap(td, hmRes, outDir, assetPrefix);

            for (int s = 0; s < splatmapCount; s++)
            {
                int first = s * 4;
                int last = Math.Min(first + 4, layerCount);
                WriteSplatmap(td, amRes, s, first, last, layerCount, outDir, assetPrefix);
            }

            for (int i = 0; i < layerCount; i++)
                WriteLayer(td.terrainLayers[i], i, td.name, outDir, assetPrefix);

            string terrainHztPath = Path.Combine(outDir, "terrain.hzt");
            WriteManifest(td, terrainHztPath, assetPrefix, layerCount, splatmapCount, tileSize, maxLod);

            string assetRef = $"{assetPrefix}/terrain.hzt";
            Debug.Log($"[HZTerrain]   → {terrainHztPath}  (engine ref: {assetRef})");
            return assetRef;
        }

        // ---- heightmap ----

        static void WriteHeightmap(TerrainData td, int res, string outDir, string assetPrefix)
        {
            var heights = td.GetHeights(0, 0, res, res);
            var bytes = new byte[res * res * 2];
            // Engine convention: X is mirrored scene-wide (negate X), Z is not.
            // The terrain geometry's world_origin already follows the X-mirror, so
            // the heightmap must be X-flipped to match — and NOT Z-flipped (Z passes
            // through unchanged). Unity GetHeights is [z, x], written here as raw
            // R16 (no implicit flip), so: file[y][x] = heights[y][res-1-x].
            for (int y = 0; y < res; y++)
            {
                int row = y * res * 2;
                for (int x = 0; x < res; x++)
                {
                    ushort v = (ushort)Mathf.Clamp(heights[y, res - 1 - x] * 65535f, 0f, 65535f);
                    bytes[row + x * 2]     = (byte)(v & 0xFF);
                    bytes[row + x * 2 + 1] = (byte)(v >> 8);
                }
            }
            File.WriteAllBytes(Path.Combine(outDir, "heightmap.r16"), bytes);

            WriteTextureSidecar(
                path:      Path.Combine(outDir, "heightmap.hzt"),
                imagePath: $"/{assetPrefix}/heightmap",
                format:    "TEX_FORMAT_RAW_R16",
                width:     res,
                height:    res,
                isSrgb:    false);
        }

        // ---- splatmaps ----

        static void WriteSplatmap(TerrainData td, int res, int splatIdx, int firstLayer, int lastLayer,
            int retainedLayers, string outDir, string assetPrefix)
        {
            var alpha = td.GetAlphamaps(0, 0, res, res);
            int alphaLayers = td.alphamapLayers;

            var pixels = new Color[res * res];
            // Must match the heightmap's flip so heights and splat weights line up
            // under the same sample uv: X-flipped, not Z-flipped. Unity's
            // Texture2D->EncodeToPNG path applies an implicit Y-flip, so keeping
            // srcY here (res-1-y) cancels it → net no Z-flip; srcX (res-1-x)
            // applies the X-flip. See WriteHeightmap.
            for (int y = 0; y < res; y++)
            {
                int srcY = res - 1 - y;   // cancels Unity's PNG y-flip → net no Z-flip
                for (int x = 0; x < res; x++)
                {
                    int srcX = res - 1 - x;   // X-flip to match engine X-mirror
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    if (firstLayer + 0 < lastLayer && firstLayer + 0 < alphaLayers) r = alpha[srcY, srcX, firstLayer + 0];
                    if (firstLayer + 1 < lastLayer && firstLayer + 1 < alphaLayers) g = alpha[srcY, srcX, firstLayer + 1];
                    if (firstLayer + 2 < lastLayer && firstLayer + 2 < alphaLayers) b = alpha[srcY, srcX, firstLayer + 2];
                    if (firstLayer + 3 < lastLayer && firstLayer + 3 < alphaLayers) a = alpha[srcY, srcX, firstLayer + 3];

                    // Renormalize against RETAINED layers (what's actually exported). Prevents coverage
                    // gaps when layer count is capped below the terrain's real count.
                    float total = 0f;
                    for (int li = 0; li < retainedLayers && li < alphaLayers; li++)
                        total += alpha[srcY, srcX, li];
                    if (total > 1e-3f)
                    {
                        float inv = 1f / total;
                        r *= inv; g *= inv; b *= inv; a *= inv;
                    }
                    else if (splatIdx == 0)
                    {
                        r = 1f;   // fully layer-0 as a sane fallback on zero-coverage pixels
                    }
                    pixels[y * res + x] = new Color(r, g, b, a);
                }
            }

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
                tex.SetPixels(pixels);
                tex.Apply(false, false);
                File.WriteAllBytes(Path.Combine(outDir, $"splatmap{splatIdx}.png"), tex.EncodeToPNG());
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }

            WriteTextureSidecar(
                path:      Path.Combine(outDir, $"splatmap{splatIdx}.hzt"),
                imagePath: $"/{assetPrefix}/splatmap{splatIdx}",
                format:    "TEX_FORMAT_RAW",
                width:     res,
                height:    res,
                isSrgb:    false,
                hasAlpha:  true);   // splatmap's .a channel carries layer (4n+3) weights; cooker would drop alpha without this (BC1 → BC3).
        }

        // ---- per-layer textures ----

        static void WriteLayer(TerrainLayer layer, int idx, string terrainName, string outDir, string assetPrefix)
        {
            if (layer == null)
            {
                Debug.LogWarning($"[HZTerrain]   layer{idx}: null TerrainLayer, skipping");
                return;
            }
            if (layer.tileSize.x != layer.tileSize.y)
                Debug.LogWarning($"[HZTerrain]   layer{idx} '{layer.name}': non-uniform tileSize ({layer.tileSize}); engine uses scalar, taking .x");

            WriteLayerTex(layer.diffuseTexture,   idx, "albedo", "TEX_FORMAT_BC1", isSrgb: true,  isNormal: false, outDir, assetPrefix);
            WriteLayerTex(layer.normalMapTexture, idx, "normal", "TEX_FORMAT_BC5", isSrgb: false, isNormal: true,  outDir, assetPrefix);
            if (layer.maskMapTexture != null)
            {
                var mm = layer.maskMapTexture;
                // A 1x1 mask map carries no spatial data — it's effectively "no mask".
                // Log it (with the terrain name) so the right terrain can be confirmed.
                if (mm.width <= 1 && mm.height <= 1)
                    Debug.Log($"[HZTerrain] '{terrainName}' layer{idx} '{layer.name}': mask map is a flat {mm.width}x{mm.height} texture ('{mm.name}') — basically no mask");
                WriteLayerTex(mm, idx, "mask", "TEX_FORMAT_BC3", isSrgb: false, isNormal: false, outDir, assetPrefix);
            }
            else
                WriteDefaultMask(idx, outDir, assetPrefix);
        }

        // Unity TerrainLayer mask is (R=metallic, G=AO, B=height, A=smoothness). When a layer has
        // no mask map, Unity's terrain shader treats those as sensible defaults — critically B=0
        // (no height). The engine's terrain shader uses mask.b for height-based blend; falling
        // through to the shader's default-white sampler gives height=1, making unmasked layers
        // dominate everywhere. Emit a 1×1 default mask to keep the math honest.
        static void WriteDefaultMask(int layerIdx, string outDir, string assetPrefix)
        {
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                // R=0 metal, G=1 AO, B=0 height, A=0.5 smoothness
                tex.SetPixel(0, 0, new Color(0f, 1f, 0f, 0.5f));
                tex.Apply(false, false);
                File.WriteAllBytes(Path.Combine(outDir, $"layer{layerIdx}_mask.png"), tex.EncodeToPNG());
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
            WriteTextureSidecar(
                path:      Path.Combine(outDir, $"layer{layerIdx}_mask.hzt"),
                imagePath: $"/{assetPrefix}/layer{layerIdx}_mask",
                format:    "TEX_FORMAT_BC3",
                width:     1, height: 1, isSrgb: false);
            Debug.Log($"[HZTerrain]   layer{layerIdx}_mask: 1x1 synthesized default (R=0 G=1 B=0 A=0.5)");
        }

        static void WriteLayerTex(Texture2D tex, int layerIdx, string kind, string format,
            bool isSrgb, bool isNormal, string outDir, string assetPrefix)
        {
            if (tex == null) return;
            Texture2D readable = null;
            try
            {
                // MakeReadable handles DXT5nm normal unpacking + non-readable source textures
                // via RenderTexture blit. Reuses the GLB path's helper.
                readable = HZGlbWriter.MakeReadable(tex, isNormal);
                byte[] png = readable.EncodeToPNG();
                string pngPath = Path.Combine(outDir, $"layer{layerIdx}_{kind}.png");
                File.WriteAllBytes(pngPath, png);
                WriteTextureSidecar(
                    path:      Path.Combine(outDir, $"layer{layerIdx}_{kind}.hzt"),
                    imagePath: $"/{assetPrefix}/layer{layerIdx}_{kind}",
                    format:    format,
                    width:     readable.width,
                    height:    readable.height,
                    isSrgb:    isSrgb);
                Debug.Log($"[HZTerrain]   layer{layerIdx}_{kind}: {readable.width}x{readable.height} ({tex.name})");
            }
            finally
            {
                if (readable != null && readable != tex) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        // ---- sidecars ----

        static void WriteTextureSidecar(string path, string imagePath, string format,
            int width, int height, bool isSrgb, bool hasAlpha = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("type TextureBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");
            sb.AppendLine($"image {imagePath} u8");
            sb.AppendLine($"format {format}");
            sb.AppendLine($"width {width}");
            sb.AppendLine($"height {height}");
            sb.AppendLine($"is_srgb {(isSrgb ? "true" : "false")}");
            if (hasAlpha) sb.AppendLine("has_alpha true");
            File.WriteAllText(path, sb.ToString());
        }

        static void WriteManifest(TerrainData td, string path, string assetPrefix,
            int layerCount, int splatmapCount, int tileSize, int maxLod)
        {
            var size = td.size;
            var sb = new StringBuilder();
            sb.AppendLine("type TerrainBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");
            sb.AppendLine($"heightmap {assetPrefix}/heightmap.hzt TextureBlobAsset");
            sb.AppendLine($"world_width {F(size.x)}");
            sb.AppendLine($"world_depth {F(size.z)}");
            sb.AppendLine($"height_scale {F(size.y)}");
            sb.AppendLine($"tile_size {tileSize}");
            sb.AppendLine($"max_lod {maxLod}");

            sb.AppendLine($"splatmaps {splatmapCount}");
            for (int s = 0; s < splatmapCount; s++)
            {
                sb.AppendLine($"  texture {assetPrefix}/splatmap{s}.hzt TextureBlobAsset");
                sb.AppendLine();
            }

            sb.AppendLine($"layers {layerCount}");
            for (int i = 0; i < layerCount; i++)
            {
                var layer = td.terrainLayers[i];
                sb.AppendLine($"  albedo {assetPrefix}/layer{i}_albedo.hzt TextureBlobAsset");
                if (layer != null && layer.normalMapTexture != null)
                    sb.AppendLine($"  normal {assetPrefix}/layer{i}_normal.hzt TextureBlobAsset");
                // mask is always emitted (either the Unity mask or a synthesized default)
                sb.AppendLine($"  mask {assetPrefix}/layer{i}_mask.hzt TextureBlobAsset");
                sb.AppendLine($"  uv_scale {F(layer != null ? layer.tileSize.x : 1f)}");
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        static string F(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
