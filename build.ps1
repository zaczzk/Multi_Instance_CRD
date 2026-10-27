param(
    [ValidateSet("arm64", "x64")]
    [string]$Arch = "arm64",
    [switch]$Run,
    [switch]$PublishStandalone,
    [switch]$PublishFramework,
    [switch]$PublishAll
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

if ($PublishAll) {
    Write-Host "Publishing Dual-Architecture (ARM64 Snapdragon + x64 Intel/AMD)..." -ForegroundColor Cyan

    Write-Host "1/2 Compiling native ARM64 release..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-arm64

    Write-Host "2/2 Compiling native x64 release..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-x64

    Write-Host "Dual-Architecture packages published successfully!" -ForegroundColor Green
    Write-Host "ARM64: ./dist/win-arm64/CRDLightweightARM.exe" -ForegroundColor Green
    Write-Host "x64:   ./dist/win-x64/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

$targetRid = "win-$Arch"

if ($PublishStandalone) {
    Write-Host "Publishing self-contained $Arch ($targetRid) executable..." -ForegroundColor Cyan
    & $DotnetPath publish $Project -c Release -r $targetRid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "./dist-$Arch-standalone"
    Write-Host "Done! Output: ./dist-$Arch-standalone/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

if ($PublishFramework) {
    Write-Host "Publishing framework-dependent $Arch ($targetRid) release..." -ForegroundColor Cyan
    & $DotnetPath publish $Project -c Release -r $targetRid -o ./dist
    Write-Host "Done! Output: ./dist/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

Write-Host "Building CRD ($Arch) Release..." -ForegroundColor Cyan
& $DotnetPath build $Project -c Release -r $targetRid

if ($Run) {
    Write-Host "Launching Chrome Remote Desktop Lightweight ($Arch)..." -ForegroundColor Green
    & $DotnetPath run --project $Project
}
