# UnityToHZ — Architecture Reference

Codebase brain-dump for future sessions. Pairs with `CLAUDE.md` (workflow & CLI) and the auto-memory at `C:\Users\Gabriel\.claude\projects\D--dev-unity-UnityToHZ\memory\`.

Read `MEMORY.md` first for current state; this file is structural.

---

## What this project does

Unity Editor tool that exports Unity scenes into the `hz-engine-v5` binary asset format. End goal: take a Unity ShaderGraph + scene authored in Unity, and produce binary `.hza` assets the C engine renders from a screenshot test.

Two repos, both checked out side-by-side:

- `D:\dev\unity\UnityToHZ\` — the Unity project. C# exporter under `Assets/Editor/HZExporter/`. Outputs to `ExportedAssets/`.
- `D:\dev\gabrieldechichi\hz-engine-v5\` — the C engine. Consumes `ExportedAssets/` contents copied into `assets/unity/`.

There's also a fork `hz-engine-v5-unity` that exists for a different test; **do not use it** for verification. Use the primary `hz-engine-v5` repo.

---

## Canonical end-to-end workflow

The 6-step verify-export-against-engine sequence (in user's memory; never skip the deletes):

```
1. unity_cmd.ps1 "exec:HZExportCommands.ExportScene"
2. rm -rf hz-engine-v5/assets/unity        # FULL nuke, not selective
3. cp ExportedAssets/scenes/<name>.hzt     -> assets/unity/<name>.hzt
   cp -r ExportedAssets/scenes/<name>      -> assets/unity/<name>/
   cp -r ExportedAssets/glb                -> assets/unity/glb
   cp -r ExportedAssets/shaders            -> assets/unity/shaders
   cp -r ExportedAssets/environments       -> assets/unity/environments  (if present)
   cp -r ExportedAssets/animations         -> assets/unity/animations    (if present)
4. rm -rf hz-engine-v5/cooked/unity        # FULL nuke
5. ./hzbuild cook
6. ./hzbuild windows --screenshot
```

The cook is content-hash + on-disk-equality based. Stale `cooked/unity/` from prior exports causes "asset collision" errors that abort cooking. Stale `assets/unity/` leaves orphan files the cooker treats as collisions. Both must be fully nuked to converge.

The engine demo that loads the Unity scene is `src/demos/demo_unity_scene.c`. `SCENE_FILE` macro at top selects which scene to load. `demos.c` is the demo registry — only one `#include "demos/...c"` is uncommented at a time.

### Build/cook gotchas

- `hzbuild windows` reports "No modules needed compilation" even after C source edits if its dependency tracker disagrees. **When the rendering looks broken after engine code changes, run `hzbuild clean && hzbuild windows` before assuming your code is wrong.** This wasted real time.
- Cook + screenshot from the **same** terminal session; the engine binary built by `hzbuild windows` reads `cooked/` from CWD.

---

## Unity-side exporter (C#)

All scripts live under `Assets/Editor/HZExporter/`. Files map to concerns:

| File | What it does |
|---|---|
| `HZExportCommands.cs` | Static no-arg methods exposed to the TCP `exec:` channel. `ExportScene`, `ExportXBot`, `ExportWaterShader`, etc. Add a new wrapper here for any new CLI-callable export. |
| `RemoteRefresh.cs` | TCP listener (port 7321). `[InitializeOnLoad]`, survives domain reloads. Skipped in `Application.isBatchMode` so import-worker processes don't double-bind. |
| `HZBlobWriter.cs` | Binary writer for engine blob format. Self-relative `BlobPtr` (i32 offset from field's own address). `StringBlob = {i32 offset; u32 len}`. `BlobAssetHeader` magic `0x54455353414800`, version 3. |
| `HZShaderMap.cs` | Declarative Unity-shader → engine-shader + property mapping, loaded from `shader_mapping.json`. Routes materials to either a glTF PBR path or a custom-shader path (`extras.hz_material`). |
| `HZKeywordReader.cs` | Reads a Unity Material's shader-keyword state. Parses the material's `.shadergraph` once (cached) via `SgParser`, walks `SgKeyword[]`, queries `mat.HasProperty(refName) ? mat.GetFloat(refName) : kw.DefaultValue`. |
| `HZGlbWriter.cs` | Emits glTF 2.0 (`.glb`). For custom materials, embeds `materials[i].extras.hz_material` JSON describing the engine `MaterialBlobAsset` shape. |
| `HZMaterialExporter.cs` | PBR-fallback material export (binary `.hza` + text `.hzt`). Bypasses GLB. Used for legacy/simple materials. |
| `HZMeshExporter.cs` | `ModelBlobAsset` binary export — `SubMeshBlobAsset` array, static + skinned. |
| `HZAnimationExporter.cs` | `AnimationBlobAsset` baked at 30 fps. |
| `HZTextureExporter.cs` | `TextureBlobAsset` + PVRTexTool BC1/BC3 KTX compression. Path: `D:\dev\gabrieldechichi\hz-engine-v5\tools\PVRTextTool\PVRTexToolCLI.exe`. |
| `HZSceneExporter.cs` | Per-entity `.hzt` + scene-manifest `.hzt`. Each entity has `EcsModelRef` listing its model path. Scene `.hzt` has no `dependencies`; entities handle their own. |
| `ShaderGraph/` | Sub-package — parse + lower + codegen `.shadergraph` → engine `.hz.glsl`. See below. |

### Coordinate conversions (Unity LH Y-up → engine RH Y-up)

- **Positions/normals/tangents**: negate X. Flip triangle winding.
- **Object quat**: `(qx, -qy, -qz, qw)` — X-mirror only.
- **Camera quat**: X-mirror **plus** 180° Y flip (Unity forward is +Z, engine forward is -Z).
- Local transform `Transform` component was renamed `LocalTransform` (has quat rotation, not euler).

### shader_mapping.json

```jsonc
{
  "default": "<unityShaderName>",   // fallback entry
  "shaders": [
    {
      "unityShader":  "Idyllic Fantasy Nature/Water",
      "engineShader": "shaders/water_material.hza",
      "engineSkinnedShader": "shaders/water_skinned_material.hza", // optional
      "customShader":  true,        // false → glTF PBR path
      "isTransparent": true,        // → MaterialBlobAsset.is_transparent
      "blend": {                    // optional, overrides cooker default
        "enabled": true,
        "src_factor": "ONE",        // GPU_BLEND_<X>; no prefix in JSON
        "dst_factor": "ZERO",
        "op": "ADD",
        "src_factor_alpha": "ONE",
        "dst_factor_alpha": "ZERO",
        "op_alpha": "ADD"
      },
      "properties": [
        { "unity": "_DeepColor",   "slot": "deep_color",  "kind": "color4" },
        { "unity": "_AlbedoMap",   "slot": "albedo",      "kind": "texture" },
        { "unity": "_BumpMap",     "slot": "normal",      "kind": "texture_normal" },
        ...
      ]
    }
  ],
  "customFunctions": [
    { "name": "MainLight", "engineCall": "hz_main_light", "structType": "MainLightData",
      "structFields": ["direction", "color"] }
  ]
}
```

- `kind` values: `float`, `vec2`, `vec3`, `color4`, `texture`, `texture_normal`, `float_oneMinus`, `texture_orm_metalsmooth`, `texture_orm_occlusion`.
- The `blend` block is *optional*. When omitted, the engine cooker defaults `isTransparent=true → SRC_ALPHA/ONE_MINUS_SRC_ALPHA`, otherwise blend disabled. Set explicitly only for manual-composite materials (`ONE/ZERO`) or additive (`ONE/ONE`).
- `texture_normal` triggers Unity's DXT5nm unpack + linear-space blit. Without it, sampling returns gamma garbage.

### Shader graph pipeline (`ShaderGraph/` subdir)

Three stages, kept separate so each is independently testable:

1. **`SgParser`** — reads `.shadergraph` (multi-object JSON), builds a typed `SgGraph` model (`SgGraph.cs`). The only file that touches raw JSON.
2. **`SgLowering`** — `SgNode` (parsed Unity nodes) → `SgValue` (typed GLSL expressions). Per-node-type `NodeHandler`. SubGraphNode resolution: matches outer slots to inner subgraph properties by `m_ShaderOutputName == m_DefaultReferenceName`. Booleans like "found a slot" must be tracked separately from sentinel slot-num values — Unity's `m_Id` values can be **negative** (e.g. `-586588005`), so `-1` is not a safe sentinel.
3. **`SgCodegen`** — assembles the final `@vs`/`@fs`/`@material` GLSL with engine-specific defines. Two templates (LIT, UNLIT) with a `${FS_FEATURE_DEFINES}` slot.

`SgKeyword` (in `SgGraph.cs`):
```csharp
class SgKeyword {
    public string Id;
    public string Name;             // m_Name (display)
    public string ReferenceName;    // m_DefaultReferenceName, "_BLEND_NORMALS"
    public bool   IsBoolean;        // m_KeywordType: 0=Bool, 1=Enum
    public int    DefaultValue;     // m_Value (bool: 0/1; enum: entry index)
    public List<SgKeywordEntry> Entries;  // enum only — ReferenceName per entry
}
```

#### `${FS_FEATURE_DEFINES}` mechanism

Some shaders need engine-side helpers like `hz_scene_color()` / `hz_scene_depth_linear()`. Those helpers are gated by `#define HZ_RENDERER_SCENE_COLOR` / `HZ_RENDERER_SCENE_DEPTH` in `renderer_shaders.glsl`. The codegen **scans the emitted body + functions + helpers** for substring references to those helper names and emits the matching `#define` only when used. Don't scan only `bodyStr` — subgraph helpers (DepthFade etc.) live in the functions/helpers stringbuilders.

#### `ScreenPositionNode` lowering (this caused real bugs)

Unity's `ScreenPositionNode` has three modes, and their semantics are asymmetric:

- **Default (0)** → emit `vec4(screen_uv.x, screen_uv.y, 0, 0)`. Already post-perspective-divide. Downstream `SceneColor`/`SceneDepth` sample `.xy` directly. This is exactly what Unity's HLSL emits.
- **Raw (1)** / **Center (2)** → emit homogeneous form `(uv_or_ndc * w_inv, z * w_inv, w_inv)` so `.w = w_clip = view_distance` is usable (DepthFade subgraph uses `ScreenPos.w` as view distance for the depth-gap subtract).

**Critical detail: divide `gl_FragCoord.xy` by `hz_scene.cluster.dpr` first.** `gl_FragCoord` is in framebuffer (DPR-scaled) pixels, but `cluster.screen_width/height` are logical pixels. Without the dpr divide, UV ranges `[0, 2]` and the sampler wraps the texture, producing quincunx-pattern ghost copies in `hz_scene_color` samples. Pattern from `common.glsl:578-579`:

```glsl
vec2 logical_pos = screen_pos / cp.dpr;
vec2 screen_uv = logical_pos / vec2(float(cp.screen_width), float(cp.screen_height));
```

The same dpr divide applies to the unwired `ScreenPositionMaterialSlot` default literal (used when SceneDepth's UV input is unwired in subgraphs like DepthFade).

#### `SceneColor` / `SceneDepth` lowering

Pattern emits `hz_scene_color({0}.xy)` and `hz_scene_depth_linear({0}.xy)`. The `.xy` works because ScreenPosition Default mode already produces normalized screen UV. The `hz_scene_depth_linear` wrapper (in `renderer_shaders.glsl`) does the reverse-Z linearization:

```glsl
float hz_scene_depth_linear(vec2 uv) {
    float d = hz_scene_depth_sample(uv);
    float n = hz_scene.cluster.near_plane;
    float f = hz_scene.cluster.far_plane;
    // Inverse of camera_perspective (RH Y-up, reverse-Z, depth [0,1])
    float denom = max(d * (f - n) - n, 1e-6);
    return (n * f) / denom;
}
```

The clamp at `1e-6` handles sky/cleared pixels (`d=0` → denom negative → infinite/negative without clamp).

---

## Engine cooker (`src/exporter/exporter.c`)

`exporter.exe` is a separate build target from `game.exe`. It uses the same engine types via reflection; both built by `hzbuild`.

### Phase ordering (the one I had to fix)

The cooker is *not* a single pass. Phases run in a specific sequence because each consumes outputs of the prior:

| Phase | What runs | Why this order |
|---|---|---|
| 0 | Shader cook — default variants | GLB cook resolves `MaterialBlobAssetRef` to `shaders/standard_static_material.hza` etc. on disk. Without these existing first, PBR-fallback material cook fails. |
| 1 | GLB cook | Writes per-glb `.hzt` files including embedded materials with `extras.hz_material` (custom shaders) and PBR fallback materials. |
| 1b | Shader cook — **keyword variants** | Re-runs after GLB cook because keyword variants are discovered by scanning material `.hzt` files. The freshly-cooked custom materials' keyword blocks define which variants need compiling. |
| 2 | PNG/WAV/font `.hzt` generation | Standalone source → `.hzt`. |
| 2b | HDR environment cook | Cubemap + mipchain. |
| 2c | BRDF LUT bake | Once per build if missing. |
| 3a | Texture `.hzt` → `.hza` | Materials reference textures by path; needs to be on disk first. |
| 3b | Material `.hzt` → `.hza` | References shaders + textures. |
| 3c | Everything else (entities, scenes) | Top of dependency stack. |

The two-pass shader cook is non-obvious but necessary. The first pass exists for GLB-cook prerequisites; the second pass exists because custom-shader materials don't exist on disk until GLB cook runs. Don't collapse to one pass without solving both constraints.

### Shader variant discovery

`shader_cook.c:270-307`:
1. Walks `assets/` recursively for `.hzt` files.
2. Filters to `MaterialBlobAsset` (via `hzt_peek_type_name`).
3. Reads each material's `keywords[]` blob.
4. Computes `kw_hash = material_keywords_hash(material_keywords_to_defines(kws))`.
5. Resolves the material's `material_shader` path to a shader stem.
6. Pushes the variant into that shader's `ShaderVariantList`.

Source-path resolution (`shader_cook_resolve_src_path`) searches three roots:

```
shaders/renderer/<stem>.hz.glsl   (canonical engine shaders)
shaders/unity/<stem>.hz.glsl      (Unity-exported shaders)
shaders/<stem>.glsl               (legacy fallback)
```

Add a new root here if you introduce another shader subtree. Missing this is how I first hit "cook: keyword variant: source not found for vegetation."

### Reflection codegen

The `.gen.h` files under `src/generated/` are produced by `hzbuild codegen` from C struct definitions. Anything with `HZ_STRUCT_TYPEDEF(Foo) { ... }` gets a `Foo_typeinfo` emitted; the hzt reader/writer + JSON parser are reflection-driven and pick up new fields **automatically** after codegen runs.

`hzbuild windows` runs codegen as a sub-step. `hzbuild codegen` runs it standalone.

After adding a field to a struct that's serialized via `hzt_write_file` or `json_parse`, you do **not** need to write any reader/writer code — just run codegen. The same applies to `GltfCustomMaterial` keyword/blend additions: append a field, codegen, done.

---

## Material model (engine side)

`MaterialBlobAsset` (engine) is built from `GltfCustomMaterial` (cooker intermediate) which is parsed from `extras.hz_material` JSON in the GLB.

### Fields (after the refactor)

```c
HZ_STRUCT_TYPEDEF(MaterialBlobAsset) {
  BlobAssetHeader header;
  BlobAssetRef(MaterialShaderBlobAsset) material_shader;
  b32 depth_test;
  b32 depth_write;
  GpuCompareFunc depth_compare;
  GpuCullMode cull_mode;
  b32 is_transparent;
  b32 alpha_cutoff;
  GpuBlendState blend;
  BlobArray(MaterialPropertyBlob) properties;
  BlobArray(MaterialKeywordBlob) keywords;
};
```

### `is_transparent` vs `blend` — orthogonal concerns

This was conflated in the original codebase and is a common confusion:

- **`is_transparent`** routes the draw into the transparent batch (after the scene_color/depth copy, depth read-only). It's a **batch-routing** decision, not a blending decision.
- **`blend`** (`GpuBlendState`) is the **color combine** — `src/dst factors`, `op`. Three common modes:
  - `SRC_ALPHA / ONE_MINUS_SRC_ALPHA` — standard alpha blend (translucent).
  - `ONE / ZERO` — manual compositing (glass that reads scene_color and emits final color via `frag_color.a = 1.0`).
  - `ONE / ONE` — additive (particles, emissive overlays).

The renderer (`renderer.c:2316`) routes on `desc->is_transparent`, **not** `blend.enabled`. Code-side materials (demos, tests) must set `desc->is_transparent = true` explicitly when they want transparent routing, even if they also set `pipeline.blend.enabled = true`. They are not equivalent.

The cooker (`build_custom_material_blob` in `asset_importer.c`) applies a back-compat default: if `is_transparent && !blend.enabled` after copying source fields, it promotes blend to standard alpha-blend. This keeps already-authored content working when the exporter doesn't (yet) emit an explicit blend block.

Same model + same back-compat default in `particle_system.c` for `ParticleMaterialBlobAsset`.

### Keyword model

`MaterialKeywordBlob`:
```c
HZ_STRUCT_TYPEDEF(MaterialKeywordBlob) {
  StringBlob name;
  StringBlob value;   // empty for bool keywords
};
```

`material_keywords_to_defines` reduces each pair to one `#define`:
- Bool: `name` only — e.g. `_BLEND_NORMALS`.
- Enum: `name_value` — e.g. `_COLOR_OVERLAY_UV`.

`material_keywords_hash` is the fnv1a of the sorted defines. It's the lookup key for `shader_resolve_variant`.

`.hzt` text serialization (reflection-emitted from `BlobArray(MaterialKeywordBlob)`):

```
keywords 4
  name _MAIN_LIGHT_SHADOWS_CASCADE
  value 
  name _COLOR_OVERLAY
  value UV
```

Empty `value` = boolean keyword. Convention: list a boolean keyword **only when ON**. Enum keywords are always listed (one entry is always selected).

### Exporter → keyword pipeline

For a Unity `Material`:
1. `HZKeywordReader.ReadEnabled(mat)` — gets the material's `.shadergraph` path via `AssetDatabase.GetAssetPath(mat.shader)`, parses it via `SgParser` (cached), walks `SgKeyword[]`.
2. For each keyword: if `mat.HasProperty(refName)` use `mat.GetFloat(refName)` else use `kw.DefaultValue`. Booleans only emitted when ≥0.5. Enums emit `entries[idx].ReferenceName`.
3. `HZShaderMap.EmbedCustomMaterial` writes this into `CustomMaterial.keywords`.
4. `HZGlbWriter.WriteCustomMaterialJson` emits `"keywords": [{ "name", "value" }, ...]` into `extras.hz_material`.
5. Cooker (`asset_importer.c:build_custom_material_blob`) copies from `GltfCustomMaterial.keywords` into `MaterialBlobAsset.keywords` (StringBlob name + value).
6. Reflection `hzt_write_file` serializes the keyword block to text `.hzt`.
7. `shader_cook.c` reads all material `.hzt` files post-GLB-cook and registers variants per unique `kw_hash`.

Single source of truth: the `.shadergraph` file. Unity Material per-material state. No `shader_mapping.json` keyword catalog needed.

---

## Engine renderer plumbing (the parts that affect export)

### Transparent pass

Implementation in `renderer.c:1859-1886`. Between opaque and transparent passes:
1. `gpu_copy_texture(scene_color_copy, hdr_target)` — only when `RendererInitDesc.scene_color_copy_enabled = true`.
2. Transparent pass binds depth `read_only = true`.
3. Shaders sample via `hz_scene_color_sample(uv)` and `hz_scene_depth_sample(uv)`.

Init: `demo_unity_scene.c` sets `.scene_color_copy_enabled = true` on `RendererInitDesc`. New demos that use refractive materials must do the same.

### Pipeline cache & batch routing

`renderer.c` keeps a pipeline cache keyed on `(name_hash, variant_hash)`. Entry's `is_transparent` flag (now from `desc->is_transparent`) determines batch routing. Five call sites set this explicitly:
- `build_renderer_material_from_blob` (from `material_blob->is_transparent`).
- `demo_renderer_scene_color.c`, `demo_renderer_transparents.c`.
- `test_renderer_transparents.c`.
- `particle_system.c` (from `blob->is_transparent`).

### Reverse-Z perspective (this affected the depth math)

`camera.c:9-33`:
```c
out.m[2][3] = -1.0f;  // RH: -1 for perspective divide
if (depth_zero_to_one) {  // D3D12/Vulkan/Metal: reverse-Z
    out.m[2][2] = z_near / (z_near - z_far);          // negative
    out.m[3][2] = (z_far * z_near) / (z_far - z_near);
}
```

Near plane → `z_ndc ≈ 1`, far plane → `z_ndc ≈ 0`. The depth-linearization derivation lives in `renderer_shaders.glsl:443-446`.

### MSAA

Demo specifies `sample_count = 4`, but `renderer.c:535` forces it to 1 to keep scene_color/depth copies as plain texture-to-texture (no MSAA resolve). Will be replaced by TAA. Don't be surprised by aliased edges in screenshots — that's expected current behavior.

---

## Common gotchas — things that wasted real time

1. **Stale binaries:** `hzbuild windows` reports "No modules needed compilation" after edits sometimes. When debugging weird behavior, do `hzbuild clean && hzbuild windows` before doubting code logic.
2. **DPR scaling:** any shader using `gl_FragCoord.xy / vec2(screen_width, screen_height)` is wrong unless it also divides by `cluster.dpr`. See ScreenPosition section above.
3. **`is_transparent` ≠ `blend.enabled`:** see Material model section.
4. **Camera convention:** engine forward is `-Z`, Unity forward is `+Z`. Camera quat needs X-mirror + 180° Y flip. Object quat needs X-mirror only.
5. **Reflection codegen drift:** after adding a struct field, run `hzbuild codegen` (or `hzbuild windows` which runs it as a step). Without re-gen, `hzt_write_file`/`json_parse` won't see the new field. The .gen.h is checked in; don't hand-edit it.
6. **Two cook passes for shader assets** — see Phase ordering. Don't try to "optimize" to one pass without understanding why both exist.
7. **Negative `m_Id` values in shadergraph JSON:** never use `-1` as a "not found" sentinel for slot IDs; track found-ness with a separate bool.
8. **`extras.hz_material` is an *intermediate*, not a wire format you maintain manually.** It's emitted by `HZGlbWriter.WriteCustomMaterialJson`, parsed by `asset_importer.c:extract_hz_material_json` + `json_parse(GltfCustomMaterial, …)`, then copied into `MaterialBlobAsset`. Both ends evolve together via the reflection codegen.
9. **The `hz-engine-v5-unity` fork is a trap.** It has its own `assets/` and `cooked/` and a different SCENE_FILE. Verify all engine work against the primary `hz-engine-v5` repo.

---

## TODOs at time of writing

From `CLAUDE.md` and observed during sessions:

- Skeleton bone skipping is only first-level; characters nested deeper export empty skeleton entities.
- GLB deduplication: multiple GameObjects sharing the same source mesh each export separate GLBs.
- PVRTexTool on macOS: KTX compression skipped if `PVRTexToolCLI` not in `PVRTEXTOOL_PATH`.
- Skinned mesh renders in T-pose without animation integration in the unity-scene demo.
- Foam/tint visual parity with Unity reference image still has tuning differences (gradient noise threshold, color values). The *structural* rendering — transparent pass, refraction sample, depth-fade — matches.

---

## Where to look first when something breaks

| Symptom | Look at |
|---|---|
| "shader variant not cooked" assert | `shader_cook.c:resolve_src_path` includes your shader subtree? Did phase 1b run? `cooked/shaders/<stem>_<hash>_d3d12.hza` exists? |
| Water/glass renders solid color, no refraction | `RendererInitDesc.scene_color_copy_enabled`. `desc.is_transparent`. `material_blob->blend` populated. |
| Multi-tree quincunx ghost-copy in scene_color sample | DPR divide in screen UV. `ScreenPositionNode` lowering. |
| Cooker crashes / empty output | `hzbuild clean`, re-cook. Check `cook complete: X failed` count — non-zero means real failures. |
| GLB cook fails with "failed to load material shader" | Phase 0 shader cook didn't run, or default variant for that shader isn't cooked yet. |
| Tree etc. invisible but scene loaded | Camera convention check. `EcsModelRef` path right? Per-entity LocalTransform vs WorldTransform world position. |
| Foam/scene_color shows multiple offset copies | DPR scaling bug in screen UV computation, or refraction normal magnitude exploding. |
