# UnityToHZ Project

Unity Editor tool that exports Unity scenes to hz-engine-v3 binary asset format (.hza).

## Unity Remote Compile

A TCP listener runs inside the Unity Editor on port 7321 (`RemoteRefresh.cs`).
Use the helper script to trigger compilation and check results **without focusing Unity**.

```bash
# Check Unity is alive
powershell -ExecutionPolicy Bypass -File 'D:/dev/unity/UnityToHZ/tools/unity_cmd.ps1' -cmd ping

# Trigger script compilation (AssetDatabase.Refresh)
powershell -ExecutionPolicy Bypass -File 'D:/dev/unity/UnityToHZ/tools/unity_cmd.ps1' -cmd refresh

# Check last compile result ("ok" or error details)
powershell -ExecutionPolicy Bypass -File 'D:/dev/unity/UnityToHZ/tools/unity_cmd.ps1' -cmd log
```

### Workflow for writing C# scripts

1. Write/edit `.cs` files under `Assets/Editor/HZExporter/`
2. Run `unity_cmd.ps1 -cmd refresh` to trigger compilation
3. Wait ~10-15 seconds for Unity to compile and domain-reload
4. Run `unity_cmd.ps1 -cmd log` to check for errors
5. If errors, fix and repeat from step 1

### Notes

- The listener starts automatically via `[InitializeOnLoad]` — no manual setup needed
- After domain reload (successful compile), the listener restarts automatically
- Compile results persist across domain reloads via a temp file
- If Unity is not open or the listener isn't running, the commands will fail with a connection error

## Project Structure

- `Assets/Editor/HZExporter/` — all exporter C# scripts (editor-only)
- `tools/unity_cmd.ps1` — CLI helper for remote Unity commands
- `EXPORT_PLAN.md` — detailed binary format spec and implementation plan

## Engine Reference

The target engine project is at `D:\dev\gabrieldechichi\hz-engine-v3\`.
See `EXPORT_PLAN.md` for full binary format documentation.
