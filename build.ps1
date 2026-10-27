param(
    [switch]$Run,
    [switch]$PublishStandalone,
    [switch]$PublishFramework
)

$DotnetPath = "$HOME\AppData\Local\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $DotnetPath)) {
    $DotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
}

if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) {
    Write-Error ".NET 9 SDK not found in path or in $HOME\AppData\Local\Microsoft\dotnet\dotnet.exe"
    exit 1
}

$Project = "src\CRDLightweightARM\CRDLightweightARM.csproj"

if ($PublishStandalone) {
    Write-Host "Publishing self-contained ARM64 executable..." -ForegroundColor Cyan
    & $DotnetPath publish $Project -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist-standalone
    Write-Host "Done! Output: ./dist-standalone/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

if ($PublishFramework) {
    Write-Host "Publishing framework-dependent ARM64 release..." -ForegroundColor Cyan
    & $DotnetPath publish $Project -c Release -r win-arm64 -o ./dist
    Write-Host "Done! Output: ./dist/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

Write-Host "Building CRD-ARM Release..." -ForegroundColor Cyan
& $DotnetPath build $Project -c Release

if ($Run) {
    Write-Host "Launching Chrome Remote Desktop Lightweight ARM..." -ForegroundColor Green
    & $DotnetPath run --project $Project
}
