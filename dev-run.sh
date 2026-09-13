#!/usr/bin/env bash
# ============================================================
#  Launch QuickerLite for a few seconds, dump its log, then kill it.
#
#  Why this exists:
#    QuickerLite is a tray-resident GUI app with no console output, and the
#    agent session reaps the whole process tree as soon as the command
#    returns. So "does it actually start?" can only be answered by launching
#    it, waiting, reading %AppData%\QuickerLite\quickerlite.log, and killing it.
#
#  Usage:
#    ./dev-run.sh              -> run the Debug build for 8 seconds
#    ./dev-run.sh 15           -> run for 15 seconds
#    ./dev-run.sh 8 Release    -> run the Release build
# ============================================================
set -uo pipefail

SECONDS_TO_RUN="${1:-8}"
CONFIG="${2:-Debug}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/src/QuickerLite/bin/$CONFIG/net8.0-windows"
EXE="$OUT_DIR/QuickerLite.exe"

DATA_DIR="${APPDATA_DIR:-/c/Users/$USERNAME/AppData/Roaming/QuickerLite}"
LOG="$DATA_DIR/quickerlite.log"

if [ ! -f "$EXE" ]; then
  echo "[ERROR] not built yet: $EXE" >&2
  echo "        run ./build.sh $CONFIG first" >&2
  exit 1
fi

# The apphost looks for the runtime in the standard install locations. This
# SDK is portable, so point DOTNET_ROOT at it explicitly.
export DOTNET_ROOT='C:\Users\'"$USERNAME"'\\.workbuddy-ai\\binaries\\dotnet'
export APPDATA='C:\Users\'"$USERNAME"'\\AppData\\Roaming'
export LOCALAPPDATA='C:\Users\'"$USERNAME"'\\AppData\\Local'
export ProgramData='C:\ProgramData'
export ALLUSERSPROFILE='C:\ProgramData'

# Remember where the log ended so we only print what this run added.
BEFORE=0
if [ -f "$LOG" ]; then BEFORE=$(wc -l < "$LOG"); fi

"$EXE" &
APP_PID=$!

sleep "$SECONDS_TO_RUN"

echo "=== new log lines ==="
if [ -f "$LOG" ]; then
  tail -n "+$((BEFORE + 1))" "$LOG"
else
  echo "(no log file at $LOG)"
fi

taskkill /F /IM QuickerLite.exe >/dev/null 2>&1 || kill "$APP_PID" 2>/dev/null || true
sleep 1
echo "=== stopped ==="
