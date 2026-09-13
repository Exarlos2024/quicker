@echo off
setlocal

rem ============================================================
rem  QuickerLite build script
rem
rem  Usage:
rem    build.cmd                 -> Debug build
rem    build.cmd Release         -> Release build
rem    build.cmd Release run     -> build then launch
rem
rem  NOTE: keep this file ASCII-only. cmd.exe parses batch files with
rem  the OEM code page; non-ASCII comments get mangled and can break
rem  command parsing in confusing ways.
rem ============================================================

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

rem ---- locate dotnet ----
set "DOTNET_EXE="
if exist "%USERPROFILE%\.workbuddy-ai\binaries\dotnet\dotnet.exe" set "DOTNET_EXE=%USERPROFILE%\.workbuddy-ai\binaries\dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET_EXE (
  where dotnet >nul 2>nul
  if not errorlevel 1 set "DOTNET_EXE=dotnet"
)
if not defined DOTNET_EXE (
  echo [ERROR] dotnet.exe not found. Install the .NET 8 SDK first:
  echo         https://dotnet.microsoft.com/download
  exit /b 1
)

rem ---- restore missing Windows environment variables ----
rem NuGet resolves its config directories straight from these variables.
rem In stripped-down environments (minimal shells, CI sandboxes, containers)
rem some of them can be absent, and NuGet then fails with a completely opaque
rem error that gives no hint about the real cause:
rem     Value cannot be null. (Parameter 'path1')
rem
rem Which variable each NuGet path needs (verified by probing NuGet.Common):
rem   UserSettingsDirectory              -> APPDATA
rem   MachineWideSettingsBaseDirectory   -> ProgramFiles / ProgramFiles(x86)
if not defined ProgramData        set "ProgramData=C:\ProgramData"
if not defined ALLUSERSPROFILE    set "ALLUSERSPROFILE=C:\ProgramData"
if not defined APPDATA            set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA       set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
if not defined ProgramFiles       set "ProgramFiles=C:\Program Files"
if not defined ProgramW6432       set "ProgramW6432=C:\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=C:\Program Files\Common Files"

rem Parentheses in variable names break `if defined`, so check via a proxy.
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "ProgramFiles(x86)=C:\Program Files (x86)"
set "CPF86=%CommonProgramFiles(x86)%"
if not defined CPF86 set "CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files"

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem ---- disable MSBuild node reuse ----
rem MSBuild keeps worker node processes alive between builds to speed things up.
rem If an earlier build started a node while the environment was incomplete,
rem every later build reuses that stale node and keeps seeing the OLD
rem environment -- so fixing the variables above appears to have no effect.
rem Shut the nodes down and forbid reuse so each build gets a clean process.
set "MSBUILDDISABLENODEREUSE=1"
"%DOTNET_EXE%" build-server shutdown >nul 2>nul

set "PROJ=%~dp0src\QuickerLite\QuickerLite.csproj"
set "OUTDIR=%~dp0src\QuickerLite\bin\%CONFIG%\net8.0-windows"

echo.
echo [1/2] Building %CONFIG% ...
"%DOTNET_EXE%" build "%PROJ%" -c %CONFIG% -v minimal -nodeReuse:false
if errorlevel 1 (
  echo.
  echo [FAILED] Build error.
  exit /b 1
)

echo.
echo [2/2] Build succeeded.
echo Output: %OUTDIR%\QuickerLite.exe
echo.

if /i "%~2"=="run" (
  echo Launching QuickerLite ...
  start "" "%OUTDIR%\QuickerLite.exe"
)

exit /b 0
