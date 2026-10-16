# Security (desktop application)

This document covers the security design of the Refraction Studio desktop application: the threats
it addresses, and its controls for licensing, device fingerprinting, local licence storage, updates,
diagnostics and secrets.

The licence server and admin portal (TLS, rate limiting, licence-key hashing, Stripe webhook
verification, admin authentication, audit log) are covered in
[licensing/LICENSE-SERVER.md](licensing/LICENSE-SERVER.md) and
[licensing/ADMIN-PORTAL.md](licensing/ADMIN-PORTAL.md).

Source files:

| Area | File |
|---|---|
| Token format, ES256, manifest signing, licence keys | `src/RefractionStudio.Licensing/Crypto.cs` |
| Claims and API contracts | `src/RefractionStudio.Licensing/Contracts.cs` |
| Device fingerprint | `src/RefractionStudio.Licensing/Fingerprint.cs` |
| Licence evaluation, encrypted store, server client | `src/RefractionStudio.Licensing/LicenseManager.cs` |
| Product identity, embedded public keys, paths, settings | `src/RefractionStudio.Infrastructure/AppEnvironment.cs` |
| Update check, download verification, Authenticode | `src/RefractionStudio.Infrastructure/Updater.cs` |
| Logging, scrubbing, crash reports, diagnostic package | `src/RefractionStudio.Infrastructure/Diagnostics.cs` |
| Embedded public keys | `src/RefractionStudio.Infrastructure/Keys/license-public.pem`, `release-public.pem` |
| Key generation and manifest signing (CLI) | `tools/RefractionStudio.Cli/Signing.cs` |
| App wiring (licence service, HTTP clients, edit gating) | `src/RefractionStudio.App/Services/Services.cs`, `src/RefractionStudio.App/ViewModels/ViewModelBase.cs` |

---

## 1. Threat model

| Asset | Threat | Control (section) |
|---|---|---|
| Licence entitlement | Forged or edited licence or trial token | ES256 signature checked against the embedded public key; product and device must match (2) |
| Licence entitlement | Copying the licence to another computer | Token bound to the device fingerprint; local store encrypted with a key derived from the fingerprint (3, 4) |
| Licence entitlement | Setting the system clock back to extend a trial or offline period | Clock-rollback guard on the stored last-seen time (5) |
| Licence entitlement | Staying offline indefinitely | Offline window `offlineValidUntil` in the signed token (5) |
| User privacy | Hardware identifiers or personal data sent to the server | Only salted hashes are sent; the machine name only with consent (3) |
| User privacy | Licence keys, tokens or e-mail addresses in support material | Crash reports, logs and settings are scrubbed in diagnostics; nothing is uploaded automatically (8) |
| Project data | Data loss caused by a licensing problem | A licence problem only disables editing; projects can always be opened and viewed (6) |
| Code integrity | Malicious or tampered update | Signed manifest, HTTPS download URL, SHA-256 check, Authenticode check, user confirmation (7) |
| Signing keys | Private keys leaking through the repository or the installer | `.secrets/` is gitignored; only public keys are embedded (9) |

Out of scope for the desktop client: an attacker with administrator rights on the licensed computer
who modifies the application binaries. The client does not try to prevent binary patching. The server
is authoritative for activations, seat counts and trials.

---

## 2. Licence tokens

Only the licence server issues trial and licence tokens. The desktop application verifies them and
never signs them.

Format (`LicenseToken`, `Crypto.cs`):

- A JWS in compact serialisation: `base64url(header).base64url(claims).base64url(signature)`, base64url
  without padding (RFC 4648 §5).
- Header: `{"alg":"ES256","typ":"RSLIC","kid":…}`.
- Claims: `LicenseClaims` (`Contracts.cs`): token id, kind (trial or licence), licence id, product,
  edition, licensed-to, e-mail, key hint, device hash, issued-at, expiry, maintenance end,
  `offlineValidUntil`, trial start and end, features, and seats.

Algorithm: ECDSA P-256 with SHA-256 (ES256); signatures use the IEEE P1363 fixed-field format (`Es256`).

`LicenseToken.Verify` rejects a token that:

- does not have three parts;
- has an `alg` other than `ES256` (Malformed — this includes `alg: none`);
- has a `typ` other than `RSLIC` (WrongType);
- has a signature that does not verify against any trusted public key (BadSignature);
- has claims that cannot be parsed.

`LicenseManager.Verify` also requires the claims to name this product, and their `DeviceHash` to equal
this computer's fingerprint.

Trusted keys: `AppInfo.LicensePublicKeys` is read from the embedded resource `Keys/license-public.pem`.
`LicensingOptions.TrustedPublicKeys` accepts a list so keys can be rotated (see "Key rotation" in
LICENSE-SERVER.md); the current build embeds one key.

Key id: `Es256.KeyId` is the first 16 hex characters of the SHA-256 of the public key in
SubjectPublicKeyInfo DER form.

`LicenseToken.PeekUnverified` reads claims without verifying them. Its code comment limits it to
display and diagnostics; no licence decision depends on it.

### Licence key format and check character

`LicenseKeys` (`Crypto.cs`):

- Format: `RFST-XXXX-XXXX-XXXX-XXXX`. The body is 15 random Crockford base32 characters (alphabet
  `0123456789ABCDEFGHJKMNPQRSTVWXYZ`), 75 bits from `RandomNumberGenerator`, followed by one check
  character.
- Check character: `Σ vᵢ·(i + 1) mod 31`, where vᵢ is the base-32 value of body character i. Because 31
  is prime, the check catches every single-character substitution and every swap of adjacent
  characters, except where the two characters differ by 31 (only "0" and "Z"). The check value is
  never "Z", so any error in the check character itself is also caught.
- Normalisation (`Normalize`): upper case, non-alphanumeric characters removed, optional `RFST` prefix
  stripped, O read as 0, I and L read as 1.

A key that fails the check is rejected on the computer before any network call
(`LicenseManager.ActivateAsync`).

Storage: the client never stores the full licence key. Tokens carry only a key hint (the last four
characters), and the licence panel shows `RFST-XXXX-XXXX-XXXX-<hint>`. On the server, keys are stored
as HMAC-SHA-256 with a server pepper (`LicenseKeys.Hash`; see LICENSE-SERVER.md).

---

## 3. Device fingerprint

`DeviceFingerprint` (`Fingerprint.cs`).

Identifiers read:

- On Windows (`WindowsIdentifierSource`): the registry `MachineGuid`; the SMBIOS system UUID (ignored
  if all zero or all FF); the baseboard manufacturer and product; the serial number of the Windows
  system volume.
- On Linux and macOS (`UnixIdentifierSource`, used for development): `/etc/machine-id` (or
  `/var/lib/dbus/machine-id`); the DMI product UUID; the board vendor and name.

MAC and IP addresses are not used. Each value is trimmed, has surrounding braces removed and is
converted to upper case; empty values are dropped.

Fingerprint: `SHA-256(salt | id₁ | id₂)` over the first two identifiers read, as lower-case hex. The
salt is `AppInfo.FingerprintSalt` = `QuakeLogic.RefractionStudio.v1`. It is not secret; it only makes
fingerprints specific to this product.

Component hashes: each identifier is also hashed on its own as `SHA-256(salt | name | value)`. These let
the server recognise a computer after a single hardware change (per LICENSE-SERVER.md it accepts an
equal fingerprint or at least two shared component hashes).

If no identifier can be read, the application creates a random per-installation id in
`%LocalAppData%\QuakeLogic\RefractionStudio\installation-id` and uses it as the only identifier
(`LicenseService.Create`, `src/RefractionStudio.App/Services/Services.cs`).

What is sent to the server (`DeviceInfo`):

- the fingerprint and the component hashes;
- the OS description and architecture;
- the computer name, only if the user turned on "send machine name" (`UserSettings.SendMachineName`,
  off by default).

Raw identifiers never leave the computer. Every request also carries the product code and application
version. Activation also sends the licence key and, if the user typed one, an e-mail address. The
HTTP user-agent is `RefractionStudio/{version}`.

---

## 4. Local licence store

`FileLicenseStore` (`LicenseManager.cs`) keeps a `StoredLicense` in
`%LocalAppData%\QuakeLogic\RefractionStudio\license.dat`: the token, the time of the last online
validation, the latest clock time seen (`LastSeen`), and any final state reported by the server
(revoked, expired, activation limit).

Protection:

- AES-256-GCM with a random 12-byte nonce and a 16-byte tag; layout `nonce | tag | ciphertext`.
- The key is derived with HKDF-SHA-256 from the device fingerprint (info string
  `RefractionStudio.LicenseStore.v1`, 32 bytes).
- Writes are atomic (`.tmp` file, then rename).
- If the file is damaged, edited or copied from another computer, decryption fails and the file is
  treated as missing: the user sees "Not activated" and the server decides.

Limitation: the key is derived from information available on the computer itself (the public salt and
local identifiers). The store therefore detects editing and copying, but cannot keep a secret from a
local administrator who reimplements the derivation. The licence itself is protected by the server's
signature on the token, not by this encryption. (ARCHITECTURE.md calls the last-seen time "signed";
it is encrypted and authenticated by AES-GCM, not signed.)

---

## 5. Offline use and the clock-rollback guard

`LicenseManager.Evaluate()` runs without network access and checks, in order:

1. Nothing stored → Not activated.
2. Clock rollback: if the current UTC time is earlier than `LastSeen` minus the tolerance (default 24 h,
   `LicensingOptions.ClockRollbackTolerance`), the state is NetworkValidationRequired and editing is
   disabled; a successful online validation clears it. If the clock has moved forward, `LastSeen` is
   updated in the store.
3. A final server state stored earlier (revoked, expired, activation limit) is reported.
4. A token that fails verification → Not activated ("could not be verified on this computer").
5. Trial token: TrialExpired at or after `TrialEnd`; NetworkValidationRequired after
   `OfflineValidUntil`; TrialExpiring within `TrialWarningDays` (default 3) of the end; otherwise
   TrialActive.
6. Licence token: LicenseExpired at or after `ExpiresAt`; NetworkValidationRequired after
   `OfflineValidUntil`; LicenseOfflineGrace if the last online validation is more than 2 days old;
   otherwise Licensed.

The server sets the offline window in the signed claim `offlineValidUntil`; per LICENSE-SERVER.md it is
30 days by default for licences (never past the expiry, configurable per licence) and 3 days for
trials (never past the trial end).

Online calls (`HttpLicenseApi`):

- Base address: the *Licence server* setting; timeout 20 s.
- Endpoints: `api/trials/start`, `api/licenses/activate`, `api/licenses/validate`,
  `api/licenses/deactivate`.
- The server address must be an `https://` URL; Settings refuses anything else (`SettingsViewModel`).
  The default is `https://licensing.quakelogic.net/`.

How responses are handled:

- Network failures and HTTP 5xx responses count as offline. The offline evaluation stands, so a server
  outage never locks anyone out within the offline window.
- If validation is refused with `not_activated` or `device_mismatch`, the local store is deleted at
  once, so a computer deactivated on the server cannot keep editing until its offline window ends.
- If validation is refused because the licence is revoked or suspended, expired, the trial has
  expired, or the activation limit is reached, that final state is stored.
- If the server returns a token that fails verification on the computer, it is not stored and an
  error is logged.

When the application is activated it refreshes the licence online at start-up
(`MainWindowViewModel.StartupAsync`). The licence panel (toolbar licence button; Settings → License has
a button that opens it; `LicenseViewModel`, `Views/LicenseView.axaml`) offers *Start 14-day trial*,
*Activate*, *Check now*, *Copy licence information* and *Deactivate this computer…*. The copied text
includes the first 12 characters of the fingerprint and the key hint, not the full key.

---

## 6. What a licence problem blocks

`LicenseStatus.CanEdit` is true for TrialActive, TrialExpiring, Licensed and LicenseOfflineGrace, and
false for every other state.

When `CanEdit` is false:

- All project edits are blocked. `StudioContext.Edit` (`ViewModelBase.cs`) refuses the edit and tells
  the user "Projects can still be opened, viewed and exported", and drag gestures do not start
  (`BeginGesture`).
- Several commands also check `CanEdit` themselves: import, restoring a snapshot, picking, processing
  changes, interpretation, tomography runs, and trace edits in the gather viewer (`ImportPageViewModel`,
  `ProjectPageViewModel`, `PicksPageViewModel`, `WaveformsAndProcessingViewModels`,
  `QcAndVelocityViewModels`, `GatherViewerViewModel`).

These keep working in every licence state: opening and viewing projects, generating PDF reports, and
XLSX, CSV, audit and figure exports. The licensing code never modifies or deletes project data. (The
doc comment on `LicenseStatus.CanEdit` mentions exporting, but no export checks it.)

---

## 7. Update verification

`UpdateService` (`Updater.cs`):

1. **Check.** `GET api/updates/refraction-studio/{channel}?current={version}` relative to the
   licence-server address. 204 means up to date; otherwise the response is a `SignedManifest`: the
   `ReleaseInfo` JSON in base64url, an ES256 signature in base64url, and a key id.
2. **Manifest signature.** Verified with `ManifestSigning.Verify` against `AppInfo.ReleasePublicKeys`
   (the embedded `Keys/release-public.pem`). The release key is separate from the licence-token key. A
   manifest that fails verification is ignored and logged.
3. **Product and version.** The manifest must name `refraction-studio` and a version newer than the
   running one, compared by SemVer 2.0 precedence (as on the server). If the release states a
   `minimumVersion` newer than the installed version, the application does not update in place and
   tells the user to download the full installer instead.
4. **HTTPS.** `downloadUrl` must start with `https://`, or the update is ignored.
5. **Download and checksum.** The installer is streamed to
   `%LocalAppData%\QuakeLogic\RefractionStudio\Updates\RefractionStudio-{version}-setup.exe.part`
   while its SHA-256 is computed. If the hash does not match the signed `sha256`, the file is deleted
   and the update fails.
6. **Authenticode (Windows only).** The installer is checked with `WinVerifyTrust`
   (`WINTRUST_ACTION_GENERIC_VERIFY_V2`, no UI, revocation checked for the whole chain except the
   root). An installer that fails is deleted. This confirms a valid, trusted Authenticode signature; it
   does not pin a specific publisher certificate — the SHA-256 in the signed manifest ties the download
   to the release.
7. **Confirmation.** Nothing is installed until the user agrees; for major releases the release notes
   are shown first. The open project is closed, with the usual save prompt.
8. **Install.** On Windows the installer starts with `/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SP-`
   and the application exits. On other platforms the verified installer is saved and its path shown.

Update modes (Settings → Updates): **Manual** checks only when the user asks; **NotifyOnly** checks at
start-up and shows an available update on the start page without downloading anything; **Automatic**
checks at start-up and offers to download and install. Nothing is installed without the user's
confirmation in any mode.

Manifests are signed with `sign-manifest <manifest.json> --key <private.pem> --out <signed.json>`
(`Signing.cs`), or by `packaging/package_windows.py --manifest-key … --download-url …`; the licence
server signs manifests per request. Code signing: [DEPLOYMENT.md](DEPLOYMENT.md).

---

## 8. Crash reports, logs and diagnostics privacy

### Logs

Logs stay on the computer:

- `%LocalAppData%\QuakeLogic\RefractionStudio\Logs\refraction-studio-*.log`, Serilog compact JSON;
- one file per day, the last 14 files kept, at most 20 MB per file;
- level Information, or Debug when "Detailed logging" is on;
- licence messages record states and error codes, not keys or tokens.

### Scrubbing

`Logging.Scrub` replaces:

- licence keys (`RFST-XXXX-…`) with `RFST-****-****-****-****`;
- compact tokens (three base64url segments of at least 10 characters, starting with `ey`) with
  `[token removed]`;
- e-mail addresses with `[e-mail removed]`.

### Crash reports

`CrashHandler` handles unhandled exceptions from .NET and from the UI dispatcher. It logs the exception,
tries to save a recovery checkpoint of the open project, and writes
`Crashes/crash-<UTC time>.json` with the time, version, exception type, and the scrubbed message and
stack trace. At the next start pending crash reports are moved to `Crashes/acknowledged/`, and the user
is offered recovery of recoverable projects. Nothing is uploaded. Unexpected UI errors are shown to the
user with scrubbed details.

### Diagnostic package

The diagnostic package (`DiagnosticPackage`, Settings → Advanced → "Export diagnostic package…") is a
ZIP that the user saves and sends manually ("Nothing was sent automatically"). It contains:

- system information: version, OS, .NET version, architecture, processor count, available memory,
  culture, and the theme, units, mode and update settings;
- `settings.json` with the logo, user name and last folder removed, then scrubbed. Company branding
  text (company name, address, phone, website) stays in this file; only e-mail addresses are removed;
- the 7 most recent log files, scrubbed;
- up to 20 crash reports;
- project or survey files, only those the user explicitly selects in a file dialog.

### Telemetry

No usage statistics are collected or sent, and Settings has no option to enable them.

---

## 9. Secrets handling

- **Embedded keys.** The application embeds only public keys:
  `src/RefractionStudio.Infrastructure/Keys/license-public.pem` and `release-public.pem`, compiled in as
  `RefractionStudio.Keys.*.pem` (`RefractionStudio.Infrastructure.csproj`). If one is missing from a
  build, the application fails the first time it needs that key.
- **Key generation.** `refraction-studio-cli keys --out <folder>` (`Signing.cs`) writes
  `signing-<kid>.private.pem` and `signing-<kid>.public.pem`; on Unix the private key is readable and
  writable by the owner only. It refuses to overwrite an existing private key and prints a reminder
  that the private key must never be committed or shipped.
- **Development keys.** The key pairs used on the build host are in `.secrets/license-signing/` and
  `.secrets/release-signing/` (one `signing-<kid>.private.pem` / `.public.pem` pair each). `.secrets/`
  is listed in the root `.gitignore`. The embedded public keys are identical to the public halves of
  these pairs. For production, generate keys on a secure machine and keep the private keys and the
  server pepper in the server's secret store (see "Signing keys" in LICENSE-SERVER.md).
- **Server.** `server/.gitignore` excludes `.env`, `secrets/`, `*.private.pem` and `*.pfx`.
- **Installer.** The installer contains only the published application, the sample project, the EULA
  and the third-party notices (`[Files]` in `packaging/windows/RefractionStudio.iss`).
  `packaging/package_windows.py` receives the release private key only as a file path for signing the
  manifest; the key is never copied into the build output.
- **Code-signing certificates** stay on the Windows build machine and are used through the `rssign`
  sign tool configured in Inno Setup ([DEPLOYMENT.md](DEPLOYMENT.md)).
- The client does not store licence keys (§2).

---

## 10. Tests

- `tests/RefractionStudio.Licensing.Tests/CryptoTests.cs`: licence-key format and check character,
  token signing and verification (tampering, wrong key, rotation, wrong type, `alg: none`), manifest
  signing.
- `tests/RefractionStudio.Licensing.Tests/DeviceAndStoreTests.cs`: device fingerprint (salt,
  component hashes, optional machine name, raw identifiers not in the device description) and the
  encrypted store (tampering, copy to another computer).
- `tests/RefractionStudio.Licensing.Tests/LicenseManagerTests.cs`: licence-state evaluation, offline
  windows, clock rollback (> 24 h requires validation, ≤ 24 h tolerated), server responses, foreign and
  untrusted tokens.

The server-side security tests (activation, rate limits, Stripe webhooks, admin portal) are in
`tests/RefractionStudio.Server.Tests/` and need Docker ([TESTING.md](TESTING.md)).
