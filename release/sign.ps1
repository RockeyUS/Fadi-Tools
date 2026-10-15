# Signs Fadi Tools with your code-signing certificate, so Windows shows your name instead of "Unknown publisher".
# Works with Certum SimplySign (cloud), a USB token, or any certificate in your Windows certificate store.
#
#   1. Install the signing tool once (part of the Windows SDK):
#        winget install --id Microsoft.WindowsSDK.10.0.26100
#   2. With SimplySign: open SimplySign Desktop and connect, so the certificate shows up in Windows.
#   3. Right-click this file > Run with PowerShell   (or: powershell -ExecutionPolicy Bypass -File sign.ps1)
#
# Optional: -Exe "C:\path\to\Fadi Tools.exe"   -Thumbprint <your certificate's thumbprint>
param(
    [string]$Exe = "$env:USERPROFILE\Downloads\Fadi Tools.exe",
    [string]$Thumbprint = "",
    [string]$Timestamp = "http://time.certum.pl"
)
$ErrorActionPreference = 'Stop'
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { Write-Host "signtool.exe wasn't found. Install it with: winget install --id Microsoft.WindowsSDK.10.0.26100" -ForegroundColor Yellow; Read-Host "Press Enter"; exit 1 }
if (-not (Test-Path $Exe)) { Write-Host "Can't find $Exe" -ForegroundColor Yellow; Read-Host "Press Enter"; exit 1 }
if ($Thumbprint) { $pick = @('/sha1', $Thumbprint) } else { $pick = @('/a') }   # /a = the best code-signing certificate available
& $signtool.FullName sign @pick /fd sha256 /tr $Timestamp /td sha256 /d "Fadi Tools" "$Exe"
if ($LASTEXITCODE -ne 0) { Write-Host "Signing failed (see above)." -ForegroundColor Red; Read-Host "Press Enter"; exit 1 }
& $signtool.FullName verify /pa "$Exe"
Write-Host "Signed: $Exe" -ForegroundColor Green
Read-Host "Press Enter"
