# UnityToHZ Project

Unity Editor tool that exports Unity scenes to hz-engine-v3 binary asset format (.hza).

## CLI Tools

Everything can be done from the command line. **Do not ask the user to click anything in Unity.**

### `tools/unity_cmd.ps1` — Remote Unity control

A TCP listener (`RemoteRefresh.cs`, port 7321) runs inside Unity Editor. Commands:

```bash
CMD="powershell -ExecutionPolicy Bypass -File 'D:/dev/unity/UnityToHZ/tools/unity_cmd.ps1'"

$CMD -cmd ping                              # Check Unity is alive → "pong"
$CMD -cmd refresh                           # Trigger C# compilation (AssetDatabase.Refresh)
$CMD -cmd log                               # Get last compile result ("ok" or errors)
$CMD -cmd "exec:ClassName.MethodName"       # Run any static no-arg C# method on main thread
```

The `exec:` command is the primary way to **run exports, tests, and any Unity logic** from CLI. For parameterized operations, create static wrapper methods with hardcoded args.

### `tools/dump_model_hza.py` — Validate exported ModelBlobAsset

```bash
python tools/dump_model_hza.py ExportedAssets/meshes/Cube.hza
```

Parses the full ModelBlobAsset binary: header, BlobPtrs, MeshBlobAsset fields, vertex samples, index samples. Reports validation errors (typehash mismatches, size mismatches, etc.). Exit code 0 = valid, 1 = errors.

### `tools/hza.py` — Shared .hza reader library

Python module with `HzaReader` class, FNV-1a, BlobPtr validation, header parsing. Used by `dump_model_hza.py` and any future validators. Import with `from hza import HzaReader`.

### `tools/validate_hza.ps1` — PowerShell model validator

```bash
powershell -ExecutionPolicy Bypass -File 'D:/dev/unity/UnityToHZ/tools/validate_hza.ps1' -path ExportedAssets/meshes/Cube.hza
```

PowerShell equivalent of `dump_model_hza.py`. Less thorough but works without Python.

## Workflow

### Writing and testing C# scripts

1. Write/edit `.cs` files under `Assets/Editor/HZExporter/`
2. `unity_cmd.ps1 -cmd refresh` — trigger compilation
3. Wait ~10-15s for compile + domain reload
4. `unity_cmd.ps1 -cmd log` — check for errors ("ok" = success)
5. If errors, fix and repeat from step 1

### Running exports

1. Write a static no-arg method (e.g., `HZExportCommands.ExportXBot()`)
2. Compile it (refresh + log)
3. `unity_cmd.ps1 -cmd "exec:HZExportCommands.ExportXBot"` — run the export
4. `python tools/dump_model_hza.py <output.hza>` — validate the result

### Notes

- The TCP listener auto-starts via `[InitializeOnLoad]` and survives domain reloads
- Compile results persist via temp file across reloads
- Connection error = Unity not open or listener not running

## Project Structure

- `Assets/Editor/HZExporter/` — all exporter C# scripts (editor-only)
- `tools/` — CLI tools (unity_cmd, validators, python libs)
- `ExportedAssets/` — export output directory
- `EXPORT_PLAN.md` — detailed binary format spec and implementation plan

## Engine Reference

Target engine: `D:\dev\gabrieldechichi\hz-engine-v3\`
See `EXPORT_PLAN.md` for full binary format documentation.
