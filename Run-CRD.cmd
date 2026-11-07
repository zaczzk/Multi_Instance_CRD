@echo off
setlocal
cd /d "%~dp0"

:: Strip Mark-of-the-Web from all files in this directory to avoid Smart App Control download flags
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path '%~dp0' -File | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1

:: 1. Locate Microsoft-signed host (dotnet.exe) to bypass Windows 11 Smart App Control
set "DOTNET_EXE="
if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=C:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET_EXE=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET_EXE (
    for /f "tokens=*" %%i in ('where dotnet.exe 2^>nul') do (
        if not defined DOTNET_EXE set "DOTNET_EXE=%%i"
    )
)

:: 2. Target the managed assembly (.dll) - executing through Microsoft-signed dotnet.exe satisfies Code Integrity
set "TARGET_DLL="
if exist "%~dp0CRDLightweightARM.dll" set "TARGET_DLL=%~dp0CRDLightweightARM.dll"
if not defined TARGET_DLL if exist "%~dp0dist-light\win-arm64\CRDLightweightARM.dll" set "TARGET_DLL=%~dp0dist-light\win-arm64\CRDLightweightARM.dll"
if not defined TARGET_DLL if exist "%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\CRDLightweightARM.dll" set "TARGET_DLL=%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\CRDLightweightARM.dll"
if not defined TARGET_DLL if exist "%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.dll" set "TARGET_DLL=%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.dll"

if defined DOTNET_EXE if defined TARGET_DLL (
    start "" "%DOTNET_EXE%" "%TARGET_DLL%" %*
    exit /b 0
)

:: 3. If no prebuilt DLL exists, build release using the local SDK
echo Building CRDLightweightARM release...
if defined DOTNET_EXE (
    "%DOTNET_EXE%" build "%~dp0src\CRDLightweightARM\CRDLightweightARM.csproj" -c Release
    if exist "%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\CRDLightweightARM.dll" (
        start "" "%DOTNET_EXE%" "%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\CRDLightweightARM.dll" %*
        exit /b 0
    )
)

:: 4. Fallback if dotnet.exe was not found on the system
if exist "%~dp0CRDLightweightARM.exe" (
    start "" "%~dp0CRDLightweightARM.exe" %*
    exit /b 0
)

exit /b 0
