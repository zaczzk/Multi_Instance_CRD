@echo off
setlocal
cd /d "%~dp0"

:: 1. Check for standalone executable in current directory (portable zip / installed app)
if exist "%~dp0CRDLightweightARM.exe" (
    start "" "%~dp0CRDLightweightARM.exe" %*
    exit /b 0
)

:: 2. Check for framework-dependent DLL in current directory
set DOTNET_EXE=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe
if not exist "%DOTNET_EXE%" set DOTNET_EXE=dotnet

if exist "%~dp0CRDLightweightARM.dll" (
    start "" "%DOTNET_EXE%" "%~dp0CRDLightweightARM.dll" %*
    exit /b 0
)

:: 3. Check for compiled binary in source development tree
set DEV_EXE=%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.exe
if exist "%DEV_EXE%" (
    start "" "%DEV_EXE%" %*
    exit /b 0
)

set DEV_DLL=%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.dll
if exist "%DEV_DLL%" (
    start "" "%DOTNET_EXE%" "%DEV_DLL%" %*
    exit /b 0
)

:: 4. Build native ARM64 release if not yet compiled
echo Building CRDLightweightARM native ARM64 release...
powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if exist "%DEV_EXE%" (
    start "" "%DEV_EXE%" %*
    exit /b 0
)
if exist "%DEV_DLL%" (
    start "" "%DOTNET_EXE%" "%DEV_DLL%" %*
    exit /b 0
)

exit /b 0
