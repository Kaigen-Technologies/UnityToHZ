# Terrain Grass Export — Implementation Spec

Instructions for extending `HZTerrainExporter.cs` to export Unity terrain **detail grass** (mesh grass) into the engine's `TerrainBlobAsset` grass format.

The engine side is **already implemented and shipping**. This document describes the target format, the Unity source data, the exact mapping, and the steps to add to the exporter. The engine consumes whatever this exporter writes — your job is only to populate the new manifest fields correctly.

---

## 1. What already exists

- `HZTerrainExporter.Export()` writes `terrain.hzt` (a `TerrainBlobAsset` manifest) plus heightmap, splatmaps, and per-layer textures. **It does not touch detail/grass at all today.**
- The engine `TerrainBlobAsset` now has two new fields the exporter must populate: `grass_tile_size` and a `grass_types[]` array. If you write nothing, grass is simply absent (back-compat preserved).
- The engine resolves all grass asset dependencies (models, textures) automatically from the manifest refs — see §6. You do **not** write a dependencies block.

---

## 2. Engine target format

These are the engine structs (`hz-engine-v5/src/engine/rendering/terrain.h`). You don't edit them; you emit matching `.hzt` text.

```c
TerrainGrassLodBlob {
  BlobAssetRef(ModelBlobAsset) model;   // ignored when generate_billboard
  b32 generate_billboard;
  f32 billboard_width;   // 0 = engine derives from near-LOD bounds
  f32 billboard_height;
  f32 min_view_distance;
  f32 max_view_distance;
  u32 instances_per_tile_max;
  u32 instances_per_tile_min;
  f32 density_falloff_exp;
}

TerrainGrassTypeBlob {            // one per exported detail prototype
  BlobAssetRef(TextureBlobAsset) albedo;
  BlobAssetRef(TextureBlobAsset) density_map;  // up to 4 types share one RGBA map
  u32 density_channel;                          // 0..3 = R/G/B/A
  f32 scale_xz_min, scale_xz_max;
  f32 scale_y_min, scale_y_max;
  f32 alpha_cutoff;
  f32 wind_response;
  vec3 root_color;
  vec3 tip_color;
  BlobArray(TerrainGrassLodBlob) lods;         // near->far, lods[0] full detail
}

TerrainBlobAsset {
  ...existing...
  f32 grass_tile_size;                          // 0 = engine default
  BlobArray(TerrainGrassTypeBlob) grass_types;
}
```

### 2.1 `.hzt` text format

The manifest is reflection-driven text. Rules (mirror `WriteManifest` / `WriteSplatmap` already in `HZTerrainExporter.cs`):

- Indent unit = **2 spaces** per nesting level.
- Array: `<fieldname> <count>` on one line, then each element's fields indented one level deeper.
- Array-of-struct elements have **no separator** — the reader delimits by field declaration order. **Emit fields in struct declaration order.** Omitted fields default to zero (e.g. omit `model` on a billboard LOD).
- `BlobAssetRef`: `<fieldname> <asset-relative-path> <TypeName>` (e.g. `model unity/glb/Reeds.glb ModelBlobAsset`).
- `vec3`: space-separated — `root_color 0.18 0.32 0.10`.
- `b32`: `true` / `false`.
- New fields go **after** the existing `layers` block (declaration order: `...layers, grass_tile_size, grass_types`).

### 2.2 Canonical example (this exact text cooks cleanly)

```
grass_tile_size 7.0
grass_types 1
  albedo unity/glb/textures/Grass_01.hzt TextureBlobAsset
  density_map terrain/<name>/grass_density0.hzt TextureBlobAsset
  density_channel 0
  scale_xz_min 0.7
  scale_xz_max 1.3
  scale_y_min 0.8
  scale_y_max 1.4
  alpha_cutoff 0.5
  wind_response 1.0
  root_color 0.18 0.32 0.10
  tip_color 0.78 0.85 0.40
  lods 2
    model unity/glb/Reeds.glb ModelBlobAsset
    generate_billboard false
    min_view_distance 0.0
    max_view_distance 100.0
    instances_per_tile_max 256
    instances_per_tile_min 80
    density_falloff_exp 2.0
    generate_billboard true
    billboard_width 0.8
    billboard_height 1.5
    min_view_distance 100.0
    max_view_distance 800.0
    instances_per_tile_max 80
    instances_per_tile_min 12
    density_falloff_exp 2.0
```

Note the second LOD omits `model` (billboard) — the engine synthesizes a 2-quad cross billboard from `billboard_width/height` + the type's `albedo`.

---

## 3. Unity source data (verified against Unity docs)

Detail grass lives in Unity's **detail** system (separate from splatmaps/trees):

- `TerrainData.detailPrototypes[]` — one `DetailPrototype` per detail type. A terrain can have many.
- `DetailPrototype` fields you need:
  - `prototype` (GameObject) + `usePrototypeMesh` — the mesh, for `renderMode == VertexLit`.
  - `prototypeTexture` (Texture2D) — for `GrassBillboard`/`Grass` render modes.
  - `renderMode` ∈ `{ GrassBillboard, VertexLit, Grass }`.
  - `minWidth/maxWidth` (XZ scale range), `minHeight/maxHeight` (Y scale range).
  - `healthyColor`, `dryColor`.
- `TerrainData.GetDetailLayer(xBase, yBase, width, height, layer) → int[,]` — density map for **one** detail layer; `layer` indexes `detailPrototypes`. **One density map per type.**
- `TerrainData.detailWidth` / `detailHeight` — density map resolution.
- `TerrainData.detailScatterMode` ∈ `{ InstanceCountMode, CoverageMode }` — changes what the integers mean.
- `TerrainData.maxDetailScatterPerRes` — the max sample value; the **normalization denominator**.

---

## 4. Field mapping (Unity → engine)

| Engine field | Unity source |
|---|---|
| `grass_types[i]` | `detailPrototypes[i]` — export mesh prototypes (`usePrototypeMesh` / `VertexLit`). Billboard-only prototypes: optional, see §7. |
| `lods[0].model` | `prototype` mesh → export as `ModelBlobAsset` (reuse `HZMeshExporter`), ref by path. Dedup shared prototypes. |
| `albedo` | mesh prototype: its material's albedo texture. Billboard prototype: `prototypeTexture`. Export as `TextureBlobAsset`, ref by path. |
| `density_map` + `density_channel` | `GetDetailLayer(i)` normalized to `[0,1]`, packed 4 layers per RGBA (see §5). |
| `scale_xz_min/max` | `minWidth` / `maxWidth` |
| `scale_y_min/max` | `minHeight` / `maxHeight` |
| `root_color` / `tip_color` | `dryColor` / `healthyColor` (engine renders a root→tip vertical gradient; this mapping is a reasonable approximation) |
| `alpha_cutoff`, `wind_response` | **engine-authored** (defaults or authoring component — §7) |
| LOD bands, billboard, `instances_per_tile_*`, `density_falloff_exp`, `grass_tile_size` | **engine-authored** — Unity has no LOD concept for details (§7) |

---

## 5. Density map: normalization + packing (the part with real gotchas)

### 5.1 Normalize

`GetDetailLayer` returns integer counts. Convert to `[0,1]` coverage:

```
coverage = clamp(value / maxDetailScatterPerRes, 0, 1)
```

This holds for both `InstanceCountMode` and `CoverageMode` — `maxDetailScatterPerRes` is the per-sample max in either mode. The engine treats the result as a probabilistic coverage mask; absolute density is set by `instances_per_tile_max`.

### 5.2 Pack 4 types per RGBA

Detail layers are stored separately in Unity, but pack 4 into one RGBA texture (exactly like `WriteSplatmap` already packs 4 alphamap layers):

- Types 0–3 → `grass_density0` channels R/G/B/A → each type's `density_channel` = 0/1/2/3.
- Types 4–7 → `grass_density1`, etc. `ceil(N/4)` textures total.
- Write a `TextureBlobAsset` sidecar per packed map (reuse `WriteTextureSidecar`, `has_alpha: true`, `format: TEX_FORMAT_RAW` so the cooker keeps alpha).

### 5.3 Coordinate flip — MUST match the heightmap/splatmap

The exporter X-flips the heightmap (`file[y][x] = heights[y][res-1-x]`) and applies the same flip (minus Unity's PNG y-flip) to splatmaps so weights line up with terrain world space. **The density map must use the identical flip**, or grass will be mirrored relative to the terrain. `GetDetailLayer` returns `[z, x]` like `GetHeights`/`GetAlphamaps` — copy the `WriteSplatmap` loop's `srcX = res-1-x`, `srcY = res-1-y` convention exactly.

---

## 6. Dependencies — you do NOT write them

The engine's `asset_terrain_deps_fn` walks `grass_types[]` and auto-emits load dependencies for each type's `albedo`, `density_map`, and each non-billboard LOD's `model`, then resolves their handles. So:

- Just write the refs in the manifest (path + TypeName).
- Do **not** emit a `dependencies` block (the existing terrain.hzt has none — same as splatmaps/layers today).
- Make sure the referenced model + textures are actually exported and cooked, or load fails.

---

## 7. Engine-authored fields — defaults + authoring component

Unity's detail data has **no** LOD chain, view distances, or wind/cutoff. Provide sensible defaults; add a per-terrain `HZTerrainGrassAuthoring` MonoBehaviour for overrides if scenes need control.

Suggested defaults:

| Field | Default |
|---|---|
| `grass_tile_size` | 7.0 |
| `alpha_cutoff` | 0.5 |
| `wind_response` | 1.0 |
| LOD0 band | `[0, 100]`, `instances_per_tile_max` ~256, `min` ~80, `falloff` 2.0 |
| LOD1 (billboard) | `generate_billboard true`, `[100, 800]`, `max` ~80, `min` ~12, `falloff` 2.0 |
| `billboard_width/height` | derive from mesh bounds, or `0` (engine derives) |

Recommended LOD policy: emit LOD0 = the Unity mesh (near band) + LOD1 = `generate_billboard true` (far band). The engine builds the billboard; Unity gives you no far-LOD geometry.

`scale_y_min/max`: **store the real Unity values** (`minHeight/maxHeight`) even though the engine currently collapses to uniform scale (uses `scale_xz`). Storing them is free and means no re-export when the engine adds two-axis scale.

---

## 8. Implementation steps in `HZTerrainExporter.cs`

1. **Enumerate** `td.detailPrototypes`. Filter to mesh prototypes (`usePrototypeMesh` / `renderMode == VertexLit`). Skip or special-case billboard-only prototypes (§7).
2. **Export each prototype mesh** via `HZMeshExporter` → `ModelBlobAsset`, dedup by source mesh (multiple prototypes can share a mesh). Record the asset-relative path.
3. **Export each prototype's albedo** texture (mesh material albedo, or `prototypeTexture`) → `TextureBlobAsset`. Record path.
4. **Build density maps**: for each group of ≤4 layers, `GetDetailLayer` each, normalize (§5.1), pack into one RGBA (§5.2) with the splatmap flip (§5.3), write PNG + `TextureBlobAsset` sidecar. Track `(texture, channel)` per type.
5. **Emit the grass block** into `WriteManifest`: `grass_tile_size`, then `grass_types <N>`, then per type the fields in declaration order (§2.2), including the `lods <M>` sub-array. Pull scale/colors from the prototype; LOD/material/billboard from defaults or the authoring component.
6. **Verify**: run the 6-step export→cook→screenshot loop (see `ARCHITECTURE.md`). `hzbuild cook` will fail to parse if field order/indent is wrong; the engine demo `demo_gpu_grass_mesh_terrain.c` already consumes this exact format and is the reference for a correct result.

---

## 9. Reference

- Engine format + consumer: `hz-engine-v5/src/engine/rendering/terrain.h`, `grass_mesh.h`, `grass_system.c`, demo `src/demos/demo_gpu_grass_mesh_terrain.c`.
- A hand-authored, cooking example lives in `hz-engine-v5/assets/terrain/terrain.hzt` (the `grass_tile_size` / `grass_types` block) — copy its shape.
- Existing exporter patterns to reuse: `WriteSplatmap` (packing + flip), `WriteTextureSidecar`, `WriteManifest`, `HZMeshExporter`, `HZGlbWriter.MakeReadable`.
