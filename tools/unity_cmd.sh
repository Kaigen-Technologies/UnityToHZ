#!/bin/bash
# unity_cmd.sh — macOS equivalent of unity_cmd.ps1
# Sends a command to Unity's RemoteRefresh TCP listener (port 7321).
# On macOS, Unity pauses its update loop when backgrounded, so this script
# activates Unity to ensure commands are processed.
#
# Usage:
#   ./tools/unity_cmd.sh ping
#   ./tools/unity_cmd.sh refresh
#   ./tools/unity_cmd.sh log
#   ./tools/unity_cmd.sh "exec:HZExportCommands.ExportScene"

CMD="${1:-ping}"
PORT=7321
WAIT="${2:-0}"  # optional: seconds to keep Unity focused (for exec commands)

# Activate Unity so its update loop processes the command
osascript -e 'tell application "Unity" to activate' 2>/dev/null
sleep 0.5

# Send the command
RESPONSE=$(printf '%s' "$CMD" | nc -w 2 localhost $PORT 2>/dev/null)
if [ $? -ne 0 ]; then
    echo "ERROR: Could not connect to Unity (port $PORT). Is Unity open?" >&2
    exit 1
fi
echo "$RESPONSE"

# If wait time specified, keep Unity focused
if [ "$WAIT" -gt 0 ] 2>/dev/null; then
    sleep "$WAIT"
fi
