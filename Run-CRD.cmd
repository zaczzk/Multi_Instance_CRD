@echo off
set DOTNET_EXE=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe
if not exist "%DOTNET_EXE%" set DOTNET_EXE=dotnet
set APP_DLL=%~dp0src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.dll
if not exist "%APP_DLL%" (
    echo Building CRDLightweightARM native ARM64 release...
    powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1"
)
start "" "%DOTNET_EXE%" "%APP_DLL%" %*
exit /b 0
