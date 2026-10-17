# Builds Elixar.exe with the C# compiler that comes with Windows (.NET Framework 4). No extra tools needed.
# .\build.ps1                -> out\Elixar.exe
# .\build.ps1 -SelfTest      -> also runs the safe self-test and fails if any check fails
param([string]$Out = "out", [switch]$SelfTest)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$outDir = Join-Path $root $Out
New-Item -ItemType Directory -Force $outDir | Out-Null

# Update settings built into the exe: the GitHub repository and the public key update manifests must be signed with.
$key = ""
$pub = Join-Path $root "update-key.pub.xml"
if (Test-Path $pub) { $key = (Get-Content -Raw $pub).Trim() }
$cfg = Join-Path $outDir "update-config.txt"
# Optional: the Google app for "Sign in with Google" (Settings > Email reports), so people using this build need no setup.
# It's a "Desktop app" client, which Google treats as public (its secret isn't a real secret). Repository secrets:
# ELIXAR_GOOGLE_ID and ELIXAR_GOOGLE_SECRET.
$gid = "$env:ELIXAR_GOOGLE_ID".Trim(); $gsec = "$env:ELIXAR_GOOGLE_SECRET".Trim()
if ($gid -and -not $gid.EndsWith(".apps.googleusercontent.com")) { throw "ELIXAR_GOOGLE_ID doesn't look like a Google client ID" }
[IO.File]::WriteAllText($cfg, "repo=$($env:GITHUB_REPOSITORY)`nkey=$key`ngoogle-id=$gid`ngoogle-secret=$gsec", (New-Object Text.UTF8Encoding $false))

$refs = "System.dll","System.Core.dll","System.Drawing.dll","System.Windows.Forms.dll","Microsoft.VisualBasic.dll","System.Xml.dll",
        "System.ServiceProcess.dll","System.Web.Extensions.dll","System.IO.Compression.dll","System.IO.Compression.FileSystem.dll","System.Security.dll" | ForEach-Object { "/r:$_" }
$src = Get-ChildItem (Join-Path $root "src\*.cs") | ForEach-Object { $_.FullName }
$exe = Join-Path $outDir "Elixar.exe"
& $csc /nologo /target:winexe /optimize+ "/out:$exe" "/win32icon:$root\app.ico" `
    "/resource:$root\app.ico,Elixar.app.ico" "/resource:$root\brand.png,Elixar.brand.png" `
    "/resource:$root\guide\UserGuide.html,Elixar.guide.html" "/resource:$cfg,Elixar.update-config.txt" $refs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $exe"

if ($SelfTest) {
    $p = Start-Process -FilePath $exe -ArgumentList "--selftest", "--quiet" -Wait -PassThru
    $report = Join-Path $outDir "selftest-report.txt"
    if (Test-Path $report) { Get-Content $report }
    if ($p.ExitCode -ne 0) { throw "Self-test: $($p.ExitCode) check(s) failed" }
    Write-Host "Self-test passed"
}
