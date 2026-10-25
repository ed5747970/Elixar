# Elixar — Backdoor & Compromise Assessment

| | |
|---|---|
| Application | Elixar (Windows disk-space and maintenance tool, WinForms / .NET Framework 4.x, C# 5) |
| Version reviewed | **1.2.55** (commit `1b22b5a`, re-audit). First review: 1.2.52 (`a958c9f`), fixes in 1.2.53 (`564bf83`), 1.2.54 and 1.2.55 |
| Repository | `github.com/ed5747970/Elixar` (56 commits reviewed for structure; current tree reviewed in full by search, sensitive areas read line by line) |
| Scope | All 94 C# source files (~34,000 lines), `build.ps1`, `.github/workflows/*.yml`, `package/Elixar.wxs`, `tools/` (Linux helper and signing scripts) |
| Method | Static review: inventory of every network call, listener, process launch, credential store, scheduled task, registry write and update path; plus the full self-test (252 checks) on Linux and on a real Windows Server 2025 runner (GitHub Actions) |
| Re-audit (1.2.55) | New since the first review: company-PC detection (`Policy.cs` `DomainJoin`), email/phone/Sysprep locks, Group Policy template (`PolicyAdmx.cs`, `Cli.cs --admx`), Windows Event Log writer (`WinEvents.cs`), LICENSE, SECURITY.md, Dependabot. No application code was changed during this re-audit |
| Not done | No dynamic network capture on a production-like Windows PC, no fuzzing, no third-party pen test, no review of the GitHub account's own security settings (2FA, branch protection) |
| Date | 2026-10-25 |

> **Conclusion (section J): NO BACKDOOR INDICATORS IDENTIFIED IN REVIEWED SCOPE.**
> This does not prove that no backdoor can exist; it means none was found in the code, build and update path reviewed here.

---

## Context: what Elixar is, for this assessment

Elixar is a **desktop application**, not a server. It has no web front end, no API, no database and no inbound network service. On Windows it **runs with administrator rights** (it asks UAC at start) because it cleans system folders, edits system settings and creates scheduled tasks. That is the main trust fact: **anything Elixar is tricked into doing, it does as an administrator of that PC.**

Integrations that **exist**:

| Area | Exists? | Notes |
|---|---|---|
| Local PC (files, registry, Task Scheduler, Windows tools) | Yes — core function | As administrator |
| Microsoft 365 / Entra ID | Optional | Only if a user connects an Outlook/Microsoft account for email reports (Microsoft Graph: `Mail.Send`; `Mail.ReadWrite` only when "Email cleaning" is turned on) |
| Google (Gmail) | Optional | Only if a user connects Gmail (`gmail.send`; `gmail.modify` only for Email cleaning) |
| Other IMAP/SMTP mail servers | Optional | User-entered server and app password |
| SMB file shares | Optional (IT) | Only `--report <\\server\share>` / Fleet report writes and reads small JSON files |
| Active Directory / Entra ID | **Read-only, local** (1.2.54+) | Elixar asks Windows only *whether* the PC is joined (`NetGetJoinInformation`, `NetGetAadJoinInformation` — local calls, `Policy.cs` `DomainJoin`) to lock Phone, Sysprep and Email. It never contacts a domain controller or Entra, never authenticates, never queries LDAP |
| SQL, network devices, telephony, internal web apps, cloud consoles | **No** | No code paths found. Elixar never opens SQL/SSH/RDP/SNMP/LDAP connections |
| Windows Event Log | Yes (1.2.55) | Writes its own changes to a local "Elixar" log (admins and Event Log Readers only) for SIEM/WEF collection; nothing is sent anywhere by Elixar |
| Android phones (USB) | Optional | Via Google's `adb` (downloaded on request, signature-checked) |
| winget (app updates) | Optional | Runs Windows' own package manager |

Nothing in the code suggests planned integrations beyond these.

---

## A. Intentional backdoor findings

*Re-audit 1.2.55:* the new code (company detection, ADMX generator, Event Log writer, email/phone/Sysprep locks) adds no listener, no network destination, no account, no remote command path and no code loading. `--admx` only writes three XML files to the folder it is given and needs no administrator rights.

**None found.** Each item from the requested checklist was searched for explicitly:

| Looked for | Result | Evidence |
|---|---|---|
| Hidden admin accounts, master passwords, auth bypass | None | The only authentication is the optional local PIN (`PinLock.cs`), salted and stretched (200,000 rounds), with try limits. No master PIN, no reset code, no hard-coded credential. The "unlock ticket" (`PinLock.cs:157`) only lets Elixar restart itself within 120 s and can only be written by administrators (`C:\ProgramData\Elixar` is admin-write). |
| Undocumented privileged endpoints, listeners | None besides the documented ones | Inbound sockets: (1) OAuth sign-in loopback listener `Mail.cs:225`, `MailProviders.cs:110` — 127.0.0.1/::1 only, random port, open only during a sign-in (max 5 min), checks a random `state` and uses PKCE; (2) `FakeImap` `FakeMail.cs:31` — loopback, created only by the self-test. No other `TcpListener`/`HttpListener`/named pipe. |
| Remote access, reverse shell, tunnels, proxying | None | No code connects out and waits for commands. No SSH/RDP/VNC/WebSocket code. |
| Command execution from outside input | None from the network | Every process start is a fixed Windows tool resolved to System32 (`Win.SystemExe`), with fixed or validated arguments; see section B for the user-input cases. |
| Obfuscated / encrypted / dynamically loaded code | None | No `Assembly.Load`, `Reflection.Emit`, `CSharpCodeProvider`, no base64 blobs executed. P/Invoke is limited to file, security and shell APIs (no `VirtualAlloc`, `WriteProcessMemory`, `CreateRemoteThread`). PowerShell is run with `-EncodedCommand`, but every script is a fixed string in the source (`Core.cs:320`, `StoreApps.cs:174`) — see B-6. |
| Downloaded and executed code | Two documented, verified cases | (1) Elixar's own update — RSA-3072 signed manifest + SHA-256 of zip and exe, user must click Yes (`Updates.cs:200-220`, `MainForm.cs:405`). (2) Google's Android `adb` — kept only if Authenticode-signed by Google (`Phone.cs:51`). |
| Secret data collection / telemetry | None | No analytics, crash upload or usage tracking. The only automatic outbound call is the weekly update check (section C). |
| Persistence beyond the documented schedules | None | Scheduled tasks are documented, all start the admin-protected copy in Program Files, all are removed on uninstall (section F). |
| Behaviour different from documentation | None found | The README/guide describe every outbound connection and task found in code. |
| Test hooks in production binary | Present, not exploitable | `--fake-adb` runs only when `ELIXAR_SELFTEST=1` (`Program.cs:48`) and only simulates a phone; `--selftest` runs safe tests in a temp folder. Neither opens network access. |

---

## B. Unintentional backdoor risks (vulnerabilities and risky designs)

Severity is for a **corporate deployment** where Elixar runs as administrator on employee PCs.

| # | Finding | Severity | Status |
|---|---|---|---|
| B-1 | **Update trust = Elisar's GitHub account + signing key.** Anyone who controls the repository (or the `ELIXAR_SIGNING_KEY` secret) can publish a *validly signed* malicious release. Clients only install it after a user clicks "Yes", but they would then run it as administrator. | **High** (supply chain) | Mitigated in 1.2.53: release job runs in a `release` **environment** that can require owner approval before the key is used; Actions pinned to commit SHAs; test build has read-only token. **Remaining:** owner must enable 2FA, branch protection and the environment approval (section I). For a company: block `updates.check` and deploy only builds you have verified. |
| B-2 | **Logs readable by every local user.** `changes.jsonl`/`changes.txt`, `activity.txt`, `cleaner-log.txt` in `C:\ProgramData\Elixar` (users-read, `Security.cs:680`) list file paths, registry values and what was deleted — information disclosure between users on a shared PC. | Medium | **Fixed**: logs moved to `C:\ProgramData\Elixar\logs`, administrators only, old logs moved automatically (`Paths.LogIn`, `Security.cs:690`). |
| B-3 | **No way for IT to stop outbound update checks.** Weekly call to `api.github.com` with `User-Agent: Elixar/<version>`. | Low–Medium | **Fixed**: policy rule `updates.check` (Policy manager, `.reg` export, `HKLM\SOFTWARE\Policies\Elixar`); the Golden image preset blocks it. |
| B-4 | **winget could install from any configured source.** `upgrade --id X` without `--source` lets a third-party source added on the PC answer for a package id, then install as administrator. | Medium | **Fixed**: only `winget` and `msstore` sources; others are refused (`AppUpdates.cs:70-80`). |
| B-5 | **Elevated `adb` server outlives Elixar.** Using the Phone tab starts `adb` (127.0.0.1:5037) with Elixar's admin token; it kept running after Elixar closed. Local-only, but an unneeded elevated listener. | Low | **Fixed**: stopped on exit when Elixar started it (`Phone.cs:26`, `DashboardHome.cs:821`). |
| B-6 | **EDR-sensitive but legitimate behaviour.** `powershell -ExecutionPolicy Bypass -EncodedCommand`; Golden image tools can clear all event logs (`wevtutil cl`, `GoldenImage.cs:661`), delete shadow copies (`vssadmin`, `GoldenImage.cs:678`), delete Wi-Fi profiles and saved credentials. These match ransomware/anti-forensics patterns and will (correctly) raise EDR alerts. All are behind confirmations and the `sysprep` policy rule. | Medium (operational) | Documented. **Company action:** set `sysprep = block` by organization policy on all non-imaging PCs. |
| B-7 | **Email reports can send PC data to a personal mailbox.** A user can connect a personal Gmail/Outlook on a work PC; reports contain the computer name, disk sizes, item names (no file names, no file contents). Mail goes only to the signed-in address (`Mail.cs:380`, `MailProviders.cs:248`, `MailProviders.cs:447`). | Low–Medium (policy) | Existing control: policy rule `email = block`. **Company action:** set it by organization policy unless wanted. |
| B-8 | **Microsoft sign-out doesn't revoke the token at Microsoft.** The refresh token is deleted locally (`MailProviders.cs:96`) but stays valid at Microsoft until it expires (up to 90 days of inactivity) — Microsoft offers no app-level revoke for public clients. | Low | **Mitigated**: disconnect now tells the user how to remove the consent (myapps.microsoft.com / account.live.com). **Company action:** don't allow user consent to third-party apps in Entra ID, or pre-approve with admin consent and Conditional Access. |
| B-9 | **Uninstall left Undo backups (copies of deleted files) and logs forever**, and the uninstall text wrongly said they would expire. | Medium (data remnant) | **Fixed**: uninstall asks; IT uses `--uninstall --quiet --purge` (`Setup.cs:120`). |
| B-10 | **Fleet report files are not authenticated.** Any PC (or user) with write access to the share can write or overwrite any `<name>.json`. Parsing is bounded and safe (64 KB cap, clipped strings, CSV formula guard — `Fleet.cs:72-140`), so the risk is false data, not code execution. | Low | Documented. **Company action:** share ACL "create files / write data" for computers, read only for IT; treat reports as advisory. |
| B-11 | **Uninstall strings of installed apps are executed** (`Apps.cs:88`) — inherent to an uninstaller. Mitigations present: per-user (HKCU) uninstallers run **without** admin rights (`Apps.cs:383`), unsigned uninstallers outside Program Files trigger a warning (`Apps.cs:117`), `cmd /d` blocks AutoRun, working folder is System32. | Low | Existing controls adequate. |
| B-13 | **GitHub Actions keep the job token in the checked-out repository.** `actions/checkout` is used without `persist-credentials: false` (`.github/workflows/ci.yml`, `release.yml`), so later steps — the build, the self-test and, in the release job, the freshly built `Elixar.exe --admx` — can read the token from `.git/config`. In the release job that token has `contents: write` (it could push or publish). Exploitable only if malicious code is already in the build. | **Medium** (supply chain) | **Fixed in 1.2.56** (`persist-credentials: false` in both workflows) |
| B-14 | **Company-PC lock fails open.** If Windows' join check fails (an API error), Elixar treats the PC as a home PC and Phone/Sysprep/Email stay on. | Low | By design (no lock-out of home users). **Company action:** set `phone`, `sysprep`, `email` explicitly in `HKLM\SOFTWARE\Policies\Elixar` (or the ADMX) instead of relying on detection |
| B-15 | **Event Log shows file paths and settings to the Event Log Readers group** (and to whoever receives the forwarded events). | Low | By design for SIEM; the log is not readable by ordinary users. Policy rule `eventlog = off` turns it off |
| B-12 | **Golden image scanner reads credential *locations* across all profiles** (SSH keys, `.aws`, `.azure`, Docker/npm auth markers, Credential Manager target names, Chromium login counts). It never decrypts or transmits secrets (`GoldenImage.cs:270-480`), but the access pattern looks like credential harvesting to EDR. | Low (operational) | Documented; covered by the `sysprep` rule. |

Hardening already present (verified): DLL search order locked to System32 (`Security.cs:891`); planted `.config/.local/.manifest` next to the exe refused (`EnvGuard.cs:36`); risky .NET/PATH environment variables dropped when elevated (`EnvGuard.cs:101`); PowerShell module path forced to `$PSHOME` (`Core.cs:317`); all deletes are link-safe and refuse Windows/user root folders (`SafeDelete`, `Protect.IsProtected`); files from Save dialogs written without following links (`SafeFile.cs`); task XML parsed without DTDs (no XXE); IMAP replies size-limited; update downloads size-limited (`Updates.cs:141`).

---

## C. Outbound communication inventory

| # | Origin (file) | Destination | When | Data sent | Auth | Needed? | Can IT disable? |
|---|---|---|---|---|---|---|---|
| 1 | `Updates.cs:186` | `api.github.com/repos/ed5747970/Elixar/releases/latest` | Once a week at start, and on "Check for updates" | IP address, `User-Agent: Elixar/<version>` | None | Optional | **Yes (1.2.53)** — `updates.check = block` |
| 2 | `Updates.cs:147` | `github.com` / `*.githubusercontent.com` (release files) | Only after the user clicks Yes on an update | IP, UA | None | Optional | Yes (same rule) |
| 3 | `Phone.cs:32,103` | `dl.google.com` (Android platform-tools) | Only when the user clicks "Get Android tools" | IP | None | Optional | Yes — `phone = block` |
| 4 | `Mail.cs`, `GmailClean.cs` | `accounts.google.com`, `oauth2.googleapis.com`, `gmail.googleapis.com`, `www.googleapis.com` | Only if a Google account is connected (sign-in, daily report task, Email cleaning) | Report text (computer name, disk numbers, item names); Email cleaning: reads message headers, moves to Trash after confirmation | OAuth 2 + PKCE, refresh token DPAPI-encrypted in an admin-only folder | Optional | Yes — `email = block` |
| 5 | `MailProviders.cs:60-62` | `login.microsoftonline.com/common`, `graph.microsoft.com` | Only if a Microsoft account is connected | Same as 4 | OAuth 2 + PKCE; `Mail.Send` (+`Mail.ReadWrite` for cleaning) | Optional | Yes — `email = block`; Entra consent policy |
| 6 | `MailProviders.cs:466,575` | User-entered IMAP (993/TLS) and SMTP (587/STARTTLS) server | Only if "Other email" is configured | Same as 4; app password (DPAPI) | Password | Optional | Yes — `email = block` |
| 7 | `AppUpdates.cs` → `winget.exe` | winget / Microsoft Store sources (Microsoft CDN, publishers' download URLs from the winget manifest) | Only on Apps > Updates or `--update-apps` | Package queries; downloads installers | winget's own | Optional | Yes — `apps.update = block` |
| 8 | `Fleet.cs:104` | SMB share given by IT | Only with `--report` or "Add this PC" | Computer name, Windows version, disk numbers, savings, policy fingerprint, tweak ids | Windows (share ACL) | Optional | By not configuring it |
| 9 | Browser (`Explain.cs:142`, `SettingsTab.cs:76`, `EmailTab.cs:354,399`) | `bing.com`, `github.com/sponsors`, `console.cloud.google.com`, `entra.microsoft.com` | Only when the user clicks those buttons; opened unelevated in the default browser | Web search sends only the folder name (never the path or user name) | — | Optional | Not separately (user-initiated) |
| 10 | `StartupTab.cs` → Microsoft Defender (`MpCmdRun`) | Defender cloud, per Windows' own Defender settings | Only on "Scan with Microsoft Defender" | Whatever Defender's cloud protection is configured to send | — | Optional | Defender policy |

**Re-audit 1.2.55:** no new destination (all URLs in the source were re-listed). On company PCs rows 3–6 are blocked by default (Phone, Email); the Event Log is local.

**Not found:** analytics, error tracking, crash upload, WebSockets, webhooks, DNS-based callbacks, cloud storage uploads, hard-coded IPs. `Dns.GetHostName` (`DiskNumber.cs:35`) is a local call.

---

## D. Credential & permission exposure

| Item | Where / how | Exposure | Assessment |
|---|---|---|---|
| Elixar's own privilege | Runs elevated (UAC) on every start | Large: any bug is an admin bug | Inherent to the tool. Least-privilege alternative: give users the MSI-installed copy only, keep the `sysprep`, `drivers.remove`, `apps.remove`, `startup.change` rules blocked where not needed. |
| OAuth refresh tokens (Google/Microsoft), app passwords | `C:\ProgramData\Elixar\mail` (administrators only) + DPAPI CurrentUser (`SecretBox.cs:47`) | Readable only by that user running as admin, or by an admin/SYSTEM process | Good. Stolen token = send mail as that user (+ read/modify mailbox if Email cleaning was enabled). No reuse for other corporate services (scopes are mail-only). |
| Google client ID / secret | Built in from CI secrets or user-supplied file | Google treats Desktop-app secrets as public | Acceptable; PKCE is used. |
| Update signing key (private) | GitHub secret `ELIXAR_SIGNING_KEY`; public half in `update-key.pub.xml` | Compromise = can sign updates (B-1) | Protect with environment approval + 2FA; keep the offline backup safe. |
| Windows credentials, AD, service accounts, DB strings, SSH/cloud keys | Never read, stored or sent | Golden image only **detects** that such files exist (B-12) | No exposure found. |
| Unattend answer files | `Unattend.cs` — passwords in Microsoft's base64 form (not encryption) | A saved `unattend.xml` with a password is a credential file | Existing controls: refuses to save the Administrator password with "file left on the PC"; `DeleteAfter` removes it after setup. Treat exported files as secrets. |
| Auto-logon password | `GoldenImage.cs:131-148` | Elixar only **removes** it (registry and LSA) | Defensive. |
| Local PIN | `pin.dat`, PBKDF2-style, try limits | Not a security boundary against a local administrator (who can delete the file) | By design; document as a convenience lock. |

---

## E. Lateral movement risk

Elixar has **no code that reaches other machines** except: SMB share for fleet reports (written by the PC's own identity), the user's mail provider, and GitHub/Google/Microsoft download endpoints. There is no SSRF surface (no server), no remote command channel, no AD/LDAP, SQL, SSH, RDP, WinRM or SNMP code.

| Path | Entry point | Needs | Trust boundary | Reachable | Realistic impact | Prevents it |
|---|---|---|---|---|---|---|
| E-1 Malicious signed update | Compromised GitHub account + signing secret (B-1) | Attacker controls the repo/key **and** a user clicks Yes | Internet → admin on each PC | Everything that PC's admin can reach: local data, cached domain credentials (via other tooling), the corporate network from that PC | High, fleet-wide over time | Environment approval, 2FA, branch protection; **corporate: `updates.check = block`, deploy only reviewed builds by MSI** |
| E-2 Fleet share poisoning | Write access to the report share | Any domain PC | PC → IT console | IT admin's Elixar parses JSON (safe) and shows values | False inventory data only | Share ACL; reports are advisory |
| E-3 Stolen mail token | Admin/SYSTEM-level malware on the PC | Already admin on that PC | PC → M365/Gmail | That one mailbox (send; read/modify if cleaning was enabled) | Phishing from a real internal address | `email = block`; Entra consent policy; Conditional Access (compliant device) |
| E-4 Elixar as a pivot/proxy | — | — | — | None | Not possible: no inbound service, no relay code | — |

---

## F. Persistence assessment

| Mechanism | Created | Runs as | Removed on uninstall? |
|---|---|---|---|
| Scheduled tasks `\Elixar\...` (low-space check, check at sign-in, weekly clean, daily tidy, email reports, policy cleaning) | Only when turned on by the user or policy | Program Files copy (`Install.Exe`), most with "highest privileges" | **Yes** — all six (policy-cleaning task fixed in 1.2.50) |
| Program Files copy `C:\Program Files\Elixar` | "Install" or MSI | — | Yes |
| `HKLM\...\Uninstall\Elixar`, `HKLM\Software\Elixar`, `HKCU\Software\Elixar` | Install / settings | — | Uninstall entry yes; settings keys **only with `--purge`** (1.2.53) |
| `C:\ProgramData\Elixar` (policy, logs, savings, mail tokens) | First run | — | Mail sign-ins revoked/deleted; rest **only with `--purge`** (1.2.53) |
| Undo folders (`C:\ProgramData\Elixar Undo`, `X:\Elixar Undo`) | When files are removed | — | **Only with `--purge`** (1.2.53; before: never) |
| Windows tweaks registry values (ads, telemetry policy, etc.) | Only when a tweak is applied | — | No — they're user settings; "Put all back to Windows' default" or Logs > Undo restores them |
| `adb` server | Phone tab | Elixar's token | Stopped on exit (1.2.53) |
| Google OAuth grant | Email setup | — | Revoked at Google on disconnect/uninstall |
| Microsoft OAuth grant | Email setup | — | Deleted locally; **consent stays at Microsoft** until removed (B-8) |
| `HKLM\SOFTWARE\Policies\Elixar` | IT (GPO/Intune/.reg/ADMX) | — | No — IT's own setting |
| Event log "Elixar" + event source (1.2.55) | First change written | — | **Only with `--purge`** (it is a record, like the logs) |

No service, driver, browser extension, WMI subscription, Run-key entry for itself, or hidden account is created.

---

## G. Supply chain risk

| Component | Risk | Assessment |
|---|---|---|
| Third-party libraries | **None** — only .NET Framework class libraries; no NuGet, npm or pip packages in the app | Very low; no lockfiles needed |
| Build | `build.ps1` uses Windows' own `csc.exe`; no downloads | Low |
| CI (`ci.yml`) | `actions/checkout`, `actions/upload-artifact` | **Fixed**: pinned to commit SHAs; token `contents: read` |
| Release (`release.yml`) | `contents: write`; signing secret; `dotnet tool install wix --version 4.0.5` (from nuget.org, used only to build the MSI) | Pinned actions; `environment: release` added. **Remaining:** WiX tool is version-pinned, not hash-pinned — acceptable for the MSI step; could be vendored later |
| Runtime downloads | Elixar update (signed + hashed), Google `adb` (Authenticode-checked), winget packages (winget's trust model; restricted to Microsoft sources in 1.2.53) | Adequate |
| Code signing of the exe | **Not code-signed** (no certificate) | Windows SmartScreen warns; integrity relies on GitHub + SHA256.txt. Recommend an Authenticode certificate (or Microsoft Store/MSIX signing) before company-wide use |
| Commits | Signed by the cloud session's SSH key, shown "Unverified" on GitHub | Cosmetic; authenticity of releases comes from the update signature |
| Dependabot (1.2.55) | Opens pull requests to update the pinned GitHub Actions weekly; nothing merges by itself | Good; review each PR (check the new commit belongs to the official action) |
| Release job runs the built exe | `Elixar.exe --admx` makes the Group Policy template inside the release job | Acceptable (it is the code being released); combined with B-13 it could reach the job token — fix B-13 |
| Dependency confusion / typosquatting | Not applicable (no package manager dependencies) | — |

---

## H. Threat scenarios

**Scenario A — External application compromise.** *Preconditions:* none apply — Elixar exposes no service to the network. The only inbound socket is the OAuth loopback during a sign-in (random port, loopback-only, `state` + PKCE). *Impact:* not reachable from outside. *Missing controls:* none identified. *Verify:* `netstat -abno` on a test PC during idle and during sign-in.

**Scenario B — Compromised dependency.** *Preconditions:* there are no third-party code dependencies; the realistic equivalents are a compromised GitHub Action or the WiX tool in the release job. *Path:* malicious action reads `ELIXAR_SIGNING_KEY` in the release job and signs a payload. *Impact:* see D. *Existing:* actions pinned to SHAs (1.2.53). *Missing:* environment approval must be switched on by the owner; `persist-credentials: false` on checkout (B-13). *Verify:* Settings > Environments > release shows "Required reviewers".

**Scenario C — Stolen service credentials.** Elixar uses **no service accounts**. The only credentials are per-user mail tokens. *Path:* admin-level malware decrypts the DPAPI blob. *Impact:* that mailbox only. *Existing:* admin-only folder + DPAPI. *Recommended:* `email = block` on corporate PCs, or admin-consented app with Conditional Access. *Verify:* Entra sign-in logs for the Elixar app id.

**Scenario D — Malicious or unauthorized update.** *Preconditions:* control of the repo **and** the signing secret, **and** a user who clicks Yes. *Existing:* RSA-3072 signature, SHA-256 of zip and exe, no downgrades, size limits, user confirmation, install only into admin-only Program Files. *Missing (owner):* 2FA, branch protection, environment approval. *Company mitigation:* `updates.check = block`; distribute only builds you have hashed and approved via the MSI. *Verify:* compare `SHA256.txt` / release manifest with your own build of the tagged source.

**Scenario E — Compromised PC (the "application host").** *Preconditions:* attacker is admin on a PC with Elixar. *What Elixar adds:* a mail token (if connected), the PC's write access to the fleet share, and schedules that run the Program Files copy with highest privileges (replacing that copy already requires admin, so no escalation). *Not added:* no AD, database, VPN or network-device credentials — Elixar never stores them. *Mitigations:* EDR, block email by policy, share ACLs.

---

## I. Recommended isolation architecture (zero-trust deployment)

Elixar is an endpoint tool: "isolation" means **limiting what each PC's copy may do and reach**, not network segmentation of a server.

1. **Distribution**: deploy the **MSI** from a build you verified (hash matches the signed release, or built yourself from the tagged source). Don't let users run copies from Downloads.
2. **Organization policy** (`HKLM\SOFTWARE\Policies\Elixar` via GPO/Intune; export from Policy manager > "Export for IT" as a .reg file or the Group Policy / Intune template `Elixar.admx`):
   - `updates.check = block` (IT pushes updates)
   - `email = block` (or allow only with an admin-consented Entra app + Conditional Access)
   - `sysprep = block` except on imaging PCs — **automatic from 1.2.54** on PCs joined to an AD domain or Microsoft Entra ID (together with `phone = block`); IT re-allows with `sysprep = allow` / `phone = allow`
   - `apps.update = block` if Intune/SCCM already patch apps; otherwise leave on (Microsoft sources only)
   - `drivers.remove = block`, `phone = block` unless needed
   - `pin.policy = on` on shared PCs
3. **Outbound allowlist** (if your proxy/firewall filters per process): `Elixar.exe` needs nothing when the rules above are set. If email is allowed: Microsoft Graph/login or Google APIs only.
4. **Identity**: Entra ID → disallow user consent to unverified multi-tenant apps; review "Enterprise applications" for an Elixar entry.
5. **Logging (1.2.55)**: collect Event Viewer > Applications and Services Logs > *Elixar* with Windows Event Forwarding or your SIEM agent (readable only by SYSTEM, Administrators and Event Log Readers). Event ids: 1000 cleaning, 1001 Undo, 1002 settings/registry, 1003 apps/startup, 1004 security, 1005 email, 1006 Windows commands; failures are warnings.
6. **EDR**: keep alerts on for shadow-copy deletion and event-log clearing — with `sysprep = block` Elixar never does these. Optionally allowlist Elixar's signed hash after you verify it.
7. **Fleet reports**: dedicated share; computers *create/write* only; IT *read*; no execute.
8. **Owner (GitHub) controls**: 2FA on the account; branch protection on `main`; Settings > Environments > `release` > **Required reviewers = you**; move `ELIXAR_SIGNING_KEY` into that environment's secrets; keep the offline key backup safe; add an Authenticode certificate when possible.
9. **Removal**: `Elixar.exe --uninstall --quiet --purge`, then `msiexec /x` for MSI installs; remove the Entra consent if email was used.

---

## Cure Agent and Cure Server (1.2.56, Elixar for Business)

Version 1.2.56 adds the first network service: **Cure Server** (on the company's own server) receives status reports from **Cure Agent** (a service on each PC). Elixar for Private contains none of this code (`#if BUSINESS`).

| Question | Answer (with evidence) |
|---|---|
| Can the server (or whoever controls it) run anything on PCs? | **No.** The agent's only use of an answer is the next report time, clamped to 5–1440 minutes (`CureAgent.cs` `SendOnce`). There is no download, command, script or file-transfer code in the agent. |
| What leaves each PC? | Computer name, Windows version, model, OU, free/total space per drive, cleanable bytes per level, disk health (name, wear, temperature), last clean, Elixar savings, app-update count, startup-program count, policy fingerprint, company locks. Never file names, file contents, emails, browsing, the signed-in user, passwords or keys. `CureAgent.exe --show` prints a report. |
| Where does it go? | Only to the server in `HKLM\SOFTWARE\Policies\Elixar\Cure\Server` (Group Policy) or set at install; HTTPS only (http only to 127.0.0.1 in the self-test); with `Thumbprint` only that certificate is accepted (`CureAgent.CertOk`); no redirects; no proxy for loopback. |
| PC identity | Enroll once with the company token (server keeps only its SHA-256), then every report is HMAC-SHA256 signed with a per-PC 32-byte key; the server refuses wrong signatures, clock skew > 5 min, repeated nonces, > 1 report/min, > 64 KB bodies, and removed PCs (`CureServer.Report`). Keys: DPAPI machine scope, admin-only folders, keys read via a locked registry/file. |
| New listener | `CureServer` on the server only: http.sys HTTPS port (default 8443), URL reserved for `NT SERVICE\CureServer` (its own low-rights virtual account), firewall opened on the **domain profile only** (`CureSetup.InstallServer`). PCs open **no** port. |
| Console access | Windows sign-in (Kerberos/NTLM) and membership of the chosen group or the server's Administrators; read-only (non-GET → 405); CSP `default-src 'none'; script-src 'self'`, `X-Frame-Options: DENY`, `nosniff`, `no-store`, HSTS; all PC text rendered with `textContent` (self-test checks the script has no `innerHTML`/`eval`); CSV formula guard. |
| Residual risks | (1) The enrollment token is readable by local users wherever Group Policy places it — it only lets a *new* PC join and send reports (false data, visible in the console); rotate with `--new-token`, remove with `--remove-pc`. (2) The server holds an inventory of all PCs — protect it like any management server (patching, admin access, backups). (3) The agent runs as LocalSystem to measure all profiles and disk health; it is read-only and has no inbound port. |
| Removal | `msiexec /x` runs `CureAgent.exe --uninstall` / `CureServer.exe --uninstall` before files are removed (service, settings key, device key, URL reservation, certificate binding, firewall rule, self-made certificate); `--purge` deletes the server's reports. |

Self-tests (`SelfTest1250.cs` `Cure`): enrollment, signed reports, wrong token, wrong/old signature, replay, rate limit, oversize, removed PC re-enrolls, console auth/group/read-only/headers/no-HTML rendering, service commands (quoting, accounts, URL ACL, domain-only firewall).

**Conclusion for 1.2.56 unchanged: NO BACKDOOR INDICATORS IDENTIFIED IN REVIEWED SCOPE.** Cure is a documented, opt-in, inbound-to-server-only reporting channel with no command path to PCs. B-13 (job token kept in the checkout) is fixed in 1.2.56 (`persist-credentials: false`).

## J. Final security assessment

**NO BACKDOOR INDICATORS IDENTIFIED IN REVIEWED SCOPE.**

No hidden accounts, master keys, authentication bypasses, remote-control channels, undocumented listeners, telemetry, obfuscated or dynamically loaded code, or behaviour inconsistent with the documentation were found. This is a static review plus automated tests; it does not rule out every possible backdoor, and it does not cover the security of the GitHub account that publishes releases.

---

## Production release gates

| Gate | Status |
|---|---|
| Unauthorized remote access mechanisms | None found |
| Authentication bypass | None found |
| Unrestricted access to corporate infrastructure | None — no AD/DB/network-device code |
| Unauthorized sensitive-data transmission | None found; email reports and fleet reports are opt-in and policy-controllable |
| High-impact credential exposure | None found |
| Exploitable privilege escalation | None confirmed |
| Uncontrolled remote command execution | None |
| Malicious/compromised dependencies | None (no third-party dependencies) |
| Critical trust-boundary violations | None confirmed |

**Decision: CONDITIONALLY APPROVED for a pilot — NOT YET APPROVED for company-wide production.**

Blocking items before company-wide use (not code bugs; they can't be closed by static review):

1. GitHub owner controls in place: 2FA, branch protection, `release` environment with required approval (B-1).
2. Organization policy deployed as in section I (at least `updates.check`, `email`, `sysprep`).
3. A dynamic check on a representative company PC: network capture during idle, sign-in and cleaning; confirm only the connections in section C appear.
4. Authenticode signing (or Store/MSIX signing), or an internal hash-allowlisting process for the exe.
5. Pilot with EDR on, reviewing alerts for one to two weeks.
6. Fix B-13 (`persist-credentials: false` in both workflows).

---

## The key question

> *If this application is deployed and connected to my company's systems, what could an attacker, malicious developer, compromised dependency, or unauthorized third party potentially access, steal, modify, or control — and what specific measures are required to prevent that?*

**What Elixar can touch:** the PC it runs on, as administrator — files, registry, scheduled tasks, installed apps, drivers, Windows settings. Optionally: the signed-in user's own mailbox (if email is connected), a fleet-report share, and download endpoints at GitHub, Google and Microsoft. It has **no code for Active Directory, databases, VPN, network devices, telephony or internal web apps**, holds no service accounts, and runs no network service.

**Who could abuse that, and how:**
- **A malicious developer or a hijacked GitHub account** is the realistic top risk: it could publish a signed update that runs as administrator after users click "Yes". Prevent: GitHub 2FA + branch protection + release approval (owner), and for the company `updates.check = block` with MSI deployment of verified builds only.
- **A compromised dependency**: none exist in the app; in the build pipeline the GitHub Actions are now pinned to exact commits.
- **An attacker already admin on a PC** gains little extra from Elixar: at most a mail token (block email by policy) and write access to the fleet share (restrict by ACL).
- **An outside network attacker** has no entry point: Elixar exposes no listener beyond a short-lived, loopback-only sign-in callback.

**Required measures:** section I, items 1–9, and the six release-gate items above.

---

### Changes made after this review (1.2.53, commit `564bf83`)

| Fix | Files |
|---|---|
| Logs moved to admin-only `C:\ProgramData\Elixar\logs` (with migration); log viewers open with Notepad from System32 | `Security.cs`, `ChangeLog.cs`, `DashboardHome.cs`, `MainForm.cs`, `LogsTab.cs` |
| Policy rule `updates.check` (Golden image preset blocks it) | `Policy.cs`, `MainForm.cs` |
| winget limited to `winget`/`msstore` sources | `AppUpdates.cs` |
| `adb` server stopped on exit when Elixar started it | `Phone.cs`, `DashboardHome.cs` |
| Uninstall asks to delete Undo backups and logs; `--purge` for IT; corrected uninstall text | `Setup.cs`, `Quarantine.cs`, `Program.cs`, `Cli.cs` |
| Microsoft disconnect explains how to remove the consent at Microsoft | `EmailTab.cs` |
| CI: Actions pinned to SHAs, read-only test token, `release` environment | `.github/workflows/ci.yml`, `release.yml` |
| Self-test for the fixes (249 checks pass on Linux; Windows run on GitHub) | `SelfTest1250.cs` |
| **1.2.55 review fixes:** (1) the weekly clean's email report ignored `email = block` — every send, mailbox scan/trash and sign-in check now re-checks the policy in one place; (2) every Sysprep/golden-image button and window and the Android tools download re-check the policy (not only the greyed tab); (3) Event Log: the "Elixar" source must belong to its own admin-only log (never Application), the log keeps 20 MB and overwrites the oldest instead of dropping new events, `%%` in names can't be read as message numbers, and a write on one thread no longer drops another thread's event | `MailProviders.cs`, `Automation.cs`, `SysprepTab.cs`, `ImageCleanForm.cs`, `UnattendForm.cs`, `PhoneTab.cs`, `WinEvents.cs` |
| **1.2.55:** email also off on company PCs (Settings and Email tab locked, every email action re-checks the policy); Group Policy / Intune template (`Elixar.admx`, en-US + he-IL) made from the same rules; every change also written to an admin-only "Elixar" event log; LICENSE (PolyForm Noncommercial 1.0.0), SECURITY.md (private reporting), Dependabot for the pinned Actions | `Policy.cs`, `EmailTab.cs`, `EmailCleanTab.cs`, `PolicyAdmx.cs`, `Cli.cs`, `WinEvents.cs`, `ChangeLog.cs`, `Setup.cs`, `.github/` |
| **1.2.54:** on a PC joined to an AD domain or Microsoft Entra ID, Phone and Sysprep tools are off and locked (no phone or image checks run); IT can allow them by policy | `Policy.cs` (`DomainJoin`, `Company`, `ByCompany`), `PolicyTab.cs`, `PhoneTab.cs`, `SysprepTab.cs` |
