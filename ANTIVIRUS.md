# When an antivirus blocks Elixar

Elixar is a cleanup tool. It deletes files, makes restore points, changes Windows settings, and in Elixar for Business it runs Windows services. Antivirus programs watch for exactly these actions, so a new, unsigned version can be flagged as a false positive. Typical names are `Trojan:Win32/Wacatac.B!ml` and `Program:Win32/Uwamson.A!ml`. The `!ml` ending means the flag came from a machine-learning guess, not from a known threat.

## 1. Check that the file is really Elixar

Every release on GitHub comes with `SHA256.txt`. In PowerShell:

```powershell
Get-FileHash .\Elixar.exe -Algorithm SHA256
```

The hash must match the line for `Elixar.exe`. If it doesn't, delete the file and download it again from the GitHub release page only.

## 2. Report the false positive

| Antivirus | Where |
|---|---|
| Microsoft Defender | https://www.microsoft.com/en-us/wdsi/filesubmission (choose **Software developer** if you publish Elixar, otherwise **Home customer** or **Enterprise customer**) |
| Any other antivirus | Its "report a false positive" page. To see which antivirus programs flag the file, check it at https://www.virustotal.com |

Microsoft usually clears a false positive within 1–3 days, and then updates every PC through Defender's normal updates.

## 3. Until it's cleared

- **Home:** allow only that one file in your antivirus. For Defender: Windows Security > Virus & threat protection > Protection history > the Elixar entry > Actions > Allow. Never turn the antivirus off, and never exclude a whole folder such as Downloads.
- **Company:** allow it by its SHA-256 hash, or by its signer once releases are code-signed. For Defender for Endpoint: Settings > Endpoints > Indicators > File hash > Allow.

## What Elixar does to avoid false alarms

- PowerShell gets its script as plain, readable text. Elixar never uses `-EncodedCommand` or `-ExecutionPolicy Bypass`, which is how malware hides what it runs.
- Event logs and restore points (golden-image cleanup only, when you choose it) are cleared through Windows' own interfaces, not with the `vssadmin` / `wevtutil` command lines that ransomware uses.
- Nothing is packed or obfuscated. The source of every release is public, and the exe carries its publisher and version details.
- A self-test checks every build for these patterns.

## For the publisher: the lasting fix

1. **Code-sign every release.** `release.yml` signs all programs once the `CODESIGN_PFX` and `CODESIGN_PASSWORD` secrets are set. Certificates bought since June 2023 are kept on a hardware token or a cloud signing service, so they can't be exported as a .pfx. Signing then needs the certificate authority's cloud-signing step added to `release.yml`.
2. **Submit each new release** to Microsoft as **Software developer**, right after publishing it.
3. Signed releases build a reputation with SmartScreen and antivirus programs over time, and false alarms become rare.
