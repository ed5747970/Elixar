# Elixar

The cure for a full C: drive. Made by Elisar.

Elixar shows what fills drive C:, from Critical to Safe, and removes only what you choose: caches, temp files,
old drivers, Windows.old, apps you don't use, phone clutter and more, with Undo for anything risky.
The full user guide is in `guide/UserGuide.html` (also built into the app: press F1).

## Build

On any Windows 10/11 PC: `powershell -ExecutionPolicy Bypass -File build.ps1 -SelfTest`
(uses the C# compiler that comes with Windows; the result is `out\Elixar.exe`).

## Automatic testing and releases (GitHub Actions)

- **Every push** builds Elixar on a real Windows machine and runs the full self-test as administrator
  (`.github/workflows/ci.yml`). The report is kept with each run.
- **Every tag** like `v1.8.0` builds, tests, packages and publishes a release (`.github/workflows/release.yml`).
  Elixar's *Check for updates* (Settings) finds it.

### One-time setup for signed updates

1. On your PC: `powershell -ExecutionPolicy Bypass -File tools\new-signing-key.ps1`
2. Commit `update-key.pub.xml`.
3. GitHub > Settings > Secrets and variables > Actions > New secret `ELIXAR_SIGNING_KEY` = the private key file
   from your Desktop. Keep a backup somewhere safe and delete the Desktop copy.

Elixar installs an update only when its manifest is signed with that key and every file matches its SHA-256.

### Optional: code signing

With a code-signing certificate (.pfx), add the secrets `CODESIGN_PFX` (base64 of the file) and `CODESIGN_PASSWORD`.
Releases are then signed and Windows stops showing "Windows protected your PC".
