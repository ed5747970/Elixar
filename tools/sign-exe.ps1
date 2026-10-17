# Code-signs Elixar.exe with your certificate, so Windows stops showing "Windows protected your PC".
# In GitHub: secrets CODESIGN_PFX (the .pfx file as base64) and CODESIGN_PASSWORD. Locally: -Pfx and -Password.
#   base64 of a .pfx:  [Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard
param([Parameter(Mandatory)][string]$Exe, [string]$Pfx, [string]$Password = $env:CODESIGN_PASSWORD)
$ErrorActionPreference = "Stop"
if (-not $Pfx) {
    $Pfx = Join-Path $env:TEMP ("cs-" + [Guid]::NewGuid().ToString("N") + ".pfx")
    [IO.File]::WriteAllBytes($Pfx, [Convert]::FromBase64String($env:CODESIGN_PFX))
    $temp = $true
}
try {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)" }
    & $signtool.FullName sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $Pfx /p $Password $Exe
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
    & $signtool.FullName verify /pa $Exe
} finally { if ($temp) { Remove-Item $Pfx -Force -ErrorAction SilentlyContinue } }
