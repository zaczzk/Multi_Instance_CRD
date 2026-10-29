<#
.SYNOPSIS
    Installs the CRDLightweightARM Developer certificate into the Current User's
    Trusted Root Certification Authorities and Trusted Publishers store.
.DESCRIPTION
    Allows local builds of CRDLightweightARM.exe to run without Smart App Control
    or Windows Defender untrusted-publisher warnings.
#>

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -like "*CRDLightweightARM*" } | Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating a new 4096-bit SHA-256 Code Signing Certificate..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type CodeSigning `
        -Subject "CN=CRDLightweightARM Developer, O=zaczzk" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(5)
    Write-Host "Created certificate: $($cert.Thumbprint)" -ForegroundColor Green
}

# 1. Add to TrustedPublisher (No prompt required)
$pubStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
    [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)
$pubStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$pubStore.Add($cert)
$pubStore.Close()
Write-Host "Installed into CurrentUser\TrustedPublisher store." -ForegroundColor Green

# 2. Add to Trusted Root Certification Authorities (Requires clicking 'Yes' on Windows security prompt)
$tempCer = "$env:TEMP\crd_dev_$($cert.Thumbprint).cer"
[System.IO.File]::WriteAllBytes($tempCer, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

Write-Host "Prompting Windows to install certificate into Trusted Root store (click 'Yes' on the dialog)..." -ForegroundColor Yellow
Start-Process certutil.exe -ArgumentList "-user -addstore root `"$tempCer`"" -Wait -NoNewWindow
Remove-Item $tempCer -ErrorAction SilentlyContinue

Write-Host "Verification:" -ForegroundColor Cyan
$rootCert = Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if ($rootCert) {
    Write-Host "Certificate is now fully trusted on this machine!" -ForegroundColor Green
} else {
    Write-Host "Notice: Certificate was not added to Root store. Re-run and click 'Yes' to trust." -ForegroundColor Yellow
}
