# Run ONCE on your own PC (not in the cloud):  powershell -ExecutionPolicy Bypass -File tools\new-signing-key.ps1
# Creates the key that proves an update really comes from you.
#  - update-key.pub.xml (public half) goes into the repository; Elixar builds it in and checks every update with it.
#  - The private half is saved to your Desktop. Put it in GitHub as the secret ELIXAR_SIGNING_KEY, keep a backup
#    somewhere safe (a password manager), and never commit it.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 3072
$rsa.PersistKeyInCsp = $false
[IO.File]::WriteAllText((Join-Path $repo "update-key.pub.xml"), $rsa.ToXmlString($false))
$secret = Join-Path ([Environment]::GetFolderPath("Desktop")) "ELIXAR-SIGNING-KEY-KEEP-SECRET.xml"
[IO.File]::WriteAllText($secret, $rsa.ToXmlString($true))
Write-Host "Public key:  $repo\update-key.pub.xml   (commit this file)"
Write-Host "Private key: $secret"
Write-Host "Next: GitHub > your repo > Settings > Secrets and variables > Actions > New repository secret"
Write-Host "      Name: ELIXAR_SIGNING_KEY   Value: the whole content of that file. Then delete it from the Desktop (keep a backup)."
