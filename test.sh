#!/usr/bin/env bash
# ============================================================
#  Run the pure-logic regression cases (POSIX / Git Bash).
#
#  Two suites: TileReorder (drag index math) and KeyComboParser (keystroke
#  parsing). Both are UI-free on purpose, so they run without launching a window.
#
#  Exits non-zero if any assertion fails, so it can gate a build.
#
#  Usage:
#    ./test.sh
# ============================================================
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# MSYS_NO_PATHCONV=1 is set in this shell, so a POSIX path would reach
# MSBuild as an unknown switch (error MSB1001). Convert explicitly.
PROJ="$(cygpath -w "$SCRIPT_DIR/tests/TileReorder/TileReorderTests.csproj")"

DOTNET="${DOTNET:-}"
if [ -z "$DOTNET" ]; then
  for candidate in \
    "$HOME/.workbuddy-ai/binaries/dotnet/dotnet.exe" \
    "/c/Program Files/dotnet/dotnet.exe"
  do
    if [ -x "$candidate" ]; then DOTNET="$candidate"; break; fi
  done
fi
[ -n "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
if [ -z "$DOTNET" ]; then
  echo "[ERROR] dotnet not found. Install the .NET 8 SDK first." >&2
  exit 1
fi

# NuGet locates its config directories through these; unset they produce the
# opaque "error : Value cannot be null. (Parameter 'path1')".
export ProgramData='C:\ProgramData'
export ALLUSERSPROFILE='C:\ProgramData'
export APPDATA="${APPDATA:-C:\\Users\\$USERNAME\\AppData\\Roaming}"
export LOCALAPPDATA="${LOCALAPPDATA:-C:\\Users\\$USERNAME\\AppData\\Local}"
export ProgramFiles='C:\Program Files'
export ProgramW6432='C:\Program Files'
export CommonProgramFiles='C:\Program Files\Common Files'
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1

# bash cannot `export` a name containing parentheses -- inject via env.
PF86='C:\Program Files (x86)'
CPF86='C:\Program Files (x86)\Common Files'

env "ProgramFiles(x86)=$PF86" \
    "CommonProgramFiles(x86)=$CPF86" \
    "$DOTNET" run --project "$PROJ" -c Debug -v quiet -nodeReuse:false

STATUS=$?

if [ "$STATUS" -ne 0 ]; then
  echo
  echo "[FAILED] 有用例没通过（退出码 $STATUS）" >&2
fi

exit "$STATUS"
