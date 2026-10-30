param(
    [ValidateSet("arm64", "x64")]
    [string]$Arch = "arm64",
    [switch]$Run,
    [switch]$PublishStandalone,
    [switch]$PublishFramework,
    [switch]$PublishAll
)

# Stop any running CRDLightweightARM instances so single-file bundle overwrite never fails
$running = Get-Process -Name "CRDLightweightARM*" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running CRDLightweightARM instance(s)..." -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
}
Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and ($_.Name -eq "dotnet.exe" -or $_.Name -eq "CRDLightweightARM.exe") -and $_.CommandLine -like "*CRDLightweightARM.dll*" } | ForEach-Object {
    Write-Host "Stopping process $($_.ProcessId) ($($_.Name)) hosting CRD..." -ForegroundColor Yellow
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

$DotnetPath = "$HOME\AppData\Local\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $DotnetPath)) {
    $DotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
}

if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) {
    Write-Error ".NET 9 SDK not found in path or in $HOME\AppData\Local\Microsoft\dotnet\dotnet.exe"
    exit 1
}

$Project = "src\CRDLightweightARM\CRDLightweightARM.csproj"

function Invoke-CodeSign([string]$binaryPath) {
    if (Test-Path $binaryPath) {
        $devCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -like "*CRDLightweightARM*" } | Select-Object -First 1
        if ($devCert) {
            Write-Host "Signing $binaryPath with developer cert ($($devCert.Thumbprint.Substring(0,8)))..." -ForegroundColor Cyan
            try {
                Set-AuthenticodeSignature -FilePath $binaryPath -Certificate $devCert -TimestampServer "http://timestamp.digicert.com" -ErrorAction Stop | Out-Null
            } catch {
                Set-AuthenticodeSignature -FilePath $binaryPath -Certificate $devCert | Out-Null
            }
        }
    }
}

if ($PublishAll) {
    Write-Host "Publishing Dual-Architecture (ARM64 Snapdragon + x64 Intel/AMD)..." -ForegroundColor Cyan

    Write-Host "1/2 Compiling native ARM64 release..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-arm64
    Invoke-CodeSign "./dist/win-arm64/CRDLightweightARM.exe"

    Write-Host "2/2 Compiling native x64 release..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-x64
    Invoke-CodeSign "./dist/win-x64/CRDLightweightARM.exe"

    Write-Host "Dual-Architecture packages published successfully!" -ForegroundColor Green
    Write-Host "ARM64: ./dist/win-arm64/CRDLightweightARM.exe" -ForegroundColor Green
    Write-Host "x64:   ./dist/win-x64/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

$targetRid = "win-$Arch"

if ($PublishStandalone) {
    Write-Host "Publishing self-contained $Arch ($targetRid) executable..." -ForegroundColor Cyan
    & $DotnetPath publish $Project -c Release -r $targetRid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "./dist-$Arch-standalone"
    Invoke-CodeSign "./dist-$Arch-standalone/CRDLightweightARM.exe"
    Write-Host "Done! Output: ./dist-$Arch-standalone/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

if ($PublishFramework) {
    Write-Host "Publishing framework-dependent $Arch ($targetRid) release..." -ForegroundColor Cyan
    & $DotnetPath publish $Project -c Release -r $targetRid -o ./dist
    Invoke-CodeSign "./dist/CRDLightweightARM.exe"
    Write-Host "Done! Output: ./dist/CRDLightweightARM.exe" -ForegroundColor Green
    exit 0
}

Write-Host "Building CRD ($Arch) Release..." -ForegroundColor Cyan
& $DotnetPath build $Project -c Release -r $targetRid
Invoke-CodeSign "src\CRDLightweightARM\bin\Release\net9.0-windows\$targetRid\CRDLightweightARM.exe"

if ($Run) {
    Write-Host "Launching Chrome Remote Desktop Lightweight ($Arch)..." -ForegroundColor Green
    & $DotnetPath run --project $Project -c Release -r $targetRid
}
