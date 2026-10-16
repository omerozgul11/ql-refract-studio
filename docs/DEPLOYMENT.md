# Deployment

This document covers packaging the Windows desktop application, code signing, update manifests and
the release checklist. The licensing back end (licence server, admin portal, PostgreSQL) is
deployed separately; see [licensing/LICENSE-SERVER.md](licensing/LICENSE-SERVER.md) and
[licensing/ADMIN-PORTAL.md](licensing/ADMIN-PORTAL.md). Build prerequisites: [BUILD.md](BUILD.md).

## 1. Overview

```
dotnet publish (win-x64, self-contained)  ->  artifacts/publish/win-x64/
third-party notices check                  ->  fails the build if a package is not listed
Inno Setup (ISCC)                          ->  artifacts/installer/RefractionStudio-<ver>-win-x64-setup.exe
SHA-256                                    ->  artifacts/installer/RefractionStudio-<ver>-win-x64-setup.exe.sha256
(optional) signed update manifest          ->  artifacts/installer/release-<channel>.json
```

All of this is driven by `packaging/package_windows.py`. `artifacts/` is listed in `.gitignore`.

## 2. `packaging/package_windows.py`

```bash
python3 packaging/package_windows.py --version 1.0.0
python3 packaging/package_windows.py --version 1.0.1 --sign \
        --manifest-key /secure/release-signing.private.pem \
        --download-url https://…/RefractionStudio-1.0.1-win-x64-setup.exe --notes "…" [--channel beta]
```

| Option | Meaning |
|---|---|
| `--version` (required) | product version; passed to `dotnet publish` (`-p:Version=`) and to Inno Setup (`/DAppVersion=`) |
| `--skip-publish` | reuse `artifacts/publish/win-x64` (for example after signing the published files) |
| `--skip-installer` | stop after publish and the notices check |
| `--iscc PATH`, `--wine PATH` | explicit Inno Setup compiler / Wine binary |
| `--sign` | passes `/DSignTool=1` to ISCC (see §5) |
| `--manifest-key PATH` | release-signing private key (PEM); writes and signs an update manifest |
| `--download-url URL` | required with `--manifest-key`; public HTTPS URL of the installer |
| `--channel` | manifest channel, default `stable` |
| `--notes` | release notes for the manifest |

Steps performed:

1. **Publish.** Deletes `artifacts/publish/win-x64`, then runs
   `dotnet publish src/RefractionStudio.App/RefractionStudio.App.csproj -c Release -r win-x64 --self-contained true -p:Version=<v> -p:DebugType=none -p:DebugSymbols=false -p:PublishReadyToRun=false`
   and checks that `RefractionStudio.exe` exists. The application is self-contained: the .NET runtime
   is bundled and nothing needs to be installed on the target computer.
2. **Third-party notices check.** Reads `RefractionStudio.deps.json` and requires that every NuGet
   package (type `package`, except `runtimepack.*`) is named in
   `packaging/windows/THIRD-PARTY-NOTICES.txt`; otherwise it stops with
   `THIRD-PARTY-NOTICES.txt does not cover: …`. When a dependency is added, add its name, licence and
   source to the notices file (and the licence text if it is a new licence).
3. **Installer.** Runs ISCC with `/Qp /DAppVersion=… /DSourceDir=… /DOutputDir=…` on
   `packaging/windows/RefractionStudio.iss`. On Windows ISCC is taken from
   `%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe` unless `--iscc` is given. On Linux it runs through Wine
   with Windows paths (`Z:\…`) and the private toolchain from §3.
4. **Checksum.** Writes `<installer>.sha256` (`<hex>  <file name>`) and prints the digest.
5. **Manifest (optional).** With `--manifest-key`, writes `release-<channel>.unsigned.json`
   (`product` `refraction-studio`, `version`, `channel`, `releasedAt` = now in UTC, `downloadUrl`,
   `sha256`, `size`, `releaseNotes`, `major: false`, `minimumVersion: null`) and signs it with
   `tools/RefractionStudio.Cli sign-manifest` into `release-<channel>.json`.

### Release settings in `.env`

Settings that must not be published are read from the repository's `.env` (gitignored; copy
[`.env.example`](../.env.example)). Values already present in the environment take precedence.

| Variable | Used for |
|---|---|
| `RS_RELEASE_SIGNING_KEY` | default for `--manifest-key`: path of the release-signing private key (keep it in `.secrets/`) |
| `RS_DOWNLOAD_BASE_URL` | default for `--download-url`: `<base>/v<version>/RefractionStudio-<version>-win-x64-setup.exe` |
| `RS_SIGNTOOL_COMMAND` | with `--sign`: passed to ISCC as `/Srssign=<command>` (certificate selection, timestamp server) |

The licence server's secrets are separate: `server/.env` and `server/secrets/` (see
[licensing/LICENSE-SERVER.md](licensing/LICENSE-SERVER.md)).

## 3. Building the installer on Linux: `packaging/windows-toolchain.sh`

The script sets up a private, user-level toolchain under `.tools/windows-build` (nothing is installed
system-wide; delete the folder to undo):

* `root/` — Wine extracted with `apt-get download` + `dpkg-deb -x` from the distribution packages
  `libwine wine64 wine-common libz-mingw-w64 libmspack0t64 cabextract libtalloc2` (no root rights).
  The MinGW `zlib1.dll` is copied next to the Wine DLLs and into the prefix.
* `prefix/` — a Wine prefix configured for headless use: the registry value
  `HKCU\Software\Wine\Drivers\Graphics = null` selects Wine's **null graphics driver**, so no X server
  is needed (the Inno Setup installer creates windows even when silent). `DISPLAY` and
  `WAYLAND_DISPLAY` are unset, `WINEDEBUG=-all`, and `mscoree`, `mshtml` and `winemenubuilder.exe`
  are disabled through `WINEDLLOVERRIDES`.
* `inno/` — Inno Setup 6.7.3, downloaded from the jrsoftware GitHub release and verified against a
  pinned SHA-256 (`9c73c3ba…b732`) before it is installed silently into the prefix
  (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /NOICONS`).

```bash
packaging/windows-toolchain.sh
python3 packaging/package_windows.py --version 1.0.0
```

`package_windows.py` finds `.tools/windows-build/root/usr/lib/x86_64-linux-gnu/wine/wine` and
`.tools/windows-build/inno/ISCC.exe` automatically and sets the same environment (prefix, library
paths, private XDG folders).

**Limitation found on the build host.** The installer built this way installs under Wine (a silent
per-user install into `C:\users\<user>\AppData\Local\Programs\QuakeLogic\Refraction Studio` completed
with "Installation process succeeded"), but the installed application could not be started under
Wine on the build host: the start failed with `FileNotFoundException` for `System.Runtime.dll`, and a
plain .NET 10 console application also hangs under the same Wine. Wine is therefore only used to
*compile* the installer. Installation, start-up, file association, upgrade and uninstallation must be
verified on real Windows 10/11 (see the checklist in §9).

## 4. The installer: `packaging/windows/RefractionStudio.iss`

Requires Inno Setup 6.3 or later. Key settings:

| Setting | Value / behaviour |
|---|---|
| `AppId` | `{FF95C27A-6EC2-48D1-970F-23E8A9B33E4E}` — identifies the product for upgrades and uninstall; never change it |
| Install folder | `{autopf}\QuakeLogic\Refraction Studio` (Program Files for all users, `%LocalAppData%\Programs` for a per-user install) |
| Install mode | `PrivilegesRequired=admin` with `PrivilegesRequiredOverridesAllowed=dialog`: per-machine by default, and the user can choose a per-user install (no administrator rights) in a dialog |
| Platform | `ArchitecturesAllowed=x64compatible`, 64-bit mode, `MinVersion=10.0.17763` (Windows 10 version 1809 or later) |
| Licence page | `LicenseFile=EULA.txt` (`packaging/windows/EULA.txt`) |
| Output | `RefractionStudio-<AppVersion>-win-x64-setup.exe`, `lzma2/max` solid compression, modern wizard |
| Running instances | `CloseApplications=yes`, `RestartApplications=yes` |
| Logging | `SetupLogging=yes` (Inno Setup writes a setup log in the temporary folder) |
| Signing | only when compiled with `/DSignTool=1`: `SignTool=rssign`, `SignedUninstaller=yes` |

What the installer does:

* **Files.** Copies the published application (`SourceDir\*`, recursively) into the install folder,
  the sample project `samples/Refraction-Studio-Sample.rfsx` into `{app}\Samples`, and `EULA.txt` and
  `THIRD-PARTY-NOTICES.txt` into `{app}`.
* **Shortcuts.** A Start-menu entry "Refraction Studio" (`{autoprograms}`), and an optional desktop
  icon (task `desktopicon`, unchecked by default). `DisableProgramGroupPage=yes`.
* **File association** (task `associate`, checked by default). Registers `.rfsx` with the ProgID
  `QuakeLogic.RefractionStudio.Project` ("Refraction Studio project"), the application icon, and the
  open command `"{app}\RefractionStudio.exe" "%1"`, under `HKA\Software\Classes` (HKLM for a
  per-machine install, HKCU for a per-user install). `ChangesAssociations=yes` notifies the shell.
  The application opens a `.rfsx` passed on the command line after start-up.
* **Post-install.** Offers to launch the application (`postinstall`, skipped in silent mode).
* **Uninstall.** Removes the program files and `{app}\Samples` and the association keys. User data is
  **kept**: projects in `Documents\Refraction Studio`, and settings, licence, logs, recovery files and
  downloaded updates in `%LocalAppData%\QuakeLogic\RefractionStudio`. After a non-silent uninstall a
  message tells the user where these were kept. To remove a computer's licence seat, deactivate it in
  the application before uninstalling (the uninstaller does not contact the licence server).

The installer's sample is opened by the start page ("Explore the sample") from
`{app}\Samples\Refraction-Studio-Sample.rfsx`; it is copied into the user's documents first, so the
installed copy is never modified.

## 5. Code signing

Unsigned installers are for testing only. The application's updater refuses an update whose
installer does not carry a trusted Authenticode signature (§7), so every installer published to the
update feed must be signed.

Signing is configured on the Windows build machine that holds the code-signing certificate (or its
hardware token / cloud signing client); no certificate or password is stored in this repository.

1. Install the Windows SDK (for `signtool.exe`) and make the code-signing certificate available to
   SignTool on the build machine.
2. In Inno Setup, define a sign tool named **`rssign`** (Compiler IDE: *Tools → Configure Sign
   Tools…*). Its command is a SignTool invocation in which Inno Setup replaces `$f` with the file to
   sign, for example:

   ```
   "<path to>\signtool.exe" sign /fd sha256 /tr <RFC 3161 timestamp URL of your CA> /td sha256 <certificate selection options> $f
   ```

   Use the certificate-selection options appropriate to how your certificate is installed (for
   example `/sha1 <thumbprint>` for a certificate in the store). When ISCC is invoked by hand, the same
   definition can be given on the command line as `/Srssign=<command>`; `package_windows.py` passes
   `RS_SIGNTOOL_COMMAND` from `.env` this way, and otherwise the definition configured in Inno Setup is used.
3. Build with `--sign`. The script passes `/DSignTool=1`, which enables `SignTool=rssign` and
   `SignedUninstaller=yes`: Inno Setup signs the application files it packages, `Setup.exe` and the
   uninstaller.
4. Verify: `signtool verify /pa /v artifacts\installer\RefractionStudio-<ver>-win-x64-setup.exe`.

With `--sign` the installer build also signs the published application files (`RefractionStudio.exe`
and the DLLs) with the same `rssign` tool, so the executable that users start carries the publisher's
signature as well as the installer. Without `--sign` nothing is signed.

## 6. Update manifests and the release key

The update feed is served by the licence server: `GET {licence server}/api/updates/refraction-studio/{channel}?current=<version>`
returns either **204 No Content** (up to date) or a `SignedManifest` `{ payload, signature, keyId }`,
where `payload` is base64url JSON of `ReleaseInfo` (`product, version, channel, releasedAt,
downloadUrl, sha256, size, releaseNotes, major, minimumVersion`; `src/RefractionStudio.Licensing/Contracts.cs`)
and `signature` is an ES256 signature of the payload bytes (`ManifestSigning`,
`src/RefractionStudio.Licensing/Crypto.cs`). Releases are entered in the admin portal (*Software
releases*); the server signs manifests per request with its configured release key
(`Licensing__ReleaseSigningKeyPem` / `…Path`). See LICENSE-SERVER.md §2 (`GET /api/updates/…`) and §3
(*Signing keys*), and ADMIN-PORTAL.md (*Software releases*).

The **release key** is an ES256 (P-256) key pair separate from the licence-token key:

```bash
dotnet run --project tools/RefractionStudio.Cli -- keys --out <secure folder>
# -> signing-<kid>.private.pem (mode 0600 on Linux) and signing-<kid>.public.pem
```

* The **public** key is embedded in the application as
  `src/RefractionStudio.Infrastructure/Keys/release-public.pem` (`AppInfo.ReleasePublicKeys`). The
  licence-token public key is `Keys/license-public.pem`.
* The **private** key goes only to the licence server's secret store and, if manifests are signed
  locally, to the release machine. It is never committed or shipped. On the build host the key pairs
  are kept in `.secrets/release-signing/` and `.secrets/license-signing/`; `.secrets/` is in
  `.gitignore`.

`sign-manifest` signs a `ReleaseInfo` JSON file locally:

```bash
dotnet run --project tools/RefractionStudio.Cli -- sign-manifest release.unsigned.json --key release.private.pem --out release.json
```

It prints the product, version, channel and key id. `package_windows.py --manifest-key` uses it to
produce `artifacts/installer/release-<channel>.json`. The licence server does not read this file (it
signs from its database), so the locally signed manifest serves as a release record and as a check
that the private key in hand matches the embedded public key (its `keyId` must equal the key id that
`keys` printed for the pair whose public half is embedded).

Key rotation (current + next key trusted by the desktop, then switch the server) is described in
LICENSE-SERVER.md §3. `AppInfo.ReleasePublicKeys` currently contains one key; trusting a second key
requires adding it to that list in `AppEnvironment.cs` and shipping a release.

## 7. How the application updates itself

Implemented in `src/RefractionStudio.Infrastructure/Updater.cs` (`UpdateService`) and
`src/RefractionStudio.App/ViewModels/StartPageViewModel.cs`:

1. **Check.** The update mode (Settings → Updates) decides when: **Manual** — only when the user
   asks (start page *Check for updates*, or *Settings → Updates → Check now*); **NotifyOnly** — also at
   start-up, and an available update is shown on the start page without downloading anything;
   **Automatic** — also at start-up, and the application offers to download and install it. The request goes to
   `api/updates/refraction-studio/<channel>?current=<version>` relative to the configured licence
   server URL (setting *Licence server*, default `https://licensing.quakelogic.net/`; the HTTP client is
   created at start-up, so a changed URL applies after a restart), with the user-agent
   `RefractionStudio/<version>`. Channels: `stable` (default) or `beta`.
2. **Verify the manifest.** The ES256 signature must verify against an embedded release public key;
   otherwise the offer is ignored ("its signature could not be verified"). The release must be for
   product `refraction-studio`, newer than the running version (SemVer 2.0 precedence, as on the
   server), and its `downloadUrl` must be HTTPS. If the release's `minimumVersion` is newer than the
   installed version, the application does not update in place and tells the user to download the full
   installer instead.
3. **Confirm.** Nothing is downloaded or installed without the user's confirmation ("Download and
   install" / "Later"); a release flagged `major` asks the user to read the release notes. The open
   project must be closed (with the usual save prompt) first.
4. **Download and verify.** The installer is downloaded to
   `%LocalAppData%\QuakeLogic\RefractionStudio\Updates\RefractionStudio-<version>-setup.exe` (via a
   `.part` file). Its SHA-256 must equal the manifest's `sha256`, otherwise the file is deleted. On
   Windows its Authenticode signature is then checked with `WinVerifyTrust`
   (`WINTRUST_ACTION_GENERIC_VERIFY_V2`, no UI, whole-chain revocation check excluding the root);
   an untrusted file is deleted.
5. **Install.** On Windows the installer is started with
   **`/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SP-`** (silent with a progress window, close and
   restart running instances, no "This will install…" prompt) and the application shuts down. On
   other platforms the verified installer is only saved and its path shown.

Observations from the code (relevant when publishing releases):

* Set `minimumVersion` in the admin portal when a release cannot be installed over older versions;
  users of those versions are then directed to the full installer.
* The silent upgrade does not pass `/ALLUSERS` or `/CURRENTUSER`; verify on Windows that upgrades of
  both per-machine and per-user installations behave as intended (a per-machine upgrade needs
  elevation).

## 8. Licence server

The licence server, admin portal and PostgreSQL are deployed with Docker Compose
(`server/docker-compose.yml`) behind a TLS reverse proxy; secrets (token signing key, release signing
key, key pepper, SMTP and Stripe secrets) are provided as Docker secrets / environment variables.
Everything — configuration, secrets, migrations, backups and key rotation — is described in
[licensing/LICENSE-SERVER.md](licensing/LICENSE-SERVER.md); portal use in
[licensing/ADMIN-PORTAL.md](licensing/ADMIN-PORTAL.md).

## 9. Release checklist

Before building:

- [ ] Version decided (SemVer); `--version` is the only place it needs to be given for the installer.
- [ ] `dotnet build RefractionStudio.sln -c Release` with 0 warnings (warnings are errors).
- [ ] All test projects pass, including `RefractionStudio.Server.Tests` (Docker) — [TESTING.md](TESTING.md).
- [ ] Scientific validation run and reviewed; report committed in `docs/validation/` —
      [SCIENTIFIC-VALIDATION.md](SCIENTIFIC-VALIDATION.md).
- [ ] Sample project regenerated if the format or the sample changed ([BUILD.md §7](BUILD.md#7-regenerating-the-sample-project)).
- [ ] `docs/user-guide/USER-GUIDE.md` matches the release (it is the in-application help).
- [ ] Embedded public keys (`src/RefractionStudio.Infrastructure/Keys/`) match the server's licence and
      release private keys (compare key ids).

Build and sign (Windows build machine with the certificate):

- [ ] `python3 packaging/package_windows.py --version X.Y.Z --sign` (notices check passes).
- [ ] `signtool verify /pa` succeeds for the installer and for the installed `RefractionStudio.exe`.
- [ ] Record the SHA-256 from `<installer>.sha256`.

Verify on clean Windows 10 and Windows 11 machines (cannot be done under Wine — §3):

- [ ] Per-machine install and per-user install; Start-menu shortcut; optional desktop icon.
- [ ] Application starts; licence activation / trial; sample opens from the start page.
- [ ] Double-clicking a `.rfsx` opens it in Refraction Studio.
- [ ] Upgrade from the previous release through the in-app updater (beta or staging feed first):
      signature, checksum and Authenticode checks pass; silent upgrade closes and restarts the app.
- [ ] Uninstall keeps `Documents\Refraction Studio` and `%LocalAppData%\QuakeLogic\RefractionStudio`.

Publish:

- [ ] Upload the installer to its public HTTPS download URL.
- [ ] Admin portal: create the release (version, channel, date, download URL, SHA-256, size, notes,
      major flag, minimum version) as a draft, check it, then publish it.
- [ ] Check the feed from a client (`Check for updates`) on the intended channel.
- [ ] Keep the release key and the code-signing credentials in the secret store only.
