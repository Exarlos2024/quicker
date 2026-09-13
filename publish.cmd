@echo off
setlocal

rem ============================================================
rem  QuickerLite self-contained publish
rem
rem  Produces a standalone exe that needs no .NET runtime installed,
rem  written to release\ -- the same place build.cmd writes to, so there is
rem  exactly one exe to look for.
rem
rem  Why this matters:
rem   1) The debug exe needs the .NET 8 runtime present on the machine.
rem   2) The autostart entry stores the exe path. If the runtime lives in a
rem      non-standard location, boot fails with:
rem          Failed to resolve hostfxr.dll
rem      The self-contained build has no such dependency.
rem
rem  NOTE: keep this file ASCII-only - cmd.exe parses batch files using the
rem  OEM code page, and non-ASCII bytes corrupt the parsing.
rem ============================================================

set "DOTNET_DIR=%USERPROFILE%\.workbuddy-ai\binaries\dotnet"
set "DOTNET_EXE="
if exist "%DOTNET_DIR%\dotnet.exe" set "DOTNET_EXE=%DOTNET_DIR%\dotnet.exe"
if not defined DOTNET_EXE if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET_EXE (
  where dotnet >nul 2>nul
  if not errorlevel 1 set "DOTNET_EXE=dotnet"
)
if not defined DOTNET_EXE (
  echo [ERROR] dotnet.exe not found. Install the .NET 8 SDK first.
  exit /b 1
)

rem see build.cmd for why these are needed
if not defined ProgramData        set "ProgramData=C:\ProgramData"
if not defined ALLUSERSPROFILE    set "ALLUSERSPROFILE=C:\ProgramData"
if not defined APPDATA            set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA       set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
if not defined ProgramFiles       set "ProgramFiles=C:\Program Files"
if not defined ProgramW6432       set "ProgramW6432=C:\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=C:\Program Files\Common Files"
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "ProgramFiles(x86)=C:\Program Files (x86)"
set "CPF86=%CommonProgramFiles(x86)%"
if not defined CPF86 set "CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files"

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "MSBUILDDISABLENODEREUSE=1"

set "PROJ=%~dp0src\QuickerLite\QuickerLite.csproj"
set "OUT=%~dp0release"

echo.
echo Publishing self-contained single-file build to release\ ...
echo The .NET runtime pack is downloaded on first run, so this may take a while.
echo.

"%DOTNET_EXE%" publish "%PROJ%" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o "%OUT%"

if errorlevel 1 (
  echo.
  echo [FAILED] Publish error.
  exit /b 1
)

rem Same reason as in build.cmd: drop the leftovers of the old
rem framework-dependent staging, which would otherwise sit next to a
rem self-contained bundle and read as "the app needs these". It does not.
if exist "%OUT%\QuickerLite.dll" del /q "%OUT%\QuickerLite.dll"
if exist "%OUT%\QuickerLite.deps.json" del /q "%OUT%\QuickerLite.deps.json"
if exist "%OUT%\QuickerLite.runtimeconfig.json" del /q "%OUT%\QuickerLite.runtimeconfig.json"
if exist "%OUT%\selfcontained" rmdir /s /q "%OUT%\selfcontained"

echo.
echo Done. Standalone exe:
echo   %OUT%\QuickerLite.exe
echo   Double-click it - no .NET runtime needed on the target machine.
echo.
exit /b 0
