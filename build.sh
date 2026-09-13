#!/usr/bin/env bash
# ============================================================
#  QuickerLite build helper for POSIX shells (Git Bash / MSYS).
#
#  Why this exists next to build.cmd:
#
#   1. This environment has APPDATA / ProgramData / ProgramFiles unset.
#      NuGet resolves its config directories straight from those variables,
#      and without them the build dies with a completely opaque error:
#          error : Value cannot be null. (Parameter 'path1')
#          [NuGet.targets(745,5)]
#      Nothing in that message hints at the real cause.
#
#   2. bash refuses to `export` names containing parentheses, so the x86
#      variants have to go through `env`.
#
#  Usage:
#    ./build.sh                -> Debug build
#    ./build.sh Release        -> Release build
# ============================================================
set -euo pipefail

CONFIG="${1:-Debug}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This shell runs with MSYS_NO_PATHCONV=1 / MSYS2_ARG_CONV_EXCL=* in the
# environment, so MSYS will NOT rewrite /d/projects/... into D:\projects\...
# for us. Passing a POSIX path straight to dotnet.exe makes MSBuild read it as
# an unknown switch ("error MSB1001"). Convert explicitly.
if command -v cygpath >/dev/null 2>&1; then
  PROJ="$(cygpath -w "$SCRIPT_DIR/src/QuickerLite/QuickerLite.csproj")"
else
  PROJ="$SCRIPT_DIR/src/QuickerLite/QuickerLite.csproj"
  PROJ="$(echo "$PROJ" | sed -E 's|^/([a-zA-Z])/|\1:/|')"
fi

# ---- locate dotnet ----
DOTNET="${DOTNET:-}"
if [ -z "$DOTNET" ]; then
  for candidate in \
    "$HOME/.workbuddy-ai/binaries/dotnet/dotnet.exe" \
    "/c/Program Files/dotnet/dotnet.exe"
  do
    if [ -x "$candidate" ]; then DOTNET="$candidate"; break; fi
  done
fi
if [ -z "$DOTNET" ]; then
  DOTNET="$(command -v dotnet || true)"
fi
if [ -z "$DOTNET" ]; then
  echo "[ERROR] dotnet not found. Install the .NET 8 SDK first." >&2
  exit 1
fi

# ---- restore the Windows environment variables NuGet needs ----
export ProgramData='C:\ProgramData'
export ALLUSERSPROFILE='C:\ProgramData'
export APPDATA="${APPDATA:-C:\\Users\\$USERNAME\\AppData\\Roaming}"
export LOCALAPPDATA="${LOCALAPPDATA:-C:\\Users\\$USERNAME\\AppData\\Local}"
export ProgramFiles='C:\Program Files'
export ProgramW6432='C:\Program Files'
export CommonProgramFiles='C:\Program Files\Common Files'

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# MSBuild keeps worker nodes alive between builds. If an earlier build started a
# node while the environment was incomplete, later builds reuse that stale node
# and keep seeing the OLD environment -- so fixing the variables above appears
# to have no effect. Forbid reuse so every build gets a clean process.
export MSBUILDDISABLENODEREUSE=1
"$DOTNET" build-server shutdown >/dev/null 2>&1 || true

# bash cannot `export` a name containing parentheses, so the x86 variants are
# injected only for the child process via `env`. Their values are already in
# Windows form (drive letter + backslashes), so MSYS leaves them alone.
PF86='C:\Program Files (x86)'
CPF86='C:\Program Files (x86)\Common Files'

echo
echo "[1/2] Building $CONFIG ..."
env "ProgramFiles(x86)=$PF86" \
    "CommonProgramFiles(x86)=$CPF86" \
    "$DOTNET" build "$PROJ" -c "$CONFIG" -v minimal -nodeReuse:false

# The staged exe is published self-contained on purpose.
#
# A plain build output is framework-dependent: at startup it asks for
# Microsoft.WindowsDesktop.App 8.0, and having .NET 10 installed does NOT
# satisfy that (roll-forward never crosses a major version) -- the exe then
# greets you with "You must install or update .NET". Self-contained costs
# ~40s and ~70MB per build, and buys an exe that starts on any Windows box.
#
# -o must be in Windows form: this shell runs with MSYS_NO_PATHCONV=1, so a
# POSIX path reaching dotnet is read as a rooted Windows path and silently
# writes to D:\d\projects\... instead of failing.
RELEASE="$SCRIPT_DIR/release"
RELEASE_WIN="$(cygpath -w "$RELEASE")"

echo "[2/2] Publishing self-contained exe to release/ ..."
DEBUGTYPE=none
if [ "$CONFIG" = "Debug" ]; then DEBUGTYPE=embedded; fi
env "ProgramFiles(x86)=$PF86" \
    "CommonProgramFiles(x86)=$CPF86" \
    "$DOTNET" publish "$PROJ" \
      -c "$CONFIG" \
      -r win-x64 \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true \
      -p:DebugType="$DEBUGTYPE" \
      -o "$RELEASE_WIN" \
      -nodeReuse:false

# Leftovers from the old framework-dependent staging would sit next to a
# self-contained bundle and read as "the app needs these". It does not.
rm -f "$RELEASE/QuickerLite.dll" \
      "$RELEASE/QuickerLite.deps.json" \
      "$RELEASE/QuickerLite.runtimeconfig.json"
rm -rf "$RELEASE/selfcontained"

echo
echo "[OK] Output: $RELEASE/QuickerLite.exe"
echo
