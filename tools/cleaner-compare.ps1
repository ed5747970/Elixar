# Cleaner comparison on a fresh Windows test PC (GitHub's Windows runner). Every tool gets its own fresh PC (one job each):
# the same "junk" is put in the usual places, plus personal files that must survive, then the tool cleans with its usual
# settings and the script measures what went, what stayed, and how long it took.
# The junk is 30 days old (so no tool skips it for being new). Results: one RESULT line (JSON) in the log and a table in
# the job summary.
param([Parameter(Mandatory = $true)][ValidateSet("none", "elixar", "elixar-ticked", "diskcleanup", "bleachbit", "ccleaner")][string]$Tool)

$ErrorActionPreference = "Continue"
$work = Join-Path $env:RUNNER_TEMP "cmp"
New-Item -ItemType Directory -Force $work | Out-Null
$old = (Get-Date).AddDays(-30)
$rng = [System.Random]::new(42)

function Age($p) { try { $i = Get-Item -LiteralPath $p -Force; $i.CreationTime = $old; $i.LastWriteTime = $old; $i.LastAccessTime = $old } catch { } }
function MakeFiles($dir, $count, $kb, $pattern) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $buf = New-Object byte[] ($kb * 1024)
    $names = @()
    for ($i = 0; $i -lt $count; $i++) {
        $rng.NextBytes($buf)
        $f = Join-Path $dir ($pattern -f $i)
        [IO.File]::WriteAllBytes($f, $buf); Age $f; $names += $f
    }
    Age $dir
    return $names
}

# ---------- the junk (each category about 50 MB unless noted) ----------
$cats = [ordered]@{}
$cats["Your temp folder"] = MakeFiles (Join-Path $env:TEMP "cmp-junk") 200 256 "tmp{0:d4}.tmp"
$cats["Windows temp folder"] = MakeFiles (Join-Path $env:windir "Temp\cmp-junk") 200 256 "tmp{0:d4}.tmp"
$cats["Chrome cache"] = MakeFiles (Join-Path $env:LOCALAPPDATA "Google\Chrome\User Data\Default\Cache\Cache_Data") 200 256 "f_{0:x6}"
$cats["Edge cache"] = MakeFiles (Join-Path $env:LOCALAPPDATA "Microsoft\Edge\User Data\Default\Cache\Cache_Data") 200 256 "f_{0:x6}"
$cats["Thumbnail cache (32 MB)"] = MakeFiles (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Explorer") 4 8192 "thumbcache_cmp{0}.db"
$wer = @()
for ($r = 0; $r -lt 10; $r++) { $wer += MakeFiles (Join-Path $env:ProgramData ("Microsoft\Windows\WER\ReportArchive\AppCrash_cmptest.exe_{0:d4}" -f $r)) 10 512 "part{0}.dmp" }
$cats["Windows error reports"] = $wer
$cats["Crash dumps"] = MakeFiles (Join-Path $env:LOCALAPPDATA "CrashDumps") 10 5120 "cmptest.exe.{0}.dmp"

# Recycle Bin: 20 files of 2 MB, deleted the normal way
$rb = MakeFiles (Join-Path $work "to-bin") 20 2048 "deleted{0}.bin"
Add-Type -AssemblyName Microsoft.VisualBasic
foreach ($f in $rb) { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($f, 'OnlyErrorDialogs', 'SendToRecycleBin') }
function BinBytes { try { $s = 0; foreach ($i in (New-Object -ComObject Shell.Application).NameSpace(10).Items()) { $s += [int64]$i.Size }; return $s } catch { return -1 } }

# ---------- personal files that must never go (also 30 days old) ----------
$keep = @()
$keep += MakeFiles (Join-Path $env:USERPROFILE "Documents") 1 300 "Taxes-2025{0}.xlsx"
$keep += MakeFiles (Join-Path $env:USERPROFILE "Documents\Old project") 1 40 "notes{0}.txt"
$keep += MakeFiles (Join-Path $env:USERPROFILE "Desktop") 1 2048 "Family photo{0}.jpg"
$keep += MakeFiles (Join-Path $env:USERPROFILE "Downloads") 1 900 "Contract{0}.pdf"
$keep += MakeFiles (Join-Path $env:APPDATA "MyApp") 1 4 "settings{0}.json"

function Left($files) { $s = 0; foreach ($f in $files) { if (Test-Path -LiteralPath $f) { $s += (Get-Item -LiteralPath $f -Force).Length } }; return $s }
$before = [ordered]@{}; foreach ($k in $cats.Keys) { $before[$k] = Left $cats[$k] }
$before["Recycle Bin (40 MB)"] = BinBytes
Write-Host ("Junk put in place: " + [math]::Round((($before.Values | Measure-Object -Sum).Sum) / 1MB) + " MB")

# ---------- get the tool ----------
function Get-File($urls, $out) {
    foreach ($u in $urls) { try { Invoke-WebRequest -Uri $u -OutFile $out -UseBasicParsing -TimeoutSec 300; if ((Get-Item $out).Length -gt 100000) { Write-Host "Downloaded $u"; return $true } } catch { Write-Host "Not at $u" } }
    return $false
}
$problem = $null; $version = ""; $run = $null
switch ($Tool) {
    "elixar" {
        if ($env:ELIXAR_FROM_SOURCE -eq "1" -and (Test-Path ".\build.ps1")) {
            & .\build.ps1 | Out-Null
            $exe = Get-Item ".\out\Elixar.exe"; $version = $exe.VersionInfo.FileVersion + " (this branch)"
            $run = { $p = Start-Process -FilePath $exe.FullName -ArgumentList "--clean" -Wait -PassThru; "exit " + $p.ExitCode }
        } elseif (Get-File @("https://github.com/ed5747970/Elixar/releases/latest/download/Elixar.zip") "$work\elixar.zip") {
            Expand-Archive "$work\elixar.zip" "$work\elixar" -Force
            $exe = Get-ChildItem "$work\elixar" -Recurse -Filter Elixar.exe | Select-Object -First 1
            $version = (Get-Item $exe.FullName).VersionInfo.FileVersion
            $run = { $p = Start-Process -FilePath $exe.FullName -ArgumentList "--clean" -Wait -PassThru; "exit " + $p.ExitCode }
        } else { $problem = "download failed" }
    }
    "elixar-ticked" {
        # the automatic Safe clean, plus what you'd tick by hand for these places: browser caches and the Recycle Bin
        $have = $false
        if ($env:ELIXAR_FROM_SOURCE -eq "1" -and (Test-Path ".\build.ps1")) { & .\build.ps1 | Out-Null; $exe = Get-Item ".\out\Elixar.exe"; $version = $exe.VersionInfo.FileVersion + " (this branch)"; $have = $true }
        elseif (Get-File @("https://github.com/ed5747970/Elixar/releases/latest/download/Elixar.zip") "$work\elixar.zip") {
            Expand-Archive "$work\elixar.zip" "$work\elixar" -Force
            $exe = Get-ChildItem "$work\elixar" -Recurse -Filter Elixar.exe | Select-Object -First 1
            $version = (Get-Item $exe.FullName).VersionInfo.FileVersion; $have = $true
        }
        if ($have) {
            $run = {
                $o = ""
                foreach ($a in @(@("--clean"), @("--clean-item", '"Chrome cache"'), @("--clean-item", '"Edge cache"'), @("--clean-item", '"Recycle Bin"'))) {
                    $p = Start-Process -FilePath $exe.FullName -ArgumentList $a -Wait -PassThru; $o += ($a -join " ") + ": exit " + $p.ExitCode + "; "
                }
                $o
            }
        } else { $problem = "download failed" }
    }
    "diskcleanup" {
        # Windows' own Disk Cleanup with the boxes it ticks by itself, plus the ones for the places above
        $cm = Join-Path $env:windir "System32\cleanmgr.exe"
        if (Test-Path $cm) {
            $version = "Windows " + [Environment]::OSVersion.Version.ToString()
            $vc = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches"
            foreach ($h in @("Temporary Files", "Recycle Bin", "Thumbnail Cache", "Windows Error Reporting Files", "System error memory dump files", "Temporary Internet Files", "Downloaded Program Files", "Delivery Optimization Files")) {
                if (Test-Path "$vc\$h") { New-ItemProperty -Path "$vc\$h" -Name StateFlags0099 -Value 2 -PropertyType DWord -Force | Out-Null }
            }
            $run = { $p = Start-Process -FilePath $cm -ArgumentList "/sagerun:99" -PassThru; if (-not $p.WaitForExit(900000)) { try { $p.Kill() } catch { }; "stopped after 15 minutes (still working)" } else { "exit " + $p.ExitCode } }
        } else { $problem = "Disk Cleanup isn't on this Windows" }
    }
    "bleachbit" {
        if (Get-File @("https://download.bleachbit.org/BleachBit-6.0.4-portable.zip", "https://download.bleachbit.org/BleachBit-6.0.2-portable.zip", "https://download.bleachbit.org/BleachBit-5.0.2-portable.zip") "$work\bb.zip") {
            Expand-Archive "$work\bb.zip" "$work\bb" -Force
            $bb = Get-ChildItem "$work\bb" -Recurse -Filter bleachbit_console.exe | Select-Object -First 1
            $version = (Get-Item $bb.FullName).VersionInfo.ProductVersion
            # BleachBit ticks nothing by itself: the options a user would tick for these places
            $all = & $bb.FullName --list 2>$null
            $want = @("system.tmp", "system.recycle_bin", "system.logs", "system.memory_dump", "windows_explorer.thumbnails", "google_chrome.cache", "microsoft_edge.cache")
            $use = @($want | Where-Object { $all -contains $_ })
            Write-Host ("BleachBit options used: " + ($use -join ", "))
            $run = { & $bb.FullName --clean @use 2>&1 | Select-Object -Last 3 | Out-String }
        } else { $problem = "download failed" }
    }
    "ccleaner" {
        if (Get-File @("https://download.ccleaner.com/portable/ccsetup641.zip", "https://download.ccleaner.com/portable/ccsetup640.zip", "https://download.ccleaner.com/portable/ccsetup639.zip") "$work\cc.zip") {
            Expand-Archive "$work\cc.zip" "$work\cc" -Force
            $cc = Get-ChildItem "$work\cc" -Recurse -Filter CCleaner64.exe | Select-Object -First 1
            if (-not $cc) { $cc = Get-ChildItem "$work\cc" -Recurse -Filter CCleaner.exe | Select-Object -First 1 }
            $version = (Get-Item $cc.FullName).VersionInfo.ProductVersion
            # /AUTO: cleans with its usual settings, no window
            $run = { $p = Start-Process -FilePath $cc.FullName -ArgumentList "/AUTO" -PassThru; if (-not $p.WaitForExit(600000)) { $p.Kill(); "stopped after 10 minutes" } else { "exit " + $p.ExitCode } }
        } else { $problem = "download failed" }
    }
    "none" { $version = "-"; $run = { "nothing run" } }
}

# ---------- clean and measure ----------
$freeBefore = (Get-PSDrive C).Free
$secs = 0; $said = ""
if ($run -and -not $problem) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    try { $said = (& $run) | Out-String } catch { $problem = $_.Exception.Message }
    $t.Stop(); $secs = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Start-Sleep -Seconds 5
}
$freeAfter = (Get-PSDrive C).Free

$rows = @(); $junkBefore = 0; $junkGone = 0
foreach ($k in $cats.Keys) {
    $b = $before[$k]; $a = Left $cats[$k]
    $rows += [ordered]@{ name = $k; before = $b; gone = $b - $a }
    $junkBefore += $b; $junkGone += ($b - $a)
}
$bb0 = $before["Recycle Bin (40 MB)"]; $bb1 = BinBytes
$rows += [ordered]@{ name = "Recycle Bin (40 MB)"; before = $bb0; gone = [math]::Max(0, $bb0 - $bb1) }
$junkBefore += $bb0; $junkGone += [math]::Max(0, $bb0 - $bb1)
$lost = @($keep | Where-Object { -not (Test-Path -LiteralPath $_) } | ForEach-Object { Split-Path $_ -Leaf })

$result = [ordered]@{
    tool = $Tool; version = $version; problem = $problem; seconds = $secs; said = $said.Trim()
    junkMB = [math]::Round($junkBefore / 1MB, 1); goneMB = [math]::Round($junkGone / 1MB, 1)
    driveFreedMB = [math]::Round(($freeAfter - $freeBefore) / 1MB, 1)
    personalLost = $lost; categories = $rows
}
Write-Host ("RESULT " + ($result | ConvertTo-Json -Compress -Depth 5))

$md = "### $Tool $version`n`n"
if ($problem) { $md += "**Couldn't run:** $problem`n`n" }
$md += "| Place | Put there | Cleaned |`n|---|---:|---:|`n"
foreach ($r in $rows) { $md += "| " + $r.name + " | " + [math]::Round($r.before / 1MB, 1) + " MB | " + [math]::Round($r.gone / 1MB, 1) + " MB |`n" }
$md += "`nTime: $secs s · Junk cleaned: " + $result.goneMB + " of " + $result.junkMB + " MB · C: freed in all: " + $result.driveFreedMB + " MB · Personal files lost: " + ($(if ($lost.Count) { $lost -join ", " } else { "none" })) + "`n"
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md }
exit 0
