# Unity-to-HZ Scene Exporter — Current State

## Overview

Unity Editor tool that exports Unity scenes to hz-engine-v5 binary asset format (`.hza`).
Exports a complete scene as a `SceneBlobAsset` manifest + per-entity `EntityBlobAsset` files,
plus separate `.hza` files for meshes, materials, textures, and animations.

**Engine**: `D:\dev\gabrieldechichi\hz-engine-v5\` (and test fork `hz-engine-v5-unity`)
**Unity project**: `D:\dev\unity\UnityToHZ\` (Unity 6, URP)

---

## 1. Binary Format (hz-engine-v5)

All offsets in BlobPtr/StringBlob are **self-relative** (offset from the field's own address, `i32`).

### 1.1 Core Types

**StringBlob** (8 bytes): `{ i32 offset; u32 len; }`
- Self-relative: `(u8*)&offset + offset = string pointer`
- String data is null-terminated, allocated size rounded to 4-byte alignment

**BlobPtr / BlobArray(T)** (16 bytes): `{ i32 offset; u32 size; u32 type_size; u32 typehash; }`
- Self-relative offset
- `typehash = TYPE_HASH(T) = fnv1a(name) ^ sizeof(T)`

**BlobAssetRef(T)** (24 bytes): `{ StringBlob path; u32 type_size; u32 typehash; HzHandle handle; }`
- Typed asset reference with path on disk
- `handle` is zero on disk, filled at runtime by asset loader

**BlobAssetHeader** (48 bytes):
```
u64 magic           = 0x54455353414800
u64 asset_size      = total file size
u64 asset_type_hash = fnv1a(name) (ASSET_TYPE, NOT TYPE_HASH — no xor with sizeof)
u32 version         = 3
u32 _pad0           = 0
BlobArray(StringBlob) dependencies  // paths loaded before this asset is ready
```

### 1.2 FNV-1a Hash

```
u32 fnv1a(str): hash = 0x811c9dc5; for each byte: hash ^= b; hash *= 0x01000193;
```

- `TYPE_HASH(T) = fnv1a(name) ^ sizeof(T)` — used in BlobPtr.typehash
- `ASSET_TYPE(T) = fnv1a(name)` — used in BlobAssetHeader.asset_type_hash (NO xor)
- `stable_type_hash = fnv1a(component_name)` — identifies ECS components (stored as u64)

---

## 2. Coordinate System Conversion

Unity: **left-handed Y-up**. Engine: **right-handed Y-up**.

Conversion rule: **negate X** everywhere spatial.

| Data | Conversion |
|------|-----------|
| Positions | `(x, y, z)` → `(-x, y, z)` |
| Normals/tangents | negate X, keep tangent W |
| Mesh indices | swap idx[1] and idx[2] per triangle (flip winding) |
| AABB | `new_min.x = -old_max.x`, `new_max.x = -old_min.x` |
| Quaternion rotation (objects) | `(qx, qy, qz, qw)` → `(qx, -qy, -qz, qw)` |
| Camera quaternion | X-mirror + 180° Y flip: `mirrored * Quaternion.Euler(0,180,0)` |
| Scale | unchanged |
| Bind pose matrices | negate X row and X column (XOR: negate if exactly one of row/col is 0) |

---

## 3. Asset Types and Struct Sizes

All sizes verified against engine `sizeof()`:

| Struct | Size | Notes |
|--------|------|-------|
| ModelBlobAsset | 160 | header(48) + mat4(64) + meshes(16) + joints(16) + joint_names(16) |
| MeshBlobAsset | 40 | name(8) + submeshes(16) + blendshape_names(16) |
| SubMeshBlobAsset | 192 | material BlobAssetRef(24) + fields + 8 BlobPtrs + blendshapes(16) |
| JointBlobAsset | 84 | mat4(64) + parent_index(4) + children(16) |
| MaterialBlobAsset | 112 | header(48) + material_shader BlobAssetRef(24) + render state + properties(16) |
| MaterialPropertyBlob | 36 | name(8) + type(4) + union(24) |
| MaterialShaderBlobAsset | 112 | header(48) + shader BlobAssetRef(24) + data_size(4) + props(16) + srvs(16) |
| TextureBlobAsset | 88 | header(48) + image BlobAssetRef(24) + format(4) + w(4) + h(4) + srgb(4) |
| AnimationBlobAsset | 72 | header(48) + length(4) + joint_count(4) + keyframes(16) |
| KeyframeBlobAsset | 24 | timestamp(4) + transform_count(4) + joint_transforms(16) |
| JointTransformBlob | 44 | joint_index(4) + translation(12) + rotation(16) + scale(12) |
| SceneBlobAsset | 72 | header(48) + scene_dir(8) + entities(16) |
| EntityBlobAsset | 72 | header(48) + stable_id(8) + components(16) |
| SceneComponentBlob | 32 | stable_type_hash(8) + data_size(4) + data(16) + 4 pad |
| BlobAssetRef | 24 | path StringBlob(8) + type_size(4) + typehash(4) + handle(8) |
| Camera | 396 | 4x mat4(256) + Frustum(96) + quat(16) + pos(12) + fov/near/far/aspect(16) |
| BlendshapeBlobAsset | 32 | position_deltas(16) + normal_deltas(16) |

---

## 4. ECS Components

From `ecs_components.h`:

| Component | Size | Fields | Hash name |
|-----------|------|--------|-----------|
| LocalTransform | 40 | v3 position + quat rotation + v3 scale | "LocalTransform" |
| EcsVisibility | 8 | b32 visible + b32 casts_shadow | "EcsVisibility" |
| EntityName | 68 | String64Bytes { u32 len + char[64] } | "EntityName" |
| SceneEntityTag | 1 | u8 _ | "SceneEntityTag" |
| EcsStableId | 8 | u64 id | "EcsStableId" |
| EcsModelRef | 24 | BlobAssetRef(ModelBlobAsset) model | "EcsModelRef" |
| EcsMaterialRef | 24 | BlobAssetRef(MaterialBlobAsset) material | "EcsMaterialRef" |
| EcsAABB | 24 | aabb { v3 min + v3 max } | "EcsAABB" |
| Camera | 396 | (see struct sizes table) | "Camera" |
| EcsDirectionalLight | 36 | v3 direction + v3 color + f32 intensity + b32 cast_shadow + f32 shadow_distance | "EcsDirectionalLight" |
| EcsPointLight | 20 | v3 color + f32 radius + f32 intensity | "EcsPointLight" |
| EcsSceneSettings | 16 | v3 ambient_color + f32 ambient_intensity | "EcsSceneSettings" |

---

## 5. Scene Format

A scene is one manifest `.hza` + a folder with one `.hza` per entity:

```
scenes/
  SampleScene.hza          ← SceneBlobAsset manifest
  SampleScene/             ← entity folder
    12345.hza              ← EntityBlobAsset (one per entity)
    67890.hza
```

**SceneBlobAsset**: references entity files via `BlobArray(BlobAssetRef(EntityBlobAsset))`.
Scene `header.dependencies` is empty — entity deps handle asset loading.

**EntityBlobAsset**: contains `stable_id` + `BlobArray(SceneComponentBlob)` components.
Entity `header.dependencies` lists model/material asset paths so they're loaded before
the entity is "ready". The asset system loads the full dependency chain:
scene → entities → models/materials → material shaders → shader blobs.

**SceneComponentBlob**: each component is `stable_type_hash(u64) + data_size(u32) + BlobArray(u8) data`.
For components with `BlobAssetRef` fields (EcsModelRef, EcsMaterialRef), the path string
data is written after the component struct bytes, with self-relative offset from within the data blob.

---

## 6. Material Pipeline

**MaterialBlobAsset** references a `MaterialShaderBlobAsset` template (e.g. `shaders/standard_static_material.hza`).

Properties exported:
- `"color"` — MAT_PROP_COLOR4 (7), vec4 from Unity `_BaseColor`
- `"albedo"` — MAT_PROP_TEXTURE (5), BlobAssetRef to TextureBlobAsset (only if texture exists)

**TextureBlobAsset** references a compressed KTX image via `BlobAssetRef(u8)`.
- `image.path` = base path (e.g. `"/unity/albedo"`) — engine appends `_bc1.ktx` etc.
- `format` = TEX_FORMAT_BC1 (opaque) or TEX_FORMAT_BC3 (alpha)
- Compression via PVRTexToolCLI: `PVRTexToolCLI.exe -i input.png -o output_bc1.ktx -m -f BC1,UBN,sRGB`

Shader mapping:
- Static meshes: `shaders/standard_static_material.hza`
- Skinned meshes: `shaders/standard_skinned_material.hza`

---

## 7. Implementation Files

All scripts in `Assets/Editor/HZExporter/`:

| File | Purpose | Status |
|------|---------|--------|
| `HZBlobWriter.cs` | Binary writer with self-relative BlobPtr/StringBlob/BlobAssetRef | Done |
| `HZMeshExporter.cs` | ModelBlobAsset export (static + skinned meshes) | Done |
| `HZMaterialExporter.cs` | MaterialBlobAsset export (color + texture properties) | Done |
| `HZTextureExporter.cs` | TextureBlobAsset + PVRTexTool BC1/BC3 compression | Done |
| `HZAnimationExporter.cs` | AnimationBlobAsset (baked sampling at 30fps) | Done |
| `HZSceneExporter.cs` | SceneBlobAsset manifest + per-entity EntityBlobAsset | Done |
| `HZExportCommands.cs` | CLI-callable export commands via unity_cmd.ps1 | Done |
| `RemoteRefresh.cs` | TCP listener for CLI control (port 7321) | Done |

### CLI Commands

```bash
CMD="powershell -ExecutionPolicy Bypass -File 'D:/dev/unity/UnityToHZ/tools/unity_cmd.ps1'"
$CMD -cmd refresh                              # Trigger C# compilation
$CMD -cmd log                                  # Get compile result
$CMD -cmd "exec:HZExportCommands.ExportScene"  # Export full scene
$CMD -cmd "exec:HZExportCommands.ExportTestCube"
$CMD -cmd "exec:HZExportCommands.ExportTexturedCube"
$CMD -cmd "exec:HZExportCommands.ExportXBot"
$CMD -cmd "exec:HZExportCommands.ExportBodyBlock"
```

### Python Validators

```bash
python tools/dump_model_hza.py <file.hza>      # Validate ModelBlobAsset
python tools/dump_material_hza.py <file.hza>    # Validate MaterialBlobAsset
python tools/dump_animation_hza.py <file.hza>   # Validate AnimationBlobAsset
```

---

## 8. Testing in Engine

Test fork: `D:\dev\gabrieldechichi\hz-engine-v5-unity\`

**Demo file**: `src/demos/demo_unity_assets.c` — loads Unity scene via `demo_scene_system.c` pattern.

**Build & screenshot**: `./hzbuild windows --screenshot`

The demo uses `demo_scene_system.c` to load `unity/SampleScene.hza`. All components are
registered via `ECS_COMPONENT_REGISTER_REFLECT`. The ECS renderer systems (`ecs_renderer_schedule`)
handle model resolution, rendering, and lighting automatically.

Assets are copied from Unity export dir to engine `assets/unity/` dir.

**Engine changes made** (in hz-engine-v5-unity):
- `camera.c`: view matrix built from quaternion directly (preserves roll)
- `asset_loaders.c`: `asset_entity_deps_fn` reads `header.dependencies` from entity blobs
- `scene.c`: `scene_save_entity` writes asset ref paths into entity `header.dependencies`

---

## 9. Known Issues / TODO

- **Shadow casting**: directional light `cast_shadow=true` with `shadow_distance>0` causes
  rendering issues when loaded via scene system. Currently exported with `cast_shadow=false`.
  Needs investigation — may be related to shadow cascade setup timing.
- **Skinned mesh rendering in scene**: skinned meshes render in T-pose (identity bones).
  The `render_skinned_models_system` needs `EcsSkinnedModel` component with bone matrices,
  which requires animation integration.
- **Point lights**: component defined but not exported yet (no point lights in test scene).
- **Scene cleanup**: exporter generates new random entity IDs each export but doesn't delete
  old entity files. Should clean the scene folder before writing.
- **Ambient light**: currently exports `RenderSettings.ambientLight` which may not match URP
  environment lighting exactly.
