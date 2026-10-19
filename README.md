# Elixar

The cure for a cluttered PC. Made by Elisar.

Elixar shows what fills drive C:, from Critical to Safe, and removes only what you choose: caches, temp files,
old drivers, Windows.old, apps you don't use, phone clutter and more, with Undo for anything risky.
The full user guide is in `guide/UserGuide.html` (also built into the app: press F1).

## Build

On any Windows 10/11 PC: `powershell -ExecutionPolicy Bypass -File build.ps1 -SelfTest`
(uses the C# compiler that comes with Windows; the result is `out\Elixar.exe`).

## Release checklist (in this order)

1. **Put the code on GitHub** (a new repository, then push this folder). From then on every push is built and
   self-tested on a real Windows machine; the report is attached to each run under *Actions*.
2. **Signed updates**: run `tools\new-signing-key.ps1`, commit `update-key.pub.xml`, add the `ELIXAR_SIGNING_KEY` secret.
3. **One-click email sign-in**: add `ELIXAR_GOOGLE_ID` / `ELIXAR_GOOGLE_SECRET` and `ELIXAR_MS_ID` (see below).
4. **No blue SmartScreen box**: buy a code-signing certificate and add `CODESIGN_PFX` / `CODESIGN_PASSWORD`.
   (An OV certificate from a public CA; Microsoft's Trusted Signing service also works with the signing step.)
5. **Release**: change `AssemblyVersion` in `src/CDriveCleaner.cs`, then push a tag like `v2.2.2`.
   The release workflow builds, tests, signs, packages and publishes; Elixar's *Check for updates* finds it.

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

### Optional: "Sign in with Google" without setup for everyone
Email reports need a Google "Desktop app" client. Without one, each person sets it up once in Elixar (Settings > Email reports).
To build it in, do the steps from Elixar's setup window once in your Google account, then add two repository secrets:
`ELIXAR_GOOGLE_ID` (the client ID, ending in `.apps.googleusercontent.com`) and `ELIXAR_GOOGLE_SECRET`.
Google treats Desktop-app clients as public, so this is safe to ship; Elixar only ever asks for the "send email" permission.
To remove Google's "unverified app" warning, submit the app for verification in Google Auth Platform (needs a privacy policy page).

### Optional: "Sign in with Microsoft" without setup for everyone
Register Elixar once at entra.microsoft.com (personal and work accounts, public client, redirect `http://localhost`,
delegated Mail.Send and Mail.ReadWrite) and add the repository secret `ELIXAR_MS_ID` (the Application ID; no secret needed).

### Optional: code signing

With a code-signing certificate (.pfx), add the secrets `CODESIGN_PFX` (base64 of the file) and `CODESIGN_PASSWORD`.
Releases are then signed and Windows stops showing "Windows protected your PC".
