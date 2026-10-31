# Records every network connection Elixar's programs make, using Windows' own firewall audit (the Windows Filtering
# Platform writes event 5156 for each connection it allows, with the program that made it). Used by the CI runs on a
# throwaway Windows PC (it always sets the exit code: 0 = fine, 1 = an unexpected connection): start it, run Elixar / Cure Agent / Cure Server, then check. The check fails when one of them
# connected anywhere other than this PC itself, unless that address is passed with -Allow.
#   netwatch.ps1 start
#   netwatch.ps1 check [-Allow host-or-ip, ...]
param(
  [Parameter(Mandatory = $true)][ValidateSet('start', 'check')][string]$Mode,
  [string[]]$Allow = @()
)
$ErrorActionPreference = 'Stop'
$stamp = Join-Path $env:RUNNER_TEMP 'netwatch-start.txt'
if (-not $env:RUNNER_TEMP) { $stamp = Join-Path $env:TEMP 'netwatch-start.txt' }
$programs = 'elixar.exe', 'cureagent.exe', 'cureserver.exe'

if ($Mode -eq 'start') {
  # "Filtering Platform Connection", by its GUID (the name is translated on non-English Windows)
  auditpol /set /subcategory:"{0CCE9226-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable | Out-Null
  if ($LASTEXITCODE) { throw "Could not turn on the connection audit" }
  wevtutil sl Security /ms:536870912   # 512 MB: every connection on the PC is written, keep them all for this run
  (Get-Date).ToString('o') | Set-Content $stamp
  Write-Host "Network watch: on (every connection made from now on is recorded)"
  exit 0
}

$since = [DateTime]::Parse((Get-Content $stamp -Raw).Trim(), $null, 'RoundtripKind')
$events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5156; StartTime = $since } -ErrorAction SilentlyContinue)
if ($events.Count -eq 0) { throw "Network watch: Windows recorded no connections at all since $since, so the audit isn't working (the check would prove nothing)" }

# this PC's own addresses: connections to them never leave the PC
$own = @('127.0.0.1', '::1', '0.0.0.0', '::') + @(Get-NetIPAddress -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress -replace '%.*$', '' })
$allowed = @()
foreach ($a in $Allow) { try { $allowed += [Net.Dns]::GetHostAddresses($a) | ForEach-Object { $_.IPAddressToString } } catch { $allowed += $a } }

$seen = @{}
foreach ($e in $events) {
  $x = [xml]$e.ToXml(); $d = @{}
  foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
  $app = Split-Path -Leaf ("" + $d['Application'])
  if ($programs -notcontains $app.ToLowerInvariant()) { continue }
  if ($d['Direction'] -ne '%%14593') { continue }   # outbound only (what the server receives is checked by its own tests)
  $dest = ("" + $d['DestAddress']) -replace '^::ffff:', ''
  $where = if ($own -contains $dest -or $dest.StartsWith('127.')) { 'this PC' } elseif ($allowed -contains $dest) { 'allowed' } else { 'NOT EXPECTED' }
  $key = "$app -> ${dest}:$($d['DestPort']) ($where)"
  $seen[$key] = 1 + [int]$seen[$key]
}

Write-Host "Network watch: $($events.Count) connections on this PC since $since; Elixar's programs made these:"
if ($seen.Count -eq 0) { Write-Host "  (none)" }
$seen.Keys | Sort-Object | ForEach-Object { Write-Host "  $_  x$($seen[$_])" }
$bad = @($seen.Keys | Where-Object { $_ -match 'NOT EXPECTED' })
if ($bad.Count) {
  Write-Host ("::error title=Unexpected connection::" + ($bad -join '%0A'))
  exit 1
}
$list = if ($seen.Count) { ($seen.Keys | Sort-Object | ForEach-Object { "$_ x$($seen[$_])" }) -join '%0A' } else { 'no connections at all' }
Write-Host "::notice title=Network watch::Elixar's programs connected only to this PC$(if ($Allow) { ' and ' + ($Allow -join ', ') }) ($($events.Count) connections recorded on the PC). Theirs:%0A$list"
exit 0
