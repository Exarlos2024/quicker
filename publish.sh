#!/usr/bin/env bash
# ============================================================
#  QuickerLite self-contained publish (POSIX / Git Bash).
#
#  Same job as publish.cmd, but runnable from bash -- cmd.exe is blocked
#  by security policy on this host, and `cmd //c` from bash degenerates
#  into an interactive shell, so publish.cmd is unreachable here.
#
#  Output: dist/QuickerLite.exe -- no .NET runtime required on the target
#  machine, which matters because the autostart entry stores this exe path
#  and a portable SDK location makes the Debug build fail at boot with
#  "Failed to resolve hostfxr.dll".
#
#  Usage:
#    ./publish.sh
# ============================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# MSYS_NO_PATHCONV=1 is set in this shell, so convert the paths ourselves.
# This matters more for -o than for the project path: a POSIX path reaching
# dotnet is read as a *rooted* Windows path, so "-o /d/projects/app/dist"
# silently writes to "D:\d\projects\app\dist" instead of failing.
PROJ="$(cygpath -w "$SCRIPT_DIR/src/QuickerLite/QuickerLite.csproj")"
OUT="$(cygpath -w "$SCRIPT_DIR/dist")"

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

# NuGet derives its config directories from these; unset they produce the
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

echo
echo "Publishing self-contained single-file build to dist/ ..."
echo "The .NET runtime pack is downloaded on first run, so this may take a while."
echo

env "ProgramFiles(x86)=$PF86" \
    "CommonProgramFiles(x86)=$CPF86" \
    "$DOTNET" publish "$PROJ" \
      -c Release \
      -r win-x64 \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true \
      -p:DebugType=none \
      -o "$OUT" \
      -nodeReuse:false

echo
echo "Done. Standalone exe:"
ls -l "$OUT/QuickerLite.exe"

# Same reason as in build.sh: keep the shippable exe somewhere findable
# instead of three levels down under bin/. Self-contained, so this one copy
# is the whole program -- no sidecar files needed.
RELEASE="$SCRIPT_DIR/release/selfcontained"
mkdir -p "$RELEASE"
cp -f "$SCRIPT_DIR/dist/QuickerLite.exe" "$RELEASE/"
echo "Copied to: $RELEASE/QuickerLite.exe"
echo
