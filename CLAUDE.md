# UnityToHZ Project

Unity Editor tool that exports Unity scenes to hz-engine-v5 binary asset format (.hza/.hzt).

## CLI Tools

Everything can be done from the command line. **Do not ask the user to click anything in Unity.**

### Remote Unity control (TCP port 7321)

A TCP listener (`RemoteRefresh.cs`, port 7321) runs inside Unity Editor. It accepts commands via raw TCP.

**macOS:**
```bash
printf 'ping' | nc -w 2 localhost 7321                              # Health check → "pong"
printf 'refresh' | nc -w 2 localhost 7321                           # Trigger C# compilation
printf 'log' | nc -w 2 localhost 7321                               # Get compile result ("ok" or errors)
printf 'exec:ClassName.MethodName' | nc -w 2 localhost 7321         # Run static no-arg method
```

**Windows (PowerShell):**
```powershell
$CMD = "powershell -ExecutionPolicy Bypass -File 'tools/unity_cmd.ps1'"
& $CMD -cmd ping
& $CMD -cmd refresh
& $CMD -cmd log
& $CMD -cmd "exec:ClassName.MethodName"
```

There is also `tools/unity_cmd.sh` for macOS which wraps `nc` and activates Unity via `osascript`.

The `exec:` command is the primary way to **run exports, tests, and any Unity logic** from CLI. For parameterized operations, create static wrapper methods with hardcoded args.

### macOS background update issue (IMPORTANT)

On macOS, Unity's `EditorApplication.update` loop pauses when Unity is not the focused application. `RemoteRefresh.cs` includes `Application.runInBackground = true` and a `QueuePlayerLoopUpdate` timer to mitigate this, but **it is unreliable after domain reloads**.

If commands are queued (`exec_queued:...`) but never execute (no output appears, ExportedAssets stays empty):
1. **Restart Unity**: `osascript -e 'tell application "Unity" to quit' && sleep 3 && open -a "/Applications/Unity/Hub/Editor/6000.3.12f1/Unity.app" --args -projectPath "/Users/gabrieldechichi/dev/unity/UnityToHZ"`
2. Wait for it to be ready: poll `printf 'ping' | nc -w 2 localhost 7321` every 5s until "pong"
3. Then retry the command

If Unity is stuck and `osascript` can't activate it (e.g. fullscreen app blocking), restart is the only reliable option.

### Available export commands

```bash
printf 'exec:HZExportCommands.ExportScene' | nc -w 2 localhost 7321          # Export active scene
printf 'exec:HZExportCommands.OpenSampleScene' | nc -w 2 localhost 7321      # Switch to SampleScene
printf 'exec:HZExportCommands.OpenAssetsShowcase' | nc -w 2 localhost 7321   # Switch to AssetsShowcase
printf 'exec:HZExportCommands.ExportXBot' | nc -w 2 localhost 7321           # Export X Bot mesh
printf 'exec:HZExportCommands.ExportBodyBlock' | nc -w 2 localhost 7321      # Export Body Block animation
printf 'exec:HZExportCommands.ExportTestCube' | nc -w 2 localhost 7321       # Export test cube GLB
```

### Python validators

```bash
python tools/dump_model_hza.py <file.hza>      # Validate ModelBlobAsset
python tools/dump_material_hza.py <file.hza>    # Validate MaterialBlobAsset
python tools/dump_animation_hza.py <file.hza>   # Validate AnimationBlobAsset
```

## Workflow

### Writing and testing C# scripts

1. Write/edit `.cs` files under `Assets/Editor/HZExporter/`
2. `printf 'refresh' | nc -w 2 localhost 7321` — trigger compilation
3. Wait ~15s for compile + domain reload
4. `printf 'log' | nc -w 2 localhost 7321` — check for errors ("ok" = success)
5. If errors, fix and repeat from step 1
6. If "compiling" persists forever, Unity's update loop is stuck — restart Unity (see above)

### Running exports

1. Write a static no-arg method (e.g., `HZExportCommands.ExportXBot()`)
2. Compile it (refresh + log)
3. `printf 'exec:HZExportCommands.ExportXBot' | nc -w 2 localhost 7321`
4. Wait ~20-30s for export to complete
5. Check `ExportedAssets/` for output

### Notes

- The TCP listener auto-starts via `[InitializeOnLoad]` and survives domain reloads
- Compile status is tracked in-memory (volatile field), reset to "ok" after domain reload
- `ExportScene` exports the currently active scene — use `OpenSampleScene`/`OpenAssetsShowcase` to switch
- Connection error = Unity not open or listener not running

## Project Structure

- `Assets/Editor/HZExporter/` — all exporter C# scripts (editor-only)
  - `HZBlobWriter.cs` — binary writer with self-relative BlobPtr/StringBlob
  - `HZGlbWriter.cs` — glTF 2.0 (.glb) intermediate mesh export (static + skinned)
  - `HZMeshExporter.cs` — ModelBlobAsset binary export
  - `HZMaterialExporter.cs` — MaterialBlobAsset export (.hzt text + .hza binary)
  - `HZTextureExporter.cs` — TextureBlobAsset + PVRTexTool BC1/BC3 compression
  - `HZAnimationExporter.cs` — AnimationBlobAsset (baked at 30fps)
  - `HZSceneExporter.cs` — SceneBlobAsset manifest + per-entity EntityBlobAsset (.hzt)
  - `HZExportCommands.cs` — CLI-callable static methods for exports
  - `RemoteRefresh.cs` — TCP listener for CLI control (port 7321)
- `tools/` — CLI tools (unity_cmd, validators, python libs)
- `ExportedAssets/` — export output directory (cross-platform, uses relative paths)
- `EXPORT_PLAN.md` — detailed binary format spec and implementation plan

## Engine Reference

Target engine: hz-engine-v5 (`/Users/gabrieldechichi/dev/gabrieldechichi/hz-engine-v5/` on macOS)

The engine loads Unity-exported scenes from `assets/unity/`. To test:
1. Copy `ExportedAssets/scenes/` and `ExportedAssets/glb/` and `ExportedAssets/animations/` to engine's `assets/unity/`
2. Set `SCENE_FILE` in `src/demos/demo_scene_system.c` to point to the scene
3. `./hzbuild macos --screenshot` to build, run, and capture a screenshot

See `EXPORT_PLAN.md` for full binary format documentation.

## Known Issues / TODO

- **Skeleton bone skipping**: Currently only skips children of root-level Animator GameObjects. Characters nested deeper in the hierarchy (like in AssetsShowcase) export all skeleton bones as empty entities. Needs recursive Animator detection.
- **GLB deduplication**: Multiple GameObjects sharing the same source mesh (e.g. Sphere, Sphere (1)) each export separate GLBs. Should deduplicate by source asset path.
- **PVRTexTool on macOS**: KTX texture compression is skipped if PVRTexToolCLI is not found (logs a warning). Set `PVRTEXTOOL_PATH` env var if available.
- **Skinned mesh in-scene**: Rendered in T-pose without animation integration.
