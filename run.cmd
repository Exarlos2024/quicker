@echo off
setlocal

rem ============================================================
rem  QuickerLite launcher
rem
rem  QuickerLite.exe is an apphost: it looks for the .NET runtime in the
rem  standard install locations. If the SDK lives somewhere else (a portable
rem  install under the user profile, for example) the apphost fails with:
rem      Failed to resolve hostfxr.dll [not found]. Error code: 0x80008083
rem  Setting DOTNET_ROOT tells it where the runtime actually is.
rem
rem  NOTE: keep this file ASCII-only - cmd.exe parses batch files using the
rem  OEM code page, and non-ASCII bytes corrupt the parsing.
rem ============================================================

set "DOTNET_DIR=%USERPROFILE%\.workbuddy-ai\binaries\dotnet"
if exist "%DOTNET_DIR%\dotnet.exe" set "DOTNET_ROOT=%DOTNET_DIR%"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "EXE=%~dp0src\QuickerLite\bin\%CONFIG%\net8.0-windows\QuickerLite.exe"

if not exist "%EXE%" (
  echo [ERROR] Not built yet. Run build.cmd first.
  echo         Missing: %EXE%
  exit /b 1
)

echo Starting QuickerLite ...
echo   DOTNET_ROOT = %DOTNET_ROOT%
echo   Executable  = %EXE%
echo.
echo   Press Ctrl+Alt+Q to open the panel.
echo   To quit: right-click the tray icon, then Exit.
echo.

rem Redirect stdio to nul: the GUI process would otherwise inherit this script's
rem stdout/stderr handles, and a parent shell waiting on that pipe never sees EOF.
start "" "%EXE%" >nul 2>nul
exit /b 0
