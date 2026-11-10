param(
    [ValidateSet("arm64", "x64")]
    [string]$Arch = "arm64",
    [switch]$Run,
    [switch]$PublishStandalone,
    [switch]$PublishFramework,
    [switch]$PublishAll,
    [switch]$BuildInstallers,
    [string]$Version = "1.0.0"
)

# All paths below are relative to the repository root, wherever the script is started from.
# The caller's location is restored at the end (the finally block also runs on exit).
Push-Location -LiteralPath $PSScriptRoot
try {

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

function Find-ISCC {
    $paths = @(
        "C:\Users\$env:USERNAME\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $paths) {
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

if ($PublishAll -or $BuildInstallers) {
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "Building Full (.NET Included) & Light (.NET Excluded) Dist" -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Cyan

    # Start from empty output folders so stale files never end up in the installers or zips.
    foreach ($dir in "./dist", "./dist-light", "./dist-installers") {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }

    # 1. Full Self-Contained (.NET Included)
    Write-Host "1/4 Publishing Full ARM64 (Self-Contained)..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-arm64
    Invoke-CodeSign "./dist/win-arm64/CRDLightweightARM.exe"

    Write-Host "2/4 Publishing Full x64 (Self-Contained)..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-x64
    Invoke-CodeSign "./dist/win-x64/CRDLightweightARM.exe"

    # 2. Lightweight (.NET Excluded)
    Write-Host "3/4 Publishing Light ARM64 (Framework-Dependent)..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-arm64 --self-contained false -o ./dist-light/win-arm64
    Invoke-CodeSign "./dist-light/win-arm64/CRDLightweightARM.exe"

    Write-Host "4/4 Publishing Light x64 (Framework-Dependent)..." -ForegroundColor Yellow
    & $DotnetPath publish $Project -c Release -r win-x64 --self-contained false -o ./dist-light/win-x64
    Invoke-CodeSign "./dist-light/win-x64/CRDLightweightARM.exe"

    if ($BuildInstallers) {
        $iscc = Find-ISCC
        if ($iscc) {
            Write-Host "`nBuilding Inno Setup Windows Installers (v$Version)..." -ForegroundColor Cyan
            New-Item -ItemType Directory -Path "./dist-installers" -Force | Out-Null
            
            Write-Host "Compiling .NET-Included Installer..." -ForegroundColor Yellow
            & $iscc "/DAppVersion=$Version" "/DBuildType=full" "installer\Multi_Instance_CRD.iss"
            Invoke-CodeSign "./dist-installers/Multi_Instance_CRD-Setup-v$Version-DotNetIncluded.exe"

            Write-Host "Compiling Lightweight Installer..." -ForegroundColor Yellow
            & $iscc "/DAppVersion=$Version" "/DBuildType=light" "installer\Multi_Instance_CRD.iss"
            Invoke-CodeSign "./dist-installers/Multi_Instance_CRD-Setup-v$Version-Lightweight.exe"
            
            Write-Host "`nInstallers built successfully in ./dist-installers/:" -ForegroundColor Green
            Get-ChildItem "./dist-installers/*" | Format-Table Name, Length
        } else {
            Write-Warning "Inno Setup Compiler (ISCC.exe) not found. Skipping installer creation."
        }
    }

    Write-Host "`nAll packages published successfully!" -ForegroundColor Green
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

} finally {
    Pop-Location
}
