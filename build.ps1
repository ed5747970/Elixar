# Builds Elixar.exe with the C# compiler that comes with Windows (.NET Framework 4). No extra tools needed.
# .\build.ps1                -> out\Elixar.exe (Elixar for Private) and out\business\ (Elixar for Business, CureAgent.exe, CureServer.exe)
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
$msid = "$env:ELIXAR_MS_ID".Trim()
if ($msid -and -not ($msid -as [guid])) { throw "ELIXAR_MS_ID must be the Application (client) ID" }
[IO.File]::WriteAllText($cfg, "repo=$($env:GITHUB_REPOSITORY)`nkey=$key`ngoogle-id=$gid`ngoogle-secret=$gsec`nms-id=$msid`nsponsor=$($env:ELIXAR_SPONSOR)", (New-Object Text.UTF8Encoding $false))

$refs = "System.dll","System.Core.dll","System.Drawing.dll","System.Windows.Forms.dll","Microsoft.VisualBasic.dll","System.Xml.dll",
        "System.ServiceProcess.dll","System.Web.Extensions.dll","System.IO.Compression.dll","System.IO.Compression.FileSystem.dll","System.Security.dll" | ForEach-Object { "/r:$_" }
$src = Get-ChildItem (Join-Path $root "src\*.cs") | ForEach-Object { $_.FullName }
$common = @("/nologo", "/optimize+", "/win32icon:$root\app.ico", "/win32manifest:$root\app.manifest",
            "/resource:$root\app.ico,Elixar.app.ico", "/resource:$root\brand.png,Elixar.brand.png",
            "/resource:$root\guide\UserGuide.html,Elixar.guide.html", "/resource:$cfg,Elixar.update-config.txt") + $refs

# Elixar for Private -> out\Elixar.exe
$exe = Join-Path $outDir "Elixar.exe"
& $csc /target:winexe "/out:$exe" $common $src
if ($LASTEXITCODE -ne 0) { throw "Build failed (Private)" }
Write-Host "Built $exe (Elixar for Private)"

# Elixar for Business -> out\business\Elixar.exe, CureAgent.exe, CureServer.exe
$bizDir = Join-Path $outDir "business"
New-Item -ItemType Directory -Force $bizDir | Out-Null
$biz = Join-Path $bizDir "Elixar.exe"
& $csc /target:winexe "/out:$biz" /define:BUSINESS $common `
    "/resource:$root\cure\console.html,Elixar.cure.console.html" "/resource:$root\cure\console.js,Elixar.cure.console.js" "/resource:$root\cure\console.css,Elixar.cure.console.css" $src
if ($LASTEXITCODE -ne 0) { throw "Build failed (Business)" }
foreach ($t in "Agent", "Server") {
    & $csc /nologo /optimize+ /target:exe "/out:$bizDir\Cure$t.exe" "/win32icon:$root\app.ico" "/win32manifest:$root\app.manifest" "/r:$biz" $refs "$root\cure\${t}Main.cs"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (Cure $t)" }
}
Write-Host "Built $biz, CureAgent.exe and CureServer.exe (Elixar for Business)"

if ($SelfTest) {
    $failed = 0
    foreach ($e in @($exe, $biz)) {
        $p = Start-Process -FilePath $e -ArgumentList "--selftest", "--quiet" -Wait -PassThru
        $report = Join-Path (Split-Path $e) "selftest-report.txt"
        Write-Host "===== Self-test: $e"
        if (Test-Path $report) { Get-Content $report }
        if ($p.ExitCode -ne 0) { $failed += $p.ExitCode }
    }
    if ($failed -ne 0) { throw "Self-test: $failed check(s) failed" }
    Write-Host "Self-test passed (Private and Business)"
}
