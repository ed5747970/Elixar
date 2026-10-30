# Elixar — Backdoor & Compromise Assessment

| | |
|---|---|
| Application | Elixar — Windows disk-space, maintenance and PC-health tool (WinForms, .NET Framework 4.x, C# 5). Two editions: **Elixar for Private** and **Elixar for Business** (adds Cure Agent, Cure Server, Cure Console, Domain PCs tab, Cure email alerts) |
| Version reviewed | **1.2.62 preview** (branch `preview/1.2.62`, commit `bda1c74`), compared with the last assessed state **1.2.55/1.2.56** (`c37f769`). Published release checked: **v1.2.61** |
| Scope | All 111 C# source files (~40,800 lines) incl. `cure/` (agent/server entry points, console HTML/JS/CSS), `.github/workflows/{ci,cure-e2e,release}.yml`, `build.ps1`, `package/*.wxs`, `tools/`, `update-key.pub.xml`. 52 files / ~7,700 lines changed since the last assessment were read in full where security-relevant (network, services, installer, update, release, data collection, deletion) |
| Method | Static review: inventory of every listener, outbound connection, URL, TLS setting, process start, PowerShell script, scheduled task, service, registry/ACL write, credential store, update and release step; searched for dynamic code loading, encoded payloads, autostart and persistence APIs. Supporting evidence: full self-test (Private 277 / Business 286 checks pass on Linux; Windows runs on GitHub Actions incl. the 41-check Cure end-to-end test), the published v1.2.61 release file, and GitHub's public repository metadata |
| Not done | No packet capture on a production-like Windows PC; no fuzzing; no penetration test of a real Cure Server; GitHub account settings (2FA, environment approval) not visible from here; no interaction with company infrastructure. **No application code was changed by this audit** |
| Date | 2026-10-30 |

> **Conclusion (section J): NO BACKDOOR INDICATORS IDENTIFIED IN REVIEWED SCOPE.**
> This does not prove that no backdoor can exist. It means none was found in the code, build, update and release path reviewed here.
> **Production decision: NOT YET APPROVED for company-wide production** — because of unresolved owner/supply-chain controls (unprotected `main` branch, unsigned published binaries, a release-pipeline ordering weakness), not because of malicious code. See "Production release gates".

---

## Context: what Elixar is, for this assessment

Elixar is a **desktop application that runs as administrator** on each PC (UAC at start). **Elixar for Business** adds the only network service: **Cure Server** (installed by IT on one server) receives status reports from **Cure Agent** (a LocalSystem service on each PC). The main trust facts:

1. **Anything Elixar is tricked into doing, it does as an administrator of that PC** (Cure Agent: as SYSTEM).
2. **Whoever can publish a validly signed release controls every PC that accepts the update** (B-1, B-19, B-20).
3. **The Cure Server holds an inventory of every enrolled PC** — it is a management server and must be protected like one.

| Integration | Exists? | Notes (evidence) |
|---|---|---|
| Local PC (files, registry, Task Scheduler, services, Windows tools) | Yes — core function | As administrator; Cure Agent as SYSTEM |
| **Cure Agent → Cure Server** (Business) | **Yes, opt-in (IT)** | HTTPS POST of status reports to the company's own server; per-PC HMAC keys; the server's answer can only change the report interval (`CureAgent.cs:437-442`) |
| **Cure Console** (Business) | **Yes, on the Cure Server** | Read-only web page on the same port, Windows sign-in (Kerberos/NTLM), Administrators or one AD group (`CureServer.cs:266, 302-306, 317-325`) |
| Active Directory / Entra ID | **Read-only, local** | Elixar only asks Windows *whether* the PC is joined (`Policy.cs` `DomainJoin`). Cure Agent reads the PC's OU from the local Group Policy state key (`CureAgent.cs:349-354`). The Cure Console relies on Windows authentication by http.sys. **No LDAP query, no domain-controller connection, no credential use** |
| Microsoft 365 / Google / IMAP-SMTP mail | Optional | User-connected mailbox for reports; Business server can email Cure alerts (only to its own connected mailbox, `MailProviders.cs:248`; policy-checked, `CureAlerts.cs:246`) |
| SMB file shares | Optional (IT) | `--report <\\server\share>` fleet JSON files (unchanged) |
| winget / Microsoft Store | Optional | App updates; **listing also runs automatically** (Dashboard PC health ≤ every 6 h; Cure Agent daily as SYSTEM) — B-29 |
| Windows Event Log | Yes | Local admin-only "Elixar" log for SIEM/WEF |
| Android phones (USB, `adb`) | Optional, blocked on company PCs by default | Unchanged |
| SQL servers, network devices, SSH/RDP/WinRM/SNMP, telephony/call-center, internal web apps, cloud consoles, VPN | **No** | No code paths found. No connection code other than the inventory in section C |

Nothing in the code or documentation suggests planned integrations beyond these. The Cure protocol comment states a "Stage 1 is reports only" design (`CureProtocol.cs:1-3`); any future "stage 2" (remote actions) would change this assessment fundamentally and must be re-audited before release.

---

## A. Intentional backdoor findings

**None found.** Each checklist item was searched for explicitly across the whole tree (including the 1.2.56–1.2.62 additions):

| Looked for | Result | Evidence |
|---|---|---|
| Hidden admin accounts, master passwords, hard-coded auth bypass | None | Only authentications: optional local PIN (`PinLock.cs`, salted/stretched, try limits); Cure enrollment token (server stores only its SHA-256, constant-time compare, `CureServer.cs:113`, `CureProtocol.cs:75`); per-PC HMAC keys; Windows authentication for the console. No master token, no default password, no backdoor header |
| Undocumented privileged endpoints / listeners | None besides documented ones | Inbound sockets in the whole tree: OAuth loopback listener during a sign-in (`Mail.cs:225`, `MailProviders.cs:110`, 127.0.0.1/::1, random port, PKCE + state); `FakeMail` (self-test only); **Cure Server** `HttpListener` with exactly 7 routes: `/api/v1/enroll`, `/api/v1/report` (POST, anonymous+token/HMAC), `/`, `/index.html`, `/console.js`, `/console.css`, `/api/v1/pcs`, `/api/v1/pc`, `/api/v1/export.csv` (GET only, Windows auth) — `CureServer.cs:282, 338-350`. No named pipes, no other HTTP/TCP/UDP listeners |
| Remote access, reverse shell, tunnels, proxying | None | No code connects out and waits for commands. The Cure Agent parses one integer from the server's reply (`"next"`, clamped 5–1440 min, `CureAgent.cs:437-442`); there is **no** download, command, script, file-transfer or configuration-push code in the agent |
| Command execution from outside input | None from the network | Every process is a fixed Windows tool resolved to System32 (`Win.SystemExe`) or winget from the protected `WindowsApps` package folder (`AppUpdates.cs:35`); arguments fixed or validated (`CureSetup.Q` rejects quotes; winget ids regex-validated) |
| Obfuscated / encrypted / dynamically loaded code | None | No `Assembly.Load*`, `Reflection.Emit`, `CSharpCodeProvider`, `VirtualAlloc`, `WriteProcessMemory`, `CreateRemoteThread`, `LoadLibrary`. The 1.2.58 change removed `-EncodedCommand` and `-ExecutionPolicy Bypass`: PowerShell now runs plain-text scripts with `RemoteSigned` and `$PSHOME`-only modules (`Core.cs:316-327`). `FromBase64String` uses are data (keys, tokens, signatures, startup-entry ids), never code |
| Downloaded and executed code | Three documented, verified cases | (1) Elixar's update: RSA-3072-signed manifest, SHA-256 of zip and exe, edition match, no downgrade, user confirmation (`Updates.cs:198-226`); (2) Google `adb` (Authenticode-checked); (3) winget packages (Microsoft sources only) |
| Secret data collection / telemetry | None to the vendor | No analytics, crash upload or usage tracking. The Cure Agent sends data **only to the company's own server** — but more than the "never sent" note claims (B-24) |
| Persistence beyond documented schedules/services | None | 8 scheduled tasks and 2 Business services, all documented, all removed by MSI uninstall (section F). No Run keys, WMI subscriptions, IFEO, Winlogon or AppInit entries for itself |
| Behaviour different from documentation | Minor gaps only | Undocumented switches are internal (task entry points, self-test): `--auto-clean`, `--daily-tidy`, `--mail-daily`, `--check-space`, `--keep-in-check`, `--cure-alerts`, `--policy-clean`, `--apply-update`, `--service`, `--selftest`, `--test`, `--no-elevate`. `--test`/`--no-elevate` only run **without** admin rights; `--fake-adb` needs `ELIXAR_SELFTEST=1` (`Program.cs:48`). Report contents broader than documented (B-24) |
| Test hooks in the production binary | Present, not exploitable | `*Override` fields are static fields set only by the self-test code; nothing reads them from files, registry or network |

---

## B. Unintentional backdoor risks (vulnerabilities and risky designs)

Severity is for a **corporate deployment** (Elixar as administrator on employee PCs; Business: Cure Server on a domain server).

### New in this re-audit (1.2.56 – 1.2.62)

| # | Finding | Severity | Status / fix |
|---|---|---|---|
| **B-19** | **Release pipeline: a third-party tool runs between packaging and manifest signing.** In `release.yml` the update zips and their SHA-256 manifests are made at line 83, then the WiX tool is downloaded from nuget.org (`dotnet tool install --global wix --version 4.0.5`, version-pinned, **not hash-pinned**, line 112) and run, then the manifests are signed with `ELIXAR_SIGNING_KEY` at line 149 — all in **one job with a writable workspace**. A compromised WiX package (or anything it pulls in) could replace `Elixar.zip` and rewrite the manifest hash; the next step would sign it, and every client would accept it as genuine. The SSL.com eSigner action (SHA-pinned, but it downloads SSL.com's CodeSignTool at runtime) also runs before manifest signing. `pwsh` steps also load PowerShell profiles that an earlier step could plant | **High** (supply chain) | **Open.** Fix: (a) build the MSIs in a separate job with no secrets that only downloads the finished artifacts; (b) sign manifests in a dedicated job (environment `release`) that receives only `*.zip` + manifests uploaded **before** any third-party tool ran, re-hashes them, and signs; (c) run `shell: pwsh -NoProfile`; (d) vendor or hash-check the WiX package |
| **B-20** | **`main` branch is not protected** (GitHub public API: `protected: false`, checked 2026-10-30). Combined with B-1, anyone who obtains push access (stolen session/PAT, compromised collaborator) can change code that a later release signs | **High** (owner control) | **Open** — was a blocking item in the last report. Fix: branch protection (require PR + review, no force-push, status checks), 2FA, and verify the `release` environment requires your approval (could not be verified here — the API is not reachable through this proxy) |
| **B-21** | **Cure enrollment token exposure.** (a) The MSI passes it as the public property `CURETOKEN` on the custom-action command line (`package/Elixar-Business.wxs:56`) — not `Hidden="yes"`, so verbose MSI logs (`msiexec /l*v`, Intune/SCCM logging) store it in clear text; (b) when deployed by Group Policy it sits in `HKLM\SOFTWARE\Policies\Elixar\Cure`, readable by every local user (documented before). The token only allows enrolling **new** PCs and sending reports (no access to other PCs' data, no commands) | Low–Medium | **Open.** Fix: `<Property Id="CURETOKEN" Hidden="yes" />` and `HideTarget="yes"` on the custom action; rotate after rollout (`CureServer.exe --new-token`); remove rogue entries (`--remove-pc`) |
| **B-22** | **Enrollment rate limit counts failed attempts** (`CureServer.cs:104-109` runs before the token check at line 113): anything on the domain network can send 200 bad enrollments per hour and block real enrollments for that hour | Low (availability) | Open. Fix: count only successful enrollments; separate per-IP limit for failures |
| **B-23** | **Duplicate computer names on enrollment** (`CureServer.cs:116`): a token holder can enroll a second "CEO-LAPTOP" and feed false data next to the real one; the console doesn't flag duplicates | Low (integrity of advisory data) | Open. Fix: mark duplicates in the console and show enrollment time/IP (already stored) |
| **B-24** | **Cure report content broader than documented.** `CureProtocol.cs` says no file names, browsing or "who is signed in" are sent — true — but reports also carry: the 30 largest **installed apps** with publisher, install and last-used dates (`CureAgent.cs:155`, `317-326`), up to 60 **startup program names** (`:170`), the PC's **AD OU distinguished name** (`:132`), model, account count, and up to 10 **error texts** that can contain paths with user names (`:174`). All clipped/sanitized server-side (`CureProtocol.cs:87`). This is a software inventory: useful to IT, and useful to an attacker who compromises the Cure Server | Low (privacy / reconnaissance value) | Open. Fix: document precisely in `CureProtocol.cs` and the guide; strip paths from error texts; optional policy "counts only" |
| **B-25** | **Secure wipe cannot be blocked by company policy.** Free-space wiping and file shredding (1.2.59, `SecureWipe.cs`) permanently destroy data and look like anti-forensics to EDR; unlike Sysprep/Phone/Email there is no `Policy` rule and no company-PC default lock | Medium (insider / evidence destruction) | Open. Fix: add a `wipe` policy rule, blocked by default on domain/Entra-joined PCs like `sysprep` |
| **B-26** | **Shred renames by path after the handle is closed** (`SecureWipe.cs:246`). Content overwrite is handle-checked, and the final delete is link-safe, but the rename in between follows the path: malware running as the user could race a junction into a user-writable parent folder so an elevated **rename** lands in a protected folder (the delete is then refused) | Low (needs local malware + race; impact: a renamed protected file) | Open. Fix: rename through the open handle (`SafeFile.MoveInto`, already used by Undo) |
| **B-27** | **Check & fix ignored the company policy** for app updates and startup changes (1.2.59–1.2.61): it offered and performed winget upgrades and startup changes even when Policy manager / GPO set `apps.update` or `startup.change` to block | Medium (policy bypass) | **Fixed in 1.2.62** (found during the 1.2.62 security review, before this audit): blocked fixes are neither offered nor performed, checked again at execution (`CheckFix.cs:178`); self-test covers it |
| **B-28** | **Non-MSI uninstall of Elixar for Business leaves Cure services registered.** `Elixar.exe --uninstall` removes tasks and files (`Setup.cs:80-100`) but not the `CureAgent`/`CureServer` services, URL reservation, certificate binding or firewall rule; MSI uninstall does remove them (`Elixar-Business.wxs:57-62`). binPath points into admin-only Program Files, so no privilege escalation | Low (residue) | Open. Fix: call `CureSetup.UninstallAgent/UninstallServer` from `Setup.Uninstall` in the Business build |
| **B-29** | **winget listing contacts Microsoft even when `apps.update = block`.** PC health (1.2.62) lists app updates (≤ every 6 h from the Dashboard) and Cure Agent does so daily **as SYSTEM**; the policy only stops installing | Low (unexpected outbound traffic) | Open. Fix: skip listing when `apps.update = block` |
| **B-30** | **PC health (1.2.62)** reads Defender/firewall/TPM/BitLocker/update history/event logs/restore points/battery | Info | Reviewed: one fixed read-only script (test asserts no `Set-/Remove-/Invoke-/Start-`, no download, only the `Microsoft.Update.Session` COM object, and that Logs doesn't classify it as a change); ignore list kept in the admin-only folder (`Health.cs:213`); Secure Boot off can't be ignored; Windows pages opened from a fixed list. No new outbound destination |

### Carried over from earlier assessments (status re-checked)

| # | Finding | Severity | Status in 1.2.62 |
|---|---|---|---|
| B-1 | Update trust = GitHub account + `ELIXAR_SIGNING_KEY` | **High** (supply chain) | Mitigations in code unchanged and verified (`Updates.cs:198-226`). **Owner controls still unverified/open** — see B-19, B-20 |
| B-2 | Logs readable by all users | Medium | Fixed (1.2.53) |
| B-3 | No way to stop update checks | Low–Medium | Fixed (`updates.check`) |
| B-4 | winget third-party sources | Medium | Fixed — `winget`/`msstore` only; winget.exe resolved only inside `Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe` (`AppUpdates.cs:28-40`) — important now that Cure Agent runs it as SYSTEM |
| B-5 | Elevated `adb` outlives Elixar | Low | Fixed |
| B-6 | EDR-sensitive behaviour | Medium (operational) | **Partly reduced** (1.2.58: no `-EncodedCommand`, no `Bypass`). Still EDR-sensitive: Golden-image event-log clearing / shadow-copy deletion (behind `sysprep` rule), **Secure wipe (B-25)**, Cure Agent as SYSTEM reading all profiles' sizes |
| B-7 | Email reports to personal mailboxes | Low–Medium | Unchanged; email is off by default on company PCs; Cure alerts follow the same rule |
| B-8 | Microsoft consent not revoked at Microsoft | Low | Unchanged (platform limit) |
| B-9 | Uninstall left Undo/logs | Medium | Fixed (`--purge`) |
| B-10 | Fleet share files unauthenticated | Low | Unchanged (Cure replaces this with signed reports) |
| B-11 | Uninstall strings executed | Low | Unchanged, controls adequate |
| B-12 | Golden image scanner reads credential *locations* | Low | Unchanged, behind `sysprep` |
| B-13 | CI job token left in checkout | Medium | Fixed (`persist-credentials: false`, all 3 workflows) |
| B-14 | Company-PC lock fails open | Low | Unchanged — set rules explicitly by GPO |
| B-15/B-16 | Event Log readers / ACL timing | Low / Medium | Fixed (1.2.56) |
| B-17 | Wrong TrustedInstaller SID | Low | Fixed (1.2.57) |
| B-18 | Cure services ran without `--service` | Low | Fixed (1.2.57) |
| — | **Code signing** | Medium | **Still open in practice:** the release workflow supports SSL.com eSigner and `.pfx`, but the **published v1.2.61 `Elixar.exe` has no Authenticode signature** (checked: empty PE security directory) |

Hardening verified in this round (Business additions): Cure service binaries must run from admin-only `Program Files\Elixar`, no link in the path, folder ACL checked (`CureSetup.CheckPlace`); Cure files updated only from Elixar's own verified update folder (`CureSetup.cs:81`); server runs as `NT SERVICE\CureServer` (low rights), settings key and data folder admin/SYSTEM/service only; firewall rule **domain profile only** (`CureSetup.cs:189`); self-signed server certificate non-exportable, RSA-3072, agents pin its thumbprint; agent refuses http (except loopback self-test), redirects, URLs with credentials or queries; responses capped at 8 KB, requests at 64 KB; JSON parsed with `JavaScriptSerializer` without type resolvers; console: CSP `default-src 'none'`, `X-Frame-Options: DENY`, `nosniff`, `no-store`, HSTS, read-only (non-GET → 405), no `innerHTML`/`eval` in `console.js`, CSV formula guard.

---

## C. Outbound communication inventory

| # | Origin (file) | Destination | When | Data sent | Auth | Needed? | IT can disable? |
|---|---|---|---|---|---|---|---|
| 1 | `Updates.cs:188` | `api.github.com/repos/ed5747970/Elixar/releases/latest` | Weekly at start; on "Check for updates" | IP, `User-Agent: Elixar/<version>` | None | Optional | Yes — `updates.check = block` |
| 2 | `Updates.cs:149` | `github.com`, `*.githubusercontent.com` (release files only; host-checked, `Updates.cs:172-177`) | After the user clicks Yes | IP, UA | None | Optional | Yes (same rule) |
| 3 | `Phone.cs:112` | `dl.google.com` (platform-tools) | On "Get Android tools" | IP | None | Optional | Yes — `phone = block` (default on company PCs) |
| 4 | `Mail.cs`, `GmailClean.cs` | Google OAuth/Gmail APIs | If a Google account is connected | Report text (PC name, disk numbers, item names, **PC health score and findings since 1.2.62**) | OAuth 2 + PKCE; DPAPI token in admin-only folder | Optional | Yes — `email = block` (default on company PCs) |
| 5 | `MailProviders.cs` | `login.microsoftonline.com/common`, `graph.microsoft.com` | If a Microsoft account is connected | Same as 4 | OAuth 2 + PKCE (`Mail.Send`) | Optional | Yes — `email = block`; Entra consent policy |
| 6 | `MailProviders.cs:466,575` | User-entered IMAP/SMTP server | If configured | Same as 4; app password (DPAPI) | Password | Optional | Yes — `email = block` |
| 7 | `AppUpdates.cs` → `winget.exe` | winget / Microsoft Store sources and publishers' download URLs | Apps > Updates, Check & fix, `--update-apps`; **listing also automatic: PC health ≤ 6-hourly, Cure Agent daily (SYSTEM)** | Package queries; installer downloads (only when updating) | winget | Optional | Installing: `apps.update = block`. Listing: **not yet** (B-29) |
| 8 | `Fleet.cs` | IT's SMB share | `--report` / "Add this PC" | PC name, Windows version, disk numbers, savings, policy fingerprint | Windows (share ACL) | Optional | By not configuring |
| 9 | Browser (user click) | `bing.com`, `github.com/sponsors`, `ko-fi.com` / `buymeacoffee.com` (sponsor link, build-config only, regex-restricted, `Updates.cs:47-55`), `console.cloud.google.com`, `entra.microsoft.com`, `myapps.microsoft.com`, `account.live.com` | Only on click | Bing: folder name only | — | Optional | No (user-initiated) |
| 10 | `MpCmdRun` (Defender) | Defender cloud per Defender settings | "Scan with Microsoft Defender" | Per Defender policy | — | Optional | Defender policy |
| **11** | **`CureAgent.cs:378-399`** (Business, LocalSystem service) | **Company Cure Server** URL from `HKLM\SOFTWARE\Policies\Elixar\Cure\Server` or `HKLM\SOFTWARE\Elixar\Cure` (admin-write) | Every 15 min by default (5–1440, jittered) | Status report (see B-24): PC name, OU, model, Windows version, drives/free space, cleanable bytes per item, places, top 30 apps (+publisher, dates), startup names, disk health, policy fingerprint, company locks, errors. Enrollment: token + PC name | TLS 1.2+, optional certificate pin; enrollment token once, then HMAC-SHA256 per report with nonce/time | Optional (IT) | Yes — don't install the agent / remove the policy value |
| **12** | **`CureAlerts.cs:239`** (Business, Cure Server PC, hourly task) | The server PC's connected mailbox (Graph/Gmail/SMTP) — **to itself only** (`MailProviders.cs:248`) | When a PC newly needs attention | PC names and their problems | As 4–6 | Optional | Yes — `email = block` (re-checked each run, `CureAlerts.cs:246`) |

**Not found:** analytics, crash/error upload, WebSockets, webhooks, DNS-based callbacks, cloud-storage uploads, hard-coded IP addresses (only `127.0.0.1`/`0.0.0.0`), certificate-validation bypasses (the only custom callback is the Cure pin, `CureAgent.cs:368-376`, which still requires a valid chain when no pin is set). `Dns.GetHostEntry("")` (`CureSetup.cs:154`, `CureAlerts.cs:233`) resolves this PC's own name.

---

## D. Credential & permission exposure

| Item | Where / how | Exposure | Assessment |
|---|---|---|---|
| Elixar's privilege | Elevated on every start | Any bug is an admin bug | Inherent. Use policy rules to switch off what isn't needed |
| **Cure Agent privilege** | Service as **LocalSystem** (`CureSetup.cs`, `sc create ... obj= LocalSystem`) | Reads sizes in all profiles, disk health, installed apps; runs winget listing | Needed for all-profile measurement; read-only, no inbound port, no command channel. A bug in the agent would be a SYSTEM bug — keep it updated only through verified releases |
| **Cure Server identity** | `NT SERVICE\CureServer` virtual account; URL ACL and data folder limited to it | Low-rights | Good |
| **Cure device keys** | Agent: DPAPI **machine** scope in admin/SYSTEM-only `ProgramData\Elixar\cure\device.dat`; server: DPAPI machine in admin/SYSTEM/service-only data folder | Admin on a PC → can impersonate that PC's reports; admin on the server → all device keys (can forge any PC's reports) | Acceptable; protect the server |
| **Cure enrollment token** | Server stores SHA-256 only; the `.reg` file with the clear token is saved in the admin-only server data folder; on PCs via GPO (user-readable) or MSI property (logged) | Lets a holder enroll new (fake) PCs | B-21; rotate after deployment |
| Console access | Windows auth; local Administrators of the server **or** the configured group | Anyone in that group sees the whole inventory (B-24) | Use a dedicated small AD group |
| OAuth refresh tokens, app passwords | Admin-only folder + DPAPI CurrentUser | That mailbox only | Unchanged; block email on company PCs |
| Update signing key | GitHub secret, used in the `release` job | Signs updates for every PC (B-1, B-19) | Move manifest signing to an isolated job; environment approval |
| Code-signing credentials (SSL.com) | GitHub secrets `ES_*` incl. TOTP seed, used by a third-party action | Holder can Authenticode-sign as the publisher | Keep in the `release` environment; restrict who can run it |
| Windows/AD credentials, service-account passwords, DB strings, SSH/cloud keys | **Never read, stored or sent** | — | No exposure found. Golden image only detects such files exist (B-12) |

No authentication flow lets a user escalate: the console has no write API; the agent API accepts only signed reports for an existing device; enrollment issues a fresh random device id/key and never returns other PCs' data.

---

## E. Lateral movement risk

Elixar reaches other machines only through: the Cure Agent → Cure Server HTTPS channel, the SMB fleet share, the user's mail provider, and download endpoints. **No SSRF surface** (the server never fetches URLs), **no remote-command channel**, no AD/LDAP, SQL, SSH, RDP, WinRM or SNMP code.

| Path | Entry point | Needs | Trust boundary | Reachable | Realistic impact | Prevents it |
|---|---|---|---|---|---|---|
| E-1 Malicious signed update | Repo push access (B-20) or poisoned release tool (B-19) or signing secret | Attacker controls build/sign path **and** users accept the update | Internet → admin on each PC (SYSTEM via Cure Agent binaries) | Everything those PCs' admins can reach | **High, fleet-wide** | Branch protection, 2FA, isolated signing job, environment approval; company: `updates.check = block`, deploy only hash-verified MSIs |
| E-2 Compromised Cure Server | Admin on the server | Server compromise by other means | Server → inventory | Inventory of all PCs (names, OUs, models, apps, startup items, disk states) — **reconnaissance**, plus forging reports | Medium (information), **no command path to PCs** | Treat as a Tier-1 management server: patching, restricted admins, EDR, backups; small console group |
| E-3 Rogue enrollment | Token from GPO registry or MSI log (B-21) | Domain-network access to the server port | Unauthenticated → console data | False PCs/data in the console; enrollment DoS (B-22) | Low | Rotate token, hide MSI property, review new enrollments |
| E-4 Malicious Cure Server (DNS/server spoofing) | Attacker answers as the Cure Server | Must pass TLS (trusted cert for the name, or the pinned thumbprint) | Server → PCs | Agents send reports (inventory) to the attacker; **nothing runs on PCs** (agent ignores everything except `next`) | Low–Medium (information) | Set `Thumbprint` by GPO (pinning) |
| E-5 Fleet share poisoning | Share write access | Any domain PC | PC → IT console | False data | Low | Share ACL |
| E-6 Stolen mail token | Admin malware on a PC | Already admin | PC → mailbox | That mailbox | Medium (phishing) | `email = block`, Conditional Access |
| E-7 Elixar as proxy/pivot | — | — | — | **None** — no relay/forwarding code; Cure Server listens only for its own routes | — | — |

---

## F. Persistence assessment

| Mechanism | Created | Runs as | Removed on uninstall? |
|---|---|---|---|
| Scheduled tasks `\Elixar\`: Low space check, …at sign-in, Weekly clean, Email reports, Daily tidy, Policy cleaning, **Keep in check** (1.2.61), **Cure alerts** (1.2.60, Business) | When turned on by user, policy or rule | Program Files copy via Elixar's launcher; most "highest privileges" | **Yes — all 8** (`Setup.cs:89-93`, also via MSI custom action) |
| **Service `CureAgent`** (Business) | IT: `CureAgent.exe --install` or MSI `CUREAGENT=1` | LocalSystem, delayed-auto, restart on failure | **MSI: yes** (`--uninstall` custom action). **`Elixar.exe --uninstall`: no** (B-28) |
| **Service `CureServer`** + http.sys URL ACL + SSL binding + firewall rule (domain) + self-made certificate | IT: `CureServer.exe --install` | `NT SERVICE\CureServer` | **MSI: yes**; `--purge` deletes reports. Non-MSI: no (B-28) |
| Cure device key / settings keys (`HKLM\SOFTWARE\Elixar\Cure`, `...\CureServer`) | Install / enrollment | — | Yes with the service uninstall; `HKLM\SOFTWARE\Policies\Elixar\Cure` is IT's own GPO value |
| Program Files copy, uninstall entry, settings keys, `ProgramData\Elixar`, Undo folders, Event Log | As before | — | As before (`--purge` for data) |
| PC health ignore list (`ProgramData\Elixar\health-ignore.txt`), last score (HKCU) | 1.2.62 | — | Removed with `--purge` (data folder) / HKCU settings |
| Google / Microsoft OAuth grants | Email setup | — | Google revoked; Microsoft consent stays at Microsoft (B-8) |

**After removal, residual access:** none to company resources from Elixar itself. If the agent was enrolled, its device record stays on the Cure Server until `CureServer.exe --remove-pc` (it can't be used without the PC's DPAPI-protected key). Rotate the enrollment token when decommissioning Cure. Remove Entra consent if email was used.

No driver, browser extension, WMI subscription, Run/RunOnce entry, IFEO, Winlogon/AppInit entry or hidden account is created.

---

## G. Supply chain risk

| Component | Risk | Assessment |
|---|---|---|
| Third-party code libraries in the app | **None** — .NET Framework class libraries only; no NuGet/npm/pip in the product | Very low; dependency confusion/typosquatting not applicable to the app |
| Build (`build.ps1`) | Windows' own `csc.exe`; **no downloads** (re-checked; Business adds `/define:BUSINESS` and the Cure console resources) | Low |
| CI (`ci.yml`, `cure-e2e.yml`) | `contents: read`, SHA-pinned `actions/checkout` and `upload-artifact`, `persist-credentials: false`; secrets are not passed to fork PRs by GitHub | Low |
| **Release (`release.yml`)** | `contents: write`; secrets: update-signing key, SSL.com (incl. TOTP seed), Google/Microsoft client ids; third-party code in the same job: **WiX from nuget.org (version-pinned only)**, **SSLcom/esigner-codesign (SHA-pinned; downloads CodeSignTool at runtime)**; manifest signing happens **after** both (B-19) | **High until restructured** |
| Workflow inputs | `workflow_dispatch` `version` flows into `$env:TAG` (environment variable, not inline script) — not injectable; only repo writers can dispatch | Low |
| Repository controls | **`main` unprotected** (B-20); environment approval unverified | **High** (owner action) |
| Runtime downloads | Elixar update (signed+hashed+edition), Google `adb` (Authenticode), winget (Microsoft sources, protected winget.exe) | Adequate |
| Code signing of binaries | Workflow ready; **published v1.2.61 is unsigned** | Medium — SmartScreen warnings; no publisher identity for allow-listing |
| Dependabot | Weekly PRs for pinned Actions; nothing auto-merges | Good (review each PR's commit) |
| Commits | Made from a cloud session, shown "Unverified" | Cosmetic; release authenticity comes from the manifest signature |

---

## H. Threat scenarios

**Scenario A — External application compromise.** *Preconditions:* Elixar for Private exposes no network service (only a short-lived loopback OAuth callback). Elixar for Business exposes the **Cure Server port** (default 8443) on the **domain firewall profile**. *Path:* pre-auth surface = two POST endpoints (64 KB cap, JSON without type resolution, token/HMAC checks, rate limits) and http.sys Windows authentication for the console. *Likely impact:* no exploitable path found; worst realistic = enrollment DoS (B-22) or false data with a stolen token (B-21/B-23). *Existing controls:* TLS, HMAC, nonce/time, size limits, read-only console, CSP, low-rights service account. *Missing:* failed-attempt-aware rate limiting, duplicate-name flag. *Verify:* external scan of the server port from a non-domain network (should be closed); `netstat -abno` on PCs (no listener).

**Scenario B — Compromised dependency.** *Preconditions:* none in the app; realistic equivalents are the release-time tools (WiX package, SSL.com action and its runtime download, GitHub-hosted runner image). *Path:* tool code runs in the release job → rewrites `Elixar.zip`/`Elixar-Business.zip` and their manifests → the following step signs them (B-19). *Impact:* malicious but validly signed update to every PC that accepts it; Business update path also replaces the SYSTEM-level `CureAgent.exe`. *Existing:* SHA-pinned actions, version-pinned WiX, environment `release`. *Missing:* isolation of signing from third-party tools; hash-pinning WiX; `-NoProfile`. *Verify:* rebuild the tagged source independently and compare SHA-256 with the release's `SHA256.txt`/manifest before rollout.

**Scenario C — Stolen service credentials.** Elixar uses **no domain service accounts**. Credentials that exist: per-PC Cure device keys (forge that PC's reports), the enrollment token (enroll fake PCs), mail tokens (one mailbox), SSL.com/update-signing secrets in GitHub (sign releases — the serious one). *Impact:* none of the Elixar credentials grants access to AD, file servers, databases or other corporate services. *Recommended:* rotate the enrollment token after rollout; keep signing secrets in the `release` environment with required reviewers. *Verify:* Entra sign-in logs for the mail app ids; Cure console enrollment dates/IPs.

**Scenario D — Malicious or unauthorized update.** *Preconditions:* push access to an unprotected `main` (B-20) **or** a poisoned release tool (B-19) **or** the signing secret; then a user (or IT) accepts the update. *Existing:* RSA-3072 manifest signature, SHA-256 of zip and exe, edition check, no downgrade, size limits, explicit user confirmation, install only into admin-only Program Files, Cure files copied only from the verified update folder. *Missing:* branch protection, isolated signing job, Authenticode on published binaries, independent reproducible-build check. *Company mitigation:* `updates.check = block`; distribute only builds you have hashed (and ideally rebuilt) via MSI.

**Scenario E — Compromised host.** *E1 — a PC (attacker is admin):* gains the PC's Cure device key (forge its reports), a mail token if connected, the PC's fleet-share write access; nothing that reaches other PCs. *E2 — the Cure Server (attacker is admin):* gains the full inventory and all device keys (forge any PC's reports), the console's view; **cannot push anything to PCs** (no command channel). *Mitigations:* EDR on both; Cure Server treated as a management server (restricted admins, patching, backups, log forwarding); `Thumbprint` pinning so a different server can't receive reports.

---

## I. Recommended isolation architecture (zero-trust deployment)

1. **Distribution:** deploy the **MSI** of a build you verified (hash matches the signed release; ideally rebuilt from the tag). Hide `CURETOKEN` (B-21) or install the agent with a secured script; don't keep verbose MSI logs with the token.
2. **Organization policy** (`HKLM\SOFTWARE\Policies\Elixar`, ADMX from the release): `updates.check = block`; `email = block` (or an admin-consented Entra app + Conditional Access); `sysprep = block` and `phone = block` (automatic on joined PCs); `apps.update = block` if Intune/SCCM patch apps (and accept B-29 until fixed); `drivers.remove = block` unless needed; `pin.policy = on` on shared PCs; **when available, `wipe = block` (B-25)**.
3. **Cure Server placement:** a dedicated Windows server in the **management/server VLAN**; inbound TCP 8443 only from workstation subnets (domain profile); **no outbound internet** needed (unless email alerts are used — then only the mail provider); not internet-facing, no reverse proxy needed. Use a CA-issued certificate for its FQDN or the self-made one **plus `Thumbprint` pinning by GPO**.
4. **Cure Console access:** a dedicated small AD group (not "Domain Users"); MFA via your normal admin access path; review membership.
5. **Outbound allowlist per process** (if your firewall supports it): `Elixar.exe` — nothing when the rules above are set; `CureAgent.exe` — only the Cure Server (plus winget/Microsoft sources until B-29 is fixed); `CureServer.exe` — none.
6. **Identity:** no user consent to third-party apps in Entra; no service accounts are needed for Elixar.
7. **Logging:** forward the "Elixar" event log (Event IDs 1000–1006) and Windows Security/System logs from PCs and the Cure Server to the SIEM; alert on new services, URL ACL changes, firewall rule changes and certificate bindings on the server.
8. **EDR:** keep alerts for shadow-copy deletion, event-log clearing and mass overwrite/delete — with the rules above Elixar never does these on normal PCs.
9. **Owner (GitHub) controls — required before company-wide use:** 2FA; **branch protection on `main`**; `release` environment with **required reviewers** and the signing/SSL.com secrets moved into it; **restructure `release.yml`** (B-19); Authenticode-sign every published binary.
10. **Removal:** `msiexec /x` (removes tasks, services, URL ACL, cert binding, firewall rule); `--purge` for data; `CureServer.exe --remove-pc` for decommissioned PCs; rotate the enrollment token; remove Entra consent if email was used.

---

## J. Final security assessment

**NO BACKDOOR INDICATORS IDENTIFIED IN REVIEWED SCOPE.**

No hidden accounts, master keys, authentication bypasses, remote-control channels, undocumented listeners, vendor telemetry, obfuscated or dynamically loaded code, or behaviour inconsistent with the documented purpose were found in 1.2.62 (Private and Business). The only network service (Cure Server, Business) is documented, opt-in, inbound-to-server only, and has no command path to PCs. This is a static review plus automated tests; it does not rule out every possible backdoor and does not cover the security of the GitHub account and runners that build and publish releases — which is where the largest remaining risk lies (B-1, B-19, B-20).

---

## Production release gates

| Gate | Status |
|---|---|
| Unauthorized remote access mechanisms | None found |
| Authentication bypass with significant impact | None found |
| Unrestricted access to sensitive corporate infrastructure | None — no AD/DB/network-device/telephony code; Cure Server is an inventory, read-only |
| Confirmed unauthorized sensitive-data transmission | None. Cure reports go only to the company's server (content broader than documented: B-24) |
| High-impact credential exposure | None in the product. **Release secrets exposed to third-party tools in one job (B-19)** |
| Exploitable privilege escalation | None confirmed (B-26 is a low-impact race) |
| Uncontrolled remote command execution | None |
| Malicious/compromised dependencies | None found; release-time tools not isolated (B-19) |
| Critical trust-boundary violations | **Release pipeline (B-19) and unprotected `main` (B-20)** — supply-chain trust boundary |

**Decision: NOT APPROVED for company-wide production yet. CONDITIONALLY APPROVED for a limited pilot** (verified build deployed by MSI, policy rules from section I, Cure Server isolated, updates blocked).

Blocking items (in order):
1. **B-20** — protect `main`; confirm 2FA and `release` environment required reviewers (owner).
2. **B-19** — isolate manifest signing from WiX/SSL.com steps; `-NoProfile`; hash-pin or vendor WiX (code change in `release.yml`).
3. **Authenticode-sign** the published binaries (the workflow is ready; v1.2.61 is unsigned) — or run an internal hash-allowlisting process.
4. **B-25** — a policy rule to block Secure wipe on company PCs.
5. **B-21** — hide `CURETOKEN` in the MSI; rotate the token after rollout.
6. A **dynamic check** on a representative PC and on the Cure Server: packet capture idle/during reports/during PC health; confirm only the connections in section C appear; external scan of the Cure Server port.
7. Pilot with EDR on for 1–2 weeks.

Lower priority: B-22, B-23, B-24 (documentation and error-text paths), B-26, B-28, B-29.

Important unverified areas: GitHub account/environment settings; the GitHub-hosted runner image; SSL.com's runtime CodeSignTool download; behaviour on non-English Windows for event parsing (functional, not security); a real Kerberos/NTLM console deployment across domains.

---

## The key question

> *If this application is deployed and connected to my company's systems, what could an attacker, malicious developer, compromised dependency, or unauthorized third party potentially access, steal, modify, or control — and what specific measures are required to prevent that?*

**What Elixar can touch:** each PC it runs on, as administrator (Cure Agent: SYSTEM) — files, registry, tasks, services, installed apps, Windows settings; optionally the signed-in user's mailbox, a fleet share, and (Business) one Cure Server that collects an inventory of all enrolled PCs. It has **no code for Active Directory queries, databases, VPN, network devices, telephony or internal web apps**, uses no domain service accounts, and offers **no way to send commands to PCs**.

**Who could abuse it, and how:**
- **A malicious developer, a hijacked GitHub account, or a poisoned release tool** is the realistic top risk: a validly signed update runs as administrator (and replaces the SYSTEM agent) on every PC that accepts it. Today two controls that would stop this are missing: **`main` is unprotected (B-20)** and **signing shares a job with third-party tools (B-19)**. *Prevent:* fix both, require release approval, Authenticode-sign, and in the company block in-app updates and deploy only builds you have verified.
- **An attacker who takes over the Cure Server** learns your whole PC inventory (names, OUs, models, installed and startup software) and can forge reports — but **cannot run anything on PCs**. *Prevent:* treat it as a management server; small console group; thumbprint pinning.
- **Someone who finds the enrollment token** (GPO registry, MSI logs) can add fake PCs and false data, or briefly block enrollment. *Prevent:* hide the MSI property, rotate the token, review enrollments.
- **An insider on a company PC** can permanently destroy files with Secure wipe, which policy can't block yet. *Prevent:* add and enforce a `wipe` rule.
- **An attacker already admin on a PC** gains little extra: that PC's Cure key and possibly a mail token.
- **An outside network attacker** has no entry point on PCs; the Cure Server port should be reachable only from workstation subnets.

**Required measures:** the 7 blocking items above and the architecture in section I.

---

### History of fixes from these assessments

| Version | Fix |
|---|---|
| 1.2.53 | Logs admin-only; `updates.check` rule; winget Microsoft sources only; `adb` stopped on exit; `--purge`; Microsoft consent guidance; Actions pinned, read-only CI token, `release` environment |
| 1.2.54–1.2.55 | Company-PC locks (Phone, Sysprep, Email); ADMX; admin-only Event Log; LICENSE, SECURITY.md, Dependabot; every email/Sysprep/phone action re-checks policy |
| 1.2.56 | B-13 (`persist-credentials: false`); B-16 (Event Log ACL applied exactly) |
| 1.2.57 | B-17 (TrustedInstaller SID); B-18 (services only with `--service`); Cure end-to-end test on Windows |
| 1.2.58 | PowerShell: no `-EncodedCommand`, no `Bypass`, `RemoteSigned`, plain text |
| 1.2.61 | Growing fast / Keep in check security review: findings per-user, rules admin-only with their own elevated task, installed-apps-only ownership, path re-checks, link-safe deletes, root validation of the findings file |
| 1.2.62 | **B-27** Check & fix obeys `apps.update` / `startup.change`; PC health read-only script, admin-only ignore list, Secure Boot not ignorable, Bitdefender/Defender name confusion fixed, winget listing at most 6-hourly from the Dashboard |
