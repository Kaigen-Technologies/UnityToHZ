# Exporter TODOs and known workarounds

Living list of hacks, fragile spots, and nice-to-haves in the Unity exporter and the engine-side companion code. Sorted by where the work lives.

When something here gets fixed, delete the entry rather than crossing it out.

## Exporter side (Assets/Editor/HZExporter)

### Hacks that work but should be cleaned up

- **`vsBodyStr.Contains("mat.")` heuristic** in `SgCodegen.EmitFromTemplate` decides whether to emit the vertex-stage `MaterialFS` mirror. Works because every `mat.X` access in the generated body comes from a single property handler, but it's textual and brittle. Replace with an explicit `LowerCtx.ReadsMaterial` flag set when the property handler emits a non-texture material reference in vertex stage.

- **Vertex-stage `MaterialFS` mirror duplicates the @fs struct** as plain GLSL (`SgCodegen.FormatVertexMaterialMirror`). Re-implements what the engine preprocessor does for `@material` in the fragment stage. Works because both stages compile to separate SPIR-V units and we iterate `g.Properties` in declaration order on both sides. Real fix is engine-side: have `renderer_shader_preprocess.c` emit the struct (without `DECLARE_TEXTURE` calls) in both stages when a vertex-stage `mat.` reference is detected.

- **`SgLiterals.From(slot, isVertexStage = false)` default parameter** is easy to miss at new call sites. Make it required, or refactor to take the `LowerCtx` so it can pull `IsVertexStage` itself (and reach other context like `Graph` if needed).

- **`unpack_normal_bc5` helper name is hard-coded** in `SampleTexture2DNodeHandler` when emitting normal-map decodes. If the engine ever renames the helper, the exporter silently emits the old name. Move the helper name to a shared constant.

- **`MakeReadableNormalMap` Y-flip is unconditional**. Unity-imported normal maps are DX convention in this project, but a future OpenGL-convention import would render wrong. Read `TextureImporter.flipGreenChannel` and respect it.

- **`MakeReadableNormalMap` Graphics.Blit fallback path** (when `AssetDatabase.GetAssetPath` returns nothing) drops the alpha-packed X channel. Acceptable today because every real normal map is an on-disk asset. Add a custom blit shader that does `UnpackNormal` correctly for the in-memory-texture case.

- **`HelperFunctions` set dedupes by name alone** (`LowerCtx.HelperFunctions: HashSet<(string name, string body)>` but `seenHelpers` in the codegen only keys on name). Two handlers registering colliding names would silently override each other. Either key on `(name, body)` or assert bodies match on duplicate adds.

- **Stale per-LOD GLBs clobber shared `<material>_mat.hzt`**. Each GLB writes its material .hzt by material name; if multiple GLBs share a material (LOD variants, etc.) and they were exported at different times, the last cook wins and may overwrite a richer material with a stale one. Workaround today: manually delete stale GLBs. Real fix: either per-GLB material namespacing in the GLB writer, or cooker-side preservation of the existing material when the candidate has fewer properties.

- **`mat.` references in `gpu_d3d12.c` etc. for the Vegetation vertex stage worked because the GLB cooker happens to honor declaration order** — but the cooker does NOT explicitly guarantee that vs/fs use the same struct layout. If the engine preprocessor ever reorders fields (e.g. group textures first), the vertex-stage mirror in this exporter will silently produce misaligned offsets. The fix is the same engine-side @material-in-@vs change above.

### Intentional `/* TODO */` markers visible in generated output

These are inline in the generated `.hz.glsl` so a programmer reading the output sees them:

- `UVNode` channels 2/3 fall back to UV0 (`v_uv /* TODO: UV channel N not plumbed; falling back to UV0 */`). Mostly hits BakedGI's static/dynamic UV slots which we ignore anyway.
- `PositionNode` non-World space in `@fs` falls back to `v_world_pos /* TODO: PositionNode space N not implemented in @fs */`. Needs additional varyings for Object/View/Tangent space positions.
- `BlendNode` unsupported modes fall back to Screen blend with a `/* TODO BlendMode N */` comment.

### Coverage gaps (auto-generator skips, see tools/sg_nodes_skipped.txt)

41 nodes still throw "no handler registered" when a graph uses them. Most are decorative HLSL helpers that are easy 1-hour additions when they actually come up:
`BlackbodyNode`, `CheckerboardNode`, `ColorMaskNode`, `ContrastNode` (multi-statement body), `DDXNode`/`DDYNode`/`DDXYNode`, `DitherNode`, `EllipseNode`, `FogNode`, `HueNode`, `MainLightDirectionNode` (replace with `hz_main_light().direction`), `NoiseSineWaveNode`, `NormalReconstructZNode`, `NormalUnpackNode`, `PolarCoordinatesNode`, `PolygonNode`, `RadialShearNode`, `RandomRangeNode`, `RectangleNode`, `ReplaceColorNode`, `RotateAboutAxisNode`, `RotateNode`, `RoundedPolygonNode`, `RoundedRectangleNode`, `SaturationNode`, `SawtoothWaveNode`, `SceneDepthDifferenceNode`, `SphereMaskNode`, `SpherizeNode`, `SplitTextureTransformNode`, `SquareWaveNode`, `TriangleWaveNode`, `TwirlNode`, `ViewVectorNode`, `WhiteBalanceNode`.

### Water.shadergraph specific (not yet shipped)

- `ScreenPositionNode` — no handler. Default and Raw modes (only ones used today).
- `SceneColorNode` — auto-pattern emits Unity URP HLSL helpers that don't compile on engine GLSL. Needs an `hz_scene_color(uv)` engine helper plus an override handler.
- `SceneDepthNode` — same problem. Needs `hz_scene_depth_linear(uv)` engine helper.
- Engine needs an opaque-pass color copy and a depth-prepass texture exposed via bindless before any of these can render.
- Engine needs a transparent rendering pass (Water has `SurfaceType=1`, `AlphaMode=0`).

## Engine side (D:/dev/gabrieldechichi/hz-engine-v5)

- **`@material` block is fragment-only** in `renderer_shader_preprocess.c`. Graphs whose vertex stage reads material uniforms (Vegetation wind/bend) need a duplicate struct + unpack helper in @vs, which the exporter currently provides as plain GLSL. Make the preprocessor emit the struct (skipping `DECLARE_TEXTURE` calls) in both stages when needed.

- **`material_set_vec2` was a TODO stub** until 2026-05-10. Same risk exists for any future `MaterialPropertyType` added — the `renderer_create_material_from_asset` switch falls through silently on unhandled types. Consider a compile-time exhaustiveness check or a default-case error log.

- **`hz_baked_gi(world_pos, world_normal)` ignores `world_pos`** — reserved for future APV/SH positional sampling. Update when APV lands.

- **Opaque-pass scene color + depth textures** not exposed yet. Required for Water and any other refraction/depth-fade shader.

- **Vertex `COLOR_0` attribute** consumed at GLB cook time (path verified). If `Texture.flipGreenChannel` import setting ever needs to propagate to the cooker, do it now while the cooker is fresh.

## Workflow gotchas (not code, but worth knowing)

- After modifying the auto-export, **all production shaders must be re-exported** before re-cooking the engine. The HZ Engine → Export Shader Graphs menu now scans `Assets/Idyllic Fantasy Nature/Shader` and `Assets/yyyrjis/ModularAnimeCharacter1/Shaders` in addition to `Assets/ShaderGraphExport`. Add new packs to `ShaderGraphSourceDirs` in `HZExportCommands.cs`.

- After adding a new property to a shader's `@material`, the **cooked `<name>_material.hza` is offset-dependent**. Materials that were cooked against an older shader struct will mis-align. Always delete `cooked/shaders/<name>*` and re-cook when changing the @material struct.

- `material_offset` is shared across all material instances in the SSBO. When the exporter drops or reorders properties relative to the shader struct, the engine cooker matches by NAME so it's tolerant — but a property name typo silently produces a zero value with no warning today (related to the unhandled-type bug above).
