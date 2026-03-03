# Unity-to-HZ Scene Exporter — Implementation Plan

## Goal

Build C# Unity Editor scripts that export a Unity scene to hz-engine-v3's binary asset
format (`.hza`). The output is a `SceneBlobAsset` file containing serialized ECS entities
with components, plus separate `.hza` files for meshes, materials, and textures.

Target: a simple scene — 1 plane, 1 cube, 1 directional light, 1 point light, camera,
scene settings. Also supports skeletal animation export (AnimationClip → AnimationBlobAsset).

---

## 1. Engine Binary Format Reference

All references below are relative to `D:\dev\gabrieldechichi\hz-engine-v3\`.

### 1.1 Core Types

**Handle** (`src/lib/handle.h:43-46`):
```c
typedef struct { u32 idx; u32 gen; } Handle;  // 8 bytes
```
Invalid handle: `{ .idx = 0, .gen = 0 }`.
Asset ref patched for serialization: `{ .idx = dependency_index, .gen = 1 }`.

**StringBlob** (`src/engine/assets/blob_asset.h:99-102`):
```c
typedef struct { u32 len; u32 offset; } StringBlob;  // 8 bytes
```
`offset` is relative to the parent struct pointer. String data is null-terminated,
allocated size is `(len + 1)` rounded up to 4-byte alignment.

**BlobPtr / BlobArray** (`src/engine/assets/blob_asset.h:47-54`):
```c
typedef struct {
  u32 offset;     // byte offset from parent
  u32 size;       // total bytes
  u32 type_size;  // per-element size
  u32 typehash;   // TYPE_HASH(T) = fnv1a(name_of_T) ^ sizeof(T)
} BlobPtr;  // 16 bytes
#define BlobArray(type) BlobPtr
```

**BlobAssetHeader** (`src/engine/assets/blob_asset.h:104-111`):
```c
typedef struct {
  u64 magic;           // 0x54455353414800 ("\0HASSET" LE)
  u64 asset_size;      // total file size in bytes
  u64 asset_type_hash; // ASSET_TYPE(T) = fnv1a(name_of_T) ^ sizeof(T) (cast to u64)
  u32 version;         // 3
  u32 _pad0;           // 0
  BlobArray(StringBlob) dependencies;  // BlobPtr (16 bytes)
} BlobAssetHeader;  // 48 bytes
```

### 1.2 FNV-1a Hash (`src/lib/hash.h:8-15`)

```c
u32 fnv1a_hash(const char *bytes) {
  u32 hash = 0x811c9dc5;
  while (*bytes) {
    hash ^= (u8)*bytes++;
    hash *= 0x01000193;
  }
  return hash;
}
```

Used for:
- `TYPE_HASH(T)` = `fnv1a(#T) ^ sizeof(T)` — validates BlobArray element types
- `ASSET_TYPE(T)` = `fnv1a(#T)` (just the hash, NO xor with sizeof) — stored in header as `u64`
  (ref: `src/engine/assets/assets.h:21` — `#define ASSET_TYPE(name) fnv1a_hash(#name)`)
- `stable_type_hash` = `fnv1a(component_name)` — identifies ECS components (stored as `u64`)

The C# exporter must implement this identically.

### 1.3 Blob Builder Pattern (`src/engine/assets/blob_asset_builder.h`)

The builder allocates sequentially from a linear arena. All offsets are relative to a
parent pointer. The C# equivalent:

1. Write the root struct first (e.g., `SceneBlobAsset`)
2. For each `BlobArray`, allocate space after the root, record `offset = (array_pos - parent_pos)`,
   `size = count * type_size`, `type_size = sizeof(element)`, `typehash = TYPE_HASH(element)`
3. For each `StringBlob`, allocate `(len+1)` rounded to 4 bytes, store `offset = (str_pos - parent_pos)`, `len`
4. For raw data (component bytes), allocate with 8-byte alignment, store offset relative to base
5. Final binary = everything from base to end of last allocation

---

## 2. Scene Blob Format

### 2.1 SceneBlobAsset (`src/engine/scene.h:18-33`)

```c
typedef struct SceneComponentBlob {
    u64 stable_type_hash;  // fnv1a(component_name), stored as u64
    u32 data_size;         // sizeof(component struct)
    u32 data_offset;       // offset from SceneBlobAsset base to raw component bytes
} SceneComponentBlob;  // 16 bytes

typedef struct SceneEntityBlob {
    u32 component_count;
    u32 component_start_index;  // index into flat components array
} SceneEntityBlob;  // 8 bytes

typedef struct SceneBlobAsset {
    BlobAssetHeader header;          // 48 bytes
    BlobArray(SceneEntityBlob) entities;    // 16 bytes (BlobPtr)
    BlobArray(SceneComponentBlob) components;  // 16 bytes (BlobPtr)
} SceneBlobAsset;  // 80 bytes
```

### 2.2 Asset Ref Patching (`src/engine/scene.c:46-96`)

On save, `FIELD_TYPE_ASSET_REF` fields in components are patched:
- The asset's path is added to `header.dependencies` (deduped)
- The Handle is rewritten to `{ .idx = dependency_index, .gen = 1 }`

On load, the engine reverses this: `dependency_index` → loaded `AssetHandle`.

The Unity exporter must do the same patching for `EcsModelRef.model` and
`EcsMaterialRef.material`.

### 2.3 Scene Loading (`src/engine/scene.c:220-262`)

`scene_spawn_entities()` iterates entities, looks up components by
`ecs_component_find_by_type_hash(world, stable_type_hash)`, validates `data_size`
matches `ti->size`, then `ecs_set_ptr()` with the raw bytes. After setting, it patches
asset refs back from dependency indices to live handles.

**Key implication**: component binary data must match the C struct layout exactly (size,
alignment, field order). Unknown components (hash not registered) are silently skipped.

---

## 3. ECS Components

### 3.1 Existing Engine Components (`src/engine/ecs/ecs_components.h`)

These MUST use the exact struct name for `fnv1a_hash` to match.

#### SceneEntityTag (1 byte)
```
stable_type_hash: fnv1a("SceneEntityTag")
Offset  Size  Field
0       1     u8 _     (always 0, marker tag)
Total: 1 byte
```

#### EcsTransform (36 bytes)
```
stable_type_hash: fnv1a("EcsTransform")
Offset  Size  Field
0       12    vec3 position   (3x f32)
12      12    vec3 rotation   (3x f32, euler angles in radians)
24      12    vec3 scale      (3x f32)
Total: 36 bytes
```

#### EcsVisibility (8 bytes)
```
stable_type_hash: fnv1a("EcsVisibility")
Offset  Size  Field
0       4     b32 visible       (u32, 0 or 1)
4       4     b32 casts_shadow  (u32, 0 or 1)
Total: 8 bytes
```

#### EntityName (68 bytes)
```
stable_type_hash: fnv1a("EntityName")
Offset  Size  Field
0       4     u32 len                (string length)
4       64    char[64] value         (null-terminated, zero-padded)
Total: 68 bytes
```
(This is `String64Bytes`: `{ u32 len; char value[64]; }`)

#### EcsModelRef (12 bytes)
```
stable_type_hash: fnv1a("EcsModelRef")
Offset  Size  Field
0       4     u32 model.idx    (patched to dependency index)
4       4     u32 model.gen    (patched to 1)
8       4     u32 mesh_index
Total: 12 bytes
```

#### EcsMaterialRef (8 bytes)
```
stable_type_hash: fnv1a("EcsMaterialRef")
Offset  Size  Field
0       4     u32 material.idx  (patched to dependency index)
4       4     u32 material.gen  (patched to 1)
Total: 8 bytes
```

#### EcsAABB (24 bytes)
```
stable_type_hash: fnv1a("EcsAABB")
Offset  Size  Field
0       12    vec3 min  (3x f32)
12      12    vec3 max  (3x f32)
Total: 24 bytes
```

#### EcsMesh — NOT serialized
`EcsMesh` contains GPU handles (`GpuMesh_Handle`, `Material_Handle`) which are runtime-
only. It is registered with `ECS_COMPONENT_DEFINE` (no reflect), so the scene system
skips it. The demo resolves `EcsModelRef` → `EcsMesh` at runtime.

### 3.2 New Components (Unity-defined, engine registers later)

These components do not exist in the engine yet. The exporter writes them into the scene
blob with their `stable_type_hash`. The engine will skip them until matching component
types are registered. When the engine adds these, it must use the EXACT same struct name
in `ecs_component_register()` so the hash matches.

#### EcsDirectionalLight (16 bytes)
```
stable_type_hash: fnv1a("EcsDirectionalLight")
Offset  Size  Field
0       12    vec3 color      (3x f32, linear RGB)
12      4     f32  intensity
Total: 16 bytes

Direction is derived from EcsTransform.rotation at runtime.
```

#### EcsPointLight (8 bytes)
```
stable_type_hash: fnv1a("EcsPointLight")
Offset  Size  Field
0       4     f32 intensity
4       4     f32 range
Total: 8 bytes

Color could be added later. Position comes from EcsTransform.
For now, use light.color in EcsTransform... actually, let's include color:
```

**Revised — EcsPointLight (20 bytes)**:
```
stable_type_hash: fnv1a("EcsPointLight")
Offset  Size  Field
0       12    vec3 color      (3x f32, linear RGB)
12      4     f32  intensity
16      4     f32  range
Total: 20 bytes

Position from EcsTransform.position at runtime.
```

#### EcsCamera (12 bytes)
```
stable_type_hash: fnv1a("EcsCamera")
Offset  Size  Field
0       4     f32 fov         (degrees)
4       4     f32 near_plane
8       4     f32 far_plane
Total: 12 bytes

Position/rotation from EcsTransform at runtime.
```

#### EcsSceneSettings (32 bytes)
```
stable_type_hash: fnv1a("EcsSceneSettings")
Offset  Size  Field
0       12    vec3 ambient_color      (3x f32, linear RGB)
12      4     f32  ambient_intensity
16      16    vec4 clear_color        (4x f32, RGBA linear)
Total: 32 bytes
```

---

## 4. Coordinate System Conversion

Unity: **left-handed Y-up** (LH). Engine: **right-handed Y-up** (RH).

The difference is Z-axis direction. Negate Z everywhere spatial:

| Data                     | Conversion                                               |
|--------------------------|----------------------------------------------------------|
| Positions (transform)    | `(x, y, z)` → `(x, y, -z)`                             |
| Euler rotations          | `(rx, ry, rz)` → `(-rx, -ry, rz)`                      |
| Directions (light, fwd)  | `(x, y, z)` → `(x, y, -z)`                             |
| Scale                    | unchanged                                                |
| Mesh vertex positions    | negate Z                                                 |
| Mesh vertex normals      | negate Z                                                 |
| Mesh vertex tangents     | negate Z (keep W sign)                                   |
| Mesh indices             | reverse winding per triangle: swap `idx[1]` and `idx[2]` |
| AABB                     | `new_min.z = -old_max.z`, `new_max.z = -old_min.z`      |

---

## 5. Model/Mesh Asset (ModelBlobAsset)

Reference: `src/engine/rendering/mesh.h:69-77`

### 5.1 Binary layout

```
ModelBlobAsset:
  BlobAssetHeader header             // 48 bytes
  u32 mesh_count                     // 4
  u32 joint_count                    // 4 (0 for static)
  mat4 armature_transform            // 64 (identity for static)
  BlobArray(MeshBlobAsset) meshes    // 16
  BlobArray(JointBlobAsset) joints   // 16 (empty for static)
  BlobArray(StringBlob) joint_names  // 16 (empty for static)
```

```
MeshBlobAsset:
  StringBlob name                  // 8
  StringBlob material              // 8  (path to material .hza)
  IndexFormat index_format         // 4  (0=U16, 1=U32)
  u32 index_count                  // 4
  u32 vertex_count                 // 4
  vec3 aabb_min                    // 12
  vec3 aabb_max                    // 12
  BlobArray(void) indices          // 16 (u16[] or u32[], typehash irrelevant since void)
  BlobArray(f32) positions         // 16 (3 floats per vertex, flat)
  BlobArray(f32) normals           // 16
  BlobArray(f32) tangents          // 16 (4 floats per vertex)
  BlobArray(f32) uvs               // 16 (2 floats per vertex)
  b32 has_skin                     // 4  (0 for static)
  BlobArray(u8) joint_indices      // 16 (empty for static)
  BlobArray(f32) joint_weights     // 16 (empty for static)
```

### 5.2 Unity → ModelBlobAsset mapping

For simple scene, each unique `Mesh` → one `ModelBlobAsset` with 1 `MeshBlobAsset`.

Source: `MeshFilter.sharedMesh`:
- `mesh.vertices` → positions (flat f32[], negate Z)
- `mesh.normals` → normals (flat f32[], negate Z)
- `mesh.tangents` → tangents (4 floats/vert, negate Z, keep W)
- `mesh.uv` → uvs (2 floats/vert)
- `mesh.triangles` → indices (swap [1] and [2] per triangle for winding)
- `mesh.bounds` → aabb_min/max (convert Z)
- `MeshRenderer.sharedMaterial` → material StringBlob path
- `mesh.indexFormat == IndexFormat.UInt16` → INDEX_FORMAT_U16 (0), else U32 (1)
- `mesh.vertexCount` ≤ 65535 → use U16

For built-in primitives (cube, plane), the engine already has `generated/cube.hza` and
`generated/quad.hza`. The exporter should detect these and reference the existing assets
instead of re-exporting. Detection: check `mesh.name` for "Cube", "Quad", "Plane", etc.

If re-exporting: extract the Unity mesh data and write a new `.hza`.

### 5.3 Empty BlobArray

For unused arrays (joints, joint_names, skin data on static meshes), write:
```
BlobPtr { .offset = 0, .size = 0, .type_size = sizeof(T), .typehash = TYPE_HASH(T) }
```

### 5.4 Header dependencies for ModelBlobAsset

Models themselves don't have blob-level dependencies (the material path is just a string
inside `MeshBlobAsset.material`, not in `header.dependencies`). The asset loader
(`asset_model_loader` in `src/engine/assets/asset_loaders.c`) discovers material deps at
load time from the mesh's material string.

So `header.dependencies` for model assets = empty array.

---

## 6. Material Asset (MaterialBlobAsset)

Reference: `src/engine/rendering/material.h:195-206`

### 6.1 Binary layout

```
MaterialBlobAsset:
  BlobAssetHeader header                        // 48
  StringBlob shader_path                        // 8
  b32 depth_test                                // 4
  b32 depth_write                               // 4
  GpuCompareFunc depth_compare                  // 4 (enum, u32)
  GpuCullMode cull_mode                         // 4 (enum, u32)
  b32 is_transparent                            // 4
  b32 alpha_cutoff                              // 4
  u32 property_count                            // 4
  BlobArray(MaterialPropertyBlob) properties    // 16
```

```
MaterialPropertyBlob:
  StringBlob name        // 8
  MaterialPropertyType type  // 4 (enum, u32)
  // 4 bytes padding (union starts at offset 16 due to alignment? Let's check)
  union {                // at offset 16
    f32 f;               // 4
    vec2 v2;             // 8
    vec3 v3;             // 12
    vec4 v4;             // 16
    StringBlob texture_path;  // 8
  };
```

**Checking alignment**: `StringBlob name` (8) + `MaterialPropertyType type` (4) = 12.
The union's largest member is `vec4` (16 bytes, 4-byte aligned). So the union likely
starts at offset 12 with no padding. Total = 12 + 16 = 28 bytes.

Actually, let me re-examine. The C struct:
```c
typedef struct MaterialPropertyBlob {
  StringBlob name;             // offset 0, size 8
  MaterialPropertyType type;   // offset 8, size 4
  union {                      // offset 12
    f32 f;                     // 4
    vec2 v2;                   // 8
    vec3 v3;                   // 12
    vec4 v4;                   // 16
    StringBlob texture_path;   // 8
  };
} MaterialPropertyBlob;
```
Union size = 16 (vec4). Total = 8 + 4 + 16 = 28 bytes. Alignment = 4.

### 6.2 Unity → MaterialBlobAsset mapping

**Shader path**: `"standard_static"` for standard opaque/cutout PBR materials.

**Render state**:
- `depth_test`: 1 (true) for opaque
- `depth_write`: 1 for opaque, 0 for transparent
- `depth_compare`: `GPU_COMPARE_GREATER` (0) — engine uses reverse-Z
- `cull_mode`: `GPU_CULL_BACK` (0) default; check Unity material `_Cull` property
- `is_transparent`: 1 if material renderQueue > 2500
- `alpha_cutoff`: 1 if material has `_ALPHATEST_ON` keyword

**GpuCompareFunc values** (`src/engine/gpu/gpu.h:234-241`):
```
GPU_COMPARE_GREATER       = 0
GPU_COMPARE_NEVER         = 1
GPU_COMPARE_EQUAL         = 2
GPU_COMPARE_GREATER_EQUAL = 3
GPU_COMPARE_LESS          = 4
GPU_COMPARE_NOT_EQUAL     = 5
GPU_COMPARE_LESS_EQUAL    = 6
GPU_COMPARE_ALWAYS        = 7
```

**GpuCullMode values** (`src/engine/gpu/gpu.h:219-221`):
```
GPU_CULL_BACK  = 0
GPU_CULL_NONE  = 1
GPU_CULL_FRONT = 2
```

**MaterialPropertyType values** (`src/engine/rendering/material.h:13-23`):
```
MAT_PROP_FLOAT         = 0
MAT_PROP_VEC2          = 1
MAT_PROP_VEC3          = 2
MAT_PROP_VEC4          = 3
MAT_PROP_MAT4          = 4
MAT_PROP_TEXTURE       = 5
MAT_PROP_COLOR3        = 6
MAT_PROP_COLOR4        = 7
MAT_PROP_RENDER_TARGET = 8
```

**URP property mapping**:

| Unity Property          | Engine Property Name     | Type           |
|-------------------------|--------------------------|----------------|
| `_BaseColor` / `_Color` | `"Color"`               | MAT_PROP_VEC4 (3) |
| `_BaseMap`              | `"Albedo"`              | MAT_PROP_TEXTURE (5) |
| `_BumpMap`              | `"Normal Map"`          | MAT_PROP_TEXTURE (5) |
| `_Metallic`             | `"Metallic"`            | MAT_PROP_FLOAT (0) |
| `_Smoothness`           | `"Smoothness"`          | MAT_PROP_FLOAT (0) |
| `_EmissionColor`        | `"Emission Color"`      | MAT_PROP_VEC4 (3) |
| `_MetallicGlossMap`     | `"Metallic"` (texture)  | MAT_PROP_TEXTURE (5) |

For texture properties, `texture_path` is a StringBlob pointing to the texture `.hza`
asset path. For non-texture properties, the value goes directly in the union.

### 6.3 Material header dependencies

Material dependencies = texture asset paths. These go in `header.dependencies`.
The material loader (`asset_material_deps_fn` in `src/engine/assets/asset_loaders.c`)
reads texture properties and queues them as deps.

---

## 7. Texture Asset (TextureBlobAsset)

Reference: `src/engine/rendering/material.h:150-157`

### 7.1 Binary layout

```
TextureBlobAsset:
  BlobAssetHeader header     // 48
  StringBlob image_path      // 8  (path to image file, e.g., "textures/albedo_bc1.ktx")
  TextureFormat format       // 4  (enum, u32)
  u32 width                  // 4
  u32 height                 // 4
  b32 is_srgb                // 4
```

**TextureFormat values** (`src/engine/gpu/gpu.h:559-565`):
```
TEX_FORMAT_RAW   = 0
TEX_FORMAT_BC1   = 1
TEX_FORMAT_BC3   = 2
TEX_FORMAT_BC5   = 3
TEX_FORMAT_BC7   = 4
TEX_FORMAT_ETC2  = 5
TEX_FORMAT_ASTC  = 6
```

### 7.2 Unity → TextureBlobAsset mapping

**Phase 1 (simple)**: Export as `TEX_FORMAT_RAW` (0) with a PNG file.
- `image_path` = relative path to exported PNG
- `width/height` from `texture.width/height`
- `is_srgb` = 1 for albedo/emission, 0 for normal/metallic

**Phase 2 (later)**: Shell out to PVRTexTool to compress to BC1/BC3/BC5/KTX.

### 7.3 Texture header dependencies

Texture depends on the raw image file (loaded as raw blob):
`header.dependencies[0]` = image file path.

---

## 8. Entity Mapping (Simple Scene)

| Unity GameObject     | Entity Components                                                              |
|----------------------|--------------------------------------------------------------------------------|
| Plane                | SceneEntityTag, EntityName, EcsTransform, EcsVisibility, EcsModelRef, EcsMaterialRef |
| Cube                 | SceneEntityTag, EntityName, EcsTransform, EcsVisibility, EcsModelRef, EcsMaterialRef |
| Directional Light    | SceneEntityTag, EntityName, EcsTransform, EcsDirectionalLight                  |
| Point Light          | SceneEntityTag, EntityName, EcsTransform, EcsPointLight                        |
| Main Camera          | SceneEntityTag, EntityName, EcsTransform, EcsCamera                            |
| [Scene Settings]     | SceneEntityTag, EcsSceneSettings                                               |

Scene Settings is a synthetic entity (no matching GameObject) created from
`RenderSettings` and URP Volume data.

---

## 9. C# Implementation Files, Entity Mapping, and Implementation Order

See sections 14, 15, and 16 below (these supersede any earlier numbering).

## 10. File Naming Convention

Output structure:
```
<output_dir>/
  scene.hza                          # SceneBlobAsset
  meshes/
    Cube.hza                         # ModelBlobAsset (or reference generated/cube.hza)
    Plane.hza                        # ModelBlobAsset (or reference generated/quad.hza)
    Character.hza                    # ModelBlobAsset (skinned, with joints)
  materials/
    Default_Lit_mat.hza              # MaterialBlobAsset
  textures/
    albedo_tex.hza                   # TextureBlobAsset
    albedo.png                       # raw image data
  animations/
    Run.hza                          # AnimationBlobAsset
    Idle.hza                         # AnimationBlobAsset
```

Asset paths stored in blobs are relative to the engine's `assets/` directory.

---

## 12. Key Pitfalls

- **Struct padding**: C structs have specific padding rules. Use `[StructLayout(Sequential, Pack=4)]`
  or explicit `[FieldOffset]` in C# to match. Verify with `sizeof` checks.
- **Endianness**: Both platforms are little-endian (x86/x64). No conversion needed.
- **String null termination**: All strings must be null-terminated. Allocate `len+1` bytes,
  round up to 4-byte alignment.
- **BlobPtr offset direction**: Always relative to the PARENT struct pointer, not file start.
  For `StringBlob` inside `MaterialPropertyBlob`, offset is from the `MaterialPropertyBlob` base.
  For `BlobArray` in `SceneBlobAsset`, offset is from `SceneBlobAsset` base (or the field's parent).
- **Asset type hash vs Type hash**: These are DIFFERENT.
  `ASSET_TYPE(T)` = `fnv1a(#T)` — used in `BlobAssetHeader.asset_type_hash`.
  `TYPE_HASH(T)` = `fnv1a(#T) ^ sizeof(T)` — used in `BlobPtr.typehash`.
  Do NOT confuse them. The exporter.c confirms: `asset_type_hash = fnv1a_hash("ModelBlobAsset")` etc.
- **Empty BlobPtr**: For zero-length arrays, write `{ 0, 0, sizeof(T), TYPE_HASH(T) }`.
- **Component data alignment**: Raw component data is allocated at 8-byte alignment
  (see `blob_builder_raw` call in `scene.c:191`).
- **Coordinate conversion**: Must be applied consistently to ALL spatial data. Missing one
  field causes subtle rendering bugs.
- **Unity mesh readable flag**: `Mesh.isReadable` must be true to access vertex data.
  For built-in meshes, may need to instantiate and mark readable.

---

## 13. Animation Asset (AnimationBlobAsset)

Reference: `src/engine/animation/animation.h:10-29`

### 13.1 Binary layout

```
AnimationBlobAsset:                         // sizeof = 80 bytes
  BlobAssetHeader header                    // 48 bytes
  f32 length                                //  4 (animation duration in seconds)
  u32 joint_count                           //  4 (total skeleton joints)
  u32 keyframe_count                        //  4 (number of keyframes)
  BlobArray(KeyframeBlobAsset) keyframes    // 16 (BlobPtr, offset from AnimationBlobAsset base)
  // 4 bytes implicit padding to reach 80

KeyframeBlobAsset:                          // sizeof = 24 bytes
  f32 timestamp                             //  4 (time in seconds)
  u32 transform_count                       //  4 (number of joint transforms in this keyframe)
  BlobArray(JointTransformBlob) joint_transforms  // 16 (BlobPtr, offset from THIS KeyframeBlobAsset)

JointTransformBlob:                         // sizeof = 44 bytes
  u32 joint_index                           //  4 (index into skeleton joint array)
  vec3 translation                          // 12 (3x f32)
  quaternion rotation                       // 16 (4x f32, xyzw)
  vec3 scale                                // 12 (3x f32)
```

### 13.2 Memory layout in file

The exporter (`exporter.c:1957-2013`) lays out the data as:

```
[AnimationBlobAsset]                 offset 0, size 80
[KeyframeBlobAsset × keyframe_count] offset 80 (= sizeof(AnimationBlobAsset))
[16-byte alignment padding]
[JointTransformBlob data]            packed sequentially per keyframe
```

**Offset rules** (confirmed from `exporter.c`):
- `AnimationBlobAsset.keyframes.offset` = offset from `AnimationBlobAsset` base
  (i.e., `sizeof(AnimationBlobAsset)` = 80 for first keyframe)
- `KeyframeBlobAsset[k].joint_transforms.offset` = offset from `KeyframeBlobAsset[k]` base
  (NOT from file start, NOT from AnimationBlobAsset base)

**BlobPtr values for keyframes array**:
```
keyframes.offset    = sizeof(AnimationBlobAsset)    // typically 80
keyframes.size      = keyframe_count * sizeof(KeyframeBlobAsset)  // count * 24
keyframes.type_size = sizeof(KeyframeBlobAsset)     // 24
keyframes.typehash  = TYPE_HASH(KeyframeBlobAsset)  // fnv1a("KeyframeBlobAsset") ^ 24
```

**BlobPtr values for each keyframe's joint_transforms**:
```
joint_transforms.offset    = byte_offset_of_transforms - byte_offset_of_this_keyframe
joint_transforms.size      = transform_count * sizeof(JointTransformBlob)  // count * 44
joint_transforms.type_size = sizeof(JointTransformBlob)   // 44
joint_transforms.typehash  = TYPE_HASH(JointTransformBlob)  // fnv1a("JointTransformBlob") ^ 44
```

### 13.3 Header

```
header.magic           = 0x54455353414800
header.asset_size      = total file size
header.asset_type_hash = fnv1a("AnimationBlobAsset")  // ASSET_TYPE, no xor with sizeof
header.version         = 3
header._pad0           = 0
header.dependencies    = empty BlobPtr { 0, 0, sizeof(StringBlob), TYPE_HASH(StringBlob) }
```

Animations have NO dependencies — they are self-contained.

### 13.4 Joint index mapping

**Critical**: Animation joint indices are **numeric indices** into the skeleton's joint array
(from `ModelAsset.joints`). They are NOT names. The indices must match between the
animation and the model it will be played on.

The skeleton joint order comes from the `ModelBlobAsset` (see section 5). When exporting
from Unity, the exporter must ensure the same joint ordering is used for both the skinned
mesh and the animation clips.

**Unity → joint index mapping**:
1. Export the `SkinnedMeshRenderer.bones` array — this defines the joint order
2. Each bone is a `Transform` in Unity's hierarchy
3. `SkinnedMeshRenderer.bones[i]` corresponds to joint index `i`
4. When exporting AnimationClip curves, find which bone each curve targets and use the
   same index

### 13.5 Unity → AnimationBlobAsset mapping

**Source**: `AnimationClip` + `SkinnedMeshRenderer` (for skeleton binding)

**Algorithm**:
1. Get the skeleton bone list from `SkinnedMeshRenderer.bones[]`
2. Build a name→index map: `boneName → jointIndex`
3. Sample the AnimationClip at each keyframe:
   - Unity stores animation curves per-property (position.x, rotation.x, etc.)
   - Use `AnimationClip.SampleAnimation()` or read `EditorCurveBinding` curves
4. For each sample time:
   - Read each bone's local TRS relative to its parent
   - Create a `JointTransformBlob` per bone that has animation data
5. Sort keyframes by timestamp
6. Write AnimationBlobAsset binary

**Sampling approaches**:

*Option A — Curve-based (precise)*:
- Use `AnimationUtility.GetCurveBindings()` to get all animated properties
- Group bindings by target bone path
- Collect unique timestamps from all curves' keyframes
- Evaluate each curve at each timestamp using `AnimationCurve.Evaluate()`
- Build per-bone TRS at each timestamp

*Option B — Baked sampling (simpler)*:
- Instantiate the model prefab
- Use `AnimationClip.SampleAnimation(gameObject, time)` at regular intervals
- Read each bone transform's localPosition/localRotation/localScale
- Fixed sample rate (e.g., 30fps) → `keyframe_count = duration * sampleRate`

Option A is more faithful to the original data. Option B is simpler to implement
and guaranteed to capture all bones at each frame.

### 13.6 Coordinate conversion for animation

Same LH→RH rules apply to animation transform data:
- `translation`: negate Z → `(x, y, -z)`
- `rotation` (quaternion xyzw): negate X and Y → `(-x, -y, z, w)`
  (This is the quaternion equivalent of negating Z in euler rotations)
- `scale`: unchanged

### 13.7 Skinned mesh export

When exporting a `SkinnedMeshRenderer`, the mesh export (section 5) must also include:
- `has_skin = 1`
- `joint_indices`: 4 `u8` per vertex (from `mesh.boneWeights[v].boneIndexN`)
- `joint_weights`: 4 `f32` per vertex (from `mesh.boneWeights[v].weightN`)
- `ModelBlobAsset.joints[]`: skeleton hierarchy
- `ModelBlobAsset.joint_names[]`: bone names (for retargeting)
- `ModelBlobAsset.armature_transform`: root transform of the armature

**JointBlobAsset** (`src/engine/rendering/mesh.h:17-21`):
```
JointBlobAsset:
  mat4 inverse_bind_matrix     // 64 bytes (from SkinnedMeshRenderer.sharedMesh.bindposes[i])
  i32 parent_index             //  4 (parent joint index, -1 for root)
  BlobArray(u32) children      // 16 (BlobPtr to child index array)
Total: 84 bytes
```

**Unity → JointBlobAsset mapping**:
- `inverse_bind_matrix` = `mesh.bindposes[i]` (convert coordinate system)
- `parent_index` = find parent bone in the bones array, -1 if not found
- `children` = find all bones whose parent is this bone

**Bind pose matrix conversion** (LH→RH):
Negate the Z column and Z row of the 4x4 matrix:
```
[ m00  m01 -m02  m03]
[ m10  m11 -m12  m13]
[-m20 -m21  m22 -m23]
[ m30  m31 -m32  m33]
```

---

## 14. Updated Entity Mapping

| Unity GameObject       | Entity Components                                                              |
|------------------------|--------------------------------------------------------------------------------|
| Static Mesh            | SceneEntityTag, EntityName, EcsTransform, EcsVisibility, EcsModelRef, EcsMaterialRef |
| Skinned Mesh           | SceneEntityTag, EntityName, EcsTransform, EcsVisibility, EcsModelRef, EcsMaterialRef |
| Directional Light      | SceneEntityTag, EntityName, EcsTransform, EcsDirectionalLight                  |
| Point Light            | SceneEntityTag, EntityName, EcsTransform, EcsPointLight                        |
| Main Camera            | SceneEntityTag, EntityName, EcsTransform, EcsCamera                            |
| [Scene Settings]       | SceneEntityTag, EcsSceneSettings                                               |

Skinned meshes use the same `EcsModelRef` as static meshes — the model `.hza` contains
the `has_skin` flag and joint data. The demo resolves skinned vs static at runtime.

---

## 15. Updated C# Implementation Files

All scripts go in `Assets/Editor/HZExporter/`.

### 15.1 `HZBlobWriter.cs` — (same as section 9.1)

### 15.2 `HZComponentDefs.cs` — (same as section 9.2)

### 15.3 `HZMeshExporter.cs` — (updated)

Now handles both static and skinned meshes:
- Static: same as section 9.4
- Skinned: additionally exports `joint_indices`, `joint_weights`, `JointBlobAsset[]`,
  `joint_names[]`, `armature_transform`
- Skeleton hierarchy built from `SkinnedMeshRenderer.bones[]` parent-child relationships

### 15.4 `HZMaterialExporter.cs` — (same as section 9.5)

### 15.5 `HZTextureExporter.cs` — (same as section 9.6)

### 15.6 `HZAnimationExporter.cs` — NEW

Exports Unity `AnimationClip` → `AnimationBlobAsset` `.hza`.

**Algorithm**:
1. Receive `AnimationClip` + bone name→index map (from `SkinnedMeshRenderer`)
2. Collect all unique keyframe timestamps from curve bindings
   (or bake at fixed sample rate)
3. For each timestamp, evaluate all bone curves → local TRS per bone
4. Apply coordinate conversion (negate Z on translation, negate X/Y on quaternion)
5. Build keyframe array sorted by timestamp
6. Write AnimationBlobAsset:
   a. Header (no dependencies)
   b. Keyframe array with BlobPtrs
   c. JointTransformBlob data packed after keyframes (16-byte aligned gap)
   d. BlobPtr offsets: keyframes relative to AnimationBlobAsset, transforms relative
      to their parent KeyframeBlobAsset

### 15.7 `HZSceneExporter.cs` — (updated from section 9.3)

Additionally detects `SkinnedMeshRenderer` and `Animator`/`Animation` components.
For animated objects, triggers animation clip export alongside mesh export.

### 15.8 `HZExporterWindow.cs` — (same as section 9.7)

---

## 16. Updated Implementation Order

1. **HZBlobWriter** — foundation
2. **HZComponentDefs** — struct definitions + hashes
3. **HZMeshExporter** — static meshes first, then skinned meshes
4. **HZMaterialExporter** — materials
5. **HZTextureExporter** — textures (RAW/PNG)
6. **HZAnimationExporter** — animation clips
7. **HZSceneExporter** — assemble scene blob
8. **HZExporterWindow** — editor UI

Test skinned mesh + animation by loading in engine with `demo_animation.c` as reference.

---

## 17. Animation-Specific Pitfalls

- **Joint order must match**: The bone index in animation data must correspond to the same
  bone index in the model's joint array. Both come from `SkinnedMeshRenderer.bones[]`.
- **Quaternion convention**: Unity quaternions are (x, y, z, w). The engine also uses
  (x, y, z, w) — `typedef float quaternion[4]` = `{x, y, z, w}`. No reordering needed,
  only coordinate conversion (negate x, y for LH→RH).
- **Local vs world space**: Animation transforms must be in LOCAL space (relative to parent
  bone). Unity's `AnimationClip.SampleAnimation` sets transforms in local space already.
- **Root motion**: If the animation has root motion, the root bone's translation will include
  movement. The engine doesn't have a root motion system — export as-is for now.
- **AnimationBlobAsset sizeof**: The struct is 80 bytes (48 header + 4 length + 4 joint_count
  + 4 keyframe_count + 16 keyframes BlobPtr + 4 padding). Verify by checking the keyframe
  offset in `exporter.c` — it starts at `sizeof(AnimationBlobAsset)`.
- **Transform data alignment**: The JointTransformBlob array starts after a 16-byte alignment
  gap after the KeyframeBlobAsset array (see `exporter.c:1962`).
- **Not all bones need transforms per keyframe**: A keyframe's `transform_count` can be less
  than `joint_count`. Only bones with animation data are included. The evaluation code handles
  this by iterating only the transforms present.
