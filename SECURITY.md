# Security policy

Elixar runs with administrator rights, so security reports matter a lot. Thank you for helping.

## Reporting a vulnerability

Please **don't** open a public issue for a security problem.
Use GitHub's private report instead: **Security** tab > **Report a vulnerability**
(https://github.com/ed5747970/Elixar/security/advisories/new).

Include what you found, the Elixar version (Settings shows it), the steps to reproduce it,
and what an attacker could do with it. You'll get an answer within 7 days.

## Supported versions

Only the latest release gets security fixes. Elixar's "Check for updates" installs it
(the update must be signed with Elixar's release key, or it is refused).

## What Elixar does to stay safe

- No telemetry, no listeners on the network, no third-party code libraries.
- Updates are checked with an RSA-3072 signature and SHA-256 hashes before anything runs.
- Logs and email sign-ins are in folders only administrators can open.
- On company PCs (joined to a domain or Microsoft Entra ID) the Phone, Sysprep and Email tools are off unless IT allows them.
- The full review: [audit-reports/BACKDOOR_AND_COMPROMISE_ASSESSMENT.md](audit-reports/BACKDOOR_AND_COMPROMISE_ASSESSMENT.md).
