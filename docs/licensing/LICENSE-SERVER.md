# Refraction Studio licence server

The licensing back end of Refraction Studio consists of three projects that share one PostgreSQL
database:

| Project | Role |
|---|---|
| `server/RefractionStudio.Server.Data` | EF Core model and migrations, licence domain services (keys, activation, trials, tokens, releases), e-mail outbox and templates, Stripe integration, hosting helpers. |
| `server/RefractionStudio.LicenseServer` | Public HTTPS API used by the desktop application and by Stripe: trials, activation, validation, deactivation, update feed, webhook, health. Runs the e-mail sender and housekeeping workers. |
| `server/RefractionStudio.AdminPortal` | Internal Razor Pages portal for QuakeLogic staff (see [ADMIN-PORTAL.md](ADMIN-PORTAL.md)). |

The desktop side is `src/RefractionStudio.Licensing` (`LicenseManager`, `HttpLicenseApi`, the DTOs in
`Contracts.cs` and the cryptography in `Crypto.cs`). That library is the contract: the server uses the
same DTOs, the same token format and the same key functions.

```
 Refraction Studio (desktop)                      QuakeLogic infrastructure
 ───────────────────────────                      ───────────────────────────────────────────────
 LicenseManager ──HTTPS JSON──►  TLS reverse proxy ──► LicenseServer ─┐
   verifies tokens with the       (Caddy / nginx)      (API, workers)  │
   embedded public key(s)                        ┌──► AdminPortal ─────┼──► PostgreSQL
 Updater ──GET /api/updates──►                   │    (staff only)     │
   verifies manifests with the   Stripe ─webhook─┘                     └──► SMTP relay
   embedded release public key
```

## 1. Design principles

* **The server is authoritative, the desktop can work offline.** Every success returns an ES256-signed
  token (`LicenseToken`, compact JWS with type `RSLIC`). The desktop verifies it with the public key it
  ships with and may use it until `offlineValidUntil` (30 days for licences, never beyond the licence
  expiry; 3 days for trials, never beyond the trial end).
* **Licence keys are never stored.** The database holds `HMAC-SHA-256(pepper, canonical key)` and the last
  four characters. The full key is shown once in the admin portal and sent once by e-mail (the queued
  e-mail body is encrypted at rest and erased after delivery).
* **Trials are per device and product and survive reinstalling.** A computer matches an existing trial
  when the fingerprint is equal *or* at least two component hashes are shared, so a single hardware
  change does not produce a fresh trial. New component hashes are merged into the trial record.
* **Every refusal is a 4xx with a stable code.** The desktop treats 5xx and network errors as "offline"
  and keeps its offline evaluation, so an outage never locks anyone out.
* **Append-only history.** `license_events` and `audit_log` reject `UPDATE`, `DELETE` and `TRUNCATE`
  (database triggers plus a guard in the EF context). Client IP addresses are stored only as keyed hashes.

## 2. API

Base URL: `https://licensing.quakelogic.net/`. All bodies are JSON (`application/json`, camelCase, enums
in camelCase) as defined by `LicenseJson.Options`. Successful licence calls return HTTP 200 with a
`TokenResponse`; refusals return 4xx with an `ErrorResponse`:

```json
{ "token": "eyJhbGciOiJFUzI1NiIsInR5cCI6IlJTTElDIiwia2lkIjoi…", "message": "Activation successful." }
{ "code": "activation_limit", "message": "This licence is already active on 1 computer, its permitted number. …" }
```

`message` is written for the end user and is shown by the application as is.

The `device` object (`DeviceInfo`) contains only salted hashes:

```json
{
  "fingerprint": "9f2c…64 hex…",
  "componentHashes": ["1b7e…", "c04a…", "77d0…"],
  "machineName": "SURVEY-LAPTOP",
  "osVersion": "Microsoft Windows 10.0.22631 X64"
}
```

`fingerprint` and every component hash must be 64 lower-case hex characters; at most 16 component
hashes are accepted. `machineName` is optional (the user decides whether to share it).

### POST /api/trials/start

Starts the 14-day trial of this computer, or continues the existing one (same fingerprint, or ≥ 2
matching component hashes). Rate limit: per-IP licence limit.

```json
{ "product": "refraction-studio", "appVersion": "1.0.0", "device": { … }, "email": "eva@example.com" }
```

`email` is optional (the user may type it on the welcome screen). It is stored as the trial's contact
address when the trial has none yet, so that the "trial ends soon" reminder can be sent; an address
recorded by staff in the portal is never replaced. An invalid address is refused with `invalid_request`.

* 200 — trial token (`kind: "trial"`, `trialStart`, `trialEnd`, `offlineValidUntil = min(now + 3 days, trialEnd)`).
  Message: "Your 14-day trial of Refraction Studio has started." or "… continues: 9 days remaining."
* 403 `trial_expired` — the trial of this computer has ended (message names the date).
* 400 `invalid_request` — malformed request, invalid e-mail address or unknown product.

### POST /api/licenses/activate

Activates a licence key on this computer. Rate limits: per-IP activation limit and a per-key limit.

```json
{ "licenseKey": "RFST-7K2M-Q9XD-4HWP-T3NB", "email": "jane@example.com", "product": "refraction-studio", "appVersion": "1.0.0", "device": { … } }
```

The key is normalised (case, spaces, `O`→`0`, `I`/`L`→`1`, optional prefix) and its check character is
verified before the database is consulted. `email` is informational and is not required to match.

Order of checks: key exists → product matches → status (revoked / suspended / expired) → device not
blocked → **same device already active** (idempotent: a new token, no extra seat) → seats available →
transfer limit → new activation. Seat accounting runs under a row lock on the licence.

* 200 — licence token (`kind: "license"`, `licensedTo`, `email`, `keyHint`, `seats`, `features`,
  `expiresAt`, `maintenanceUntil`, `offlineValidUntil = min(now + offline_days, expiresAt)`).
* 404 `invalid_key`, 400 `product_mismatch`, 403 `revoked` / `suspended` / `expired`,
  409 `activation_limit`, 409 `transfer_limit`, 429 `rate_limited`.

**Transfer limit.** A transfer is a user-initiated deactivation (`deactivation_reason = 'user'`). When
the number of such deactivations in the last 30 days exceeds `max_transfers_per_30_days` (default 3),
activating a new computer is refused with `transfer_limit`. Deactivations made by support staff
(single deactivation or "reset activations") do not count.

### POST /api/licenses/validate

Online check of the stored token (licence *or* trial), called by `LicenseManager.RefreshAsync`.

```json
{ "token": "eyJ…", "appVersion": "1.0.1", "device": { … } }
```

The token signature is verified with the server's current and retired public keys; the device in the
token must equal the requesting device. For licences the licence must still be active and unexpired, the
device not blocked and the activation still active; for trials the trial must not have ended.

* 200 — a refreshed token with a new offline window (new `tokenId`, `issuedAt`).
* 400 `invalid_request` (token not genuine), 403 `device_mismatch`, 404 `not_activated` (the computer was
  deactivated), 403 `revoked` / `suspended` / `expired` / `trial_expired`.

### POST /api/licenses/deactivate

Frees the seat of this computer and e-mails a confirmation to the licence owner. Idempotent: a computer
that is already deactivated succeeds with "This computer was already deactivated."

```json
{ "token": "eyJ…", "device": { … } }
```

* 200 — `{ "token": "", "message": "This computer has been deactivated. The licence can now be activated on another computer." }`
  (the contract's `TokenResponse` requires a token field; it is empty here).
* 400 `invalid_request` (not genuine, or a trial token), 403 `device_mismatch`.

### GET /api/updates/{product}/{channel}?current=x.y.z

Returns the newest published release newer than `current` as a `SignedManifest`, or **204 No Content**
when the application is up to date. `channel` is `stable` or `beta`; the beta channel also receives
stable releases. Versions compare by SemVer 2.0 precedence (`1.2.0-beta.2 < 1.2.0-beta.10 < 1.2.0`).
Releases with a future `released_at` are not offered yet.

```json
{ "payload": "eyJwcm9kdWN0Ijoi…", "signature": "MEUCIQ…", "keyId": "3fa1c09e2b7d4410" }
```

`payload` is base64url JSON of `ReleaseInfo`; the desktop verifies it with `ManifestSigning.Verify` and
the embedded **release** public key, which is different from the licence-token key.

### GET /api/health

`200 {"status":"ok","database":"ok"}` or `503` when PostgreSQL is unreachable. Not rate limited.

### POST /api/webhooks/stripe

Stripe webhook endpoint (see §8). Requires a valid `Stripe-Signature` header (HMAC-SHA-256 of
`"{t}.{payload}"` with the endpoint secret, constant-time comparison, 5-minute tolerance); otherwise
400. Each Stripe event id is processed at most once (`webhook_events`); a replayed delivery returns
`200 {"received":true,"outcome":"duplicate"}`.

### Error codes

| Code | HTTP | Meaning | Desktop state |
|---|---|---|---|
| `invalid_request` | 400 | Malformed request, unknown product, token not genuine | message only |
| `product_mismatch` | 400 | Key belongs to another product | message only |
| `invalid_key` | 404 | Unknown or mistyped key | message only |
| `not_activated` | 404 | This computer's activation has ended | message only |
| `revoked` | 403 | Licence revoked, or the computer is blocked | `LicenseRevoked` |
| `suspended` | 403 | Licence suspended | `LicenseRevoked` |
| `expired` | 403 | Licence term ended | `LicenseExpired` |
| `trial_expired` | 403 | Trial ended | `TrialExpired` |
| `device_mismatch` | 403 | Token belongs to another computer | message only |
| `activation_limit` | 409 | All seats in use | `ActivationLimitReached` |
| `transfer_limit` | 409 | Too many transfers in 30 days | message only |
| `rate_limited` | 429 | Too many requests (`Retry-After` header) | message only |
| `server_error` | 500 | Unexpected error (logged) | treated as offline |

### Rate limits

| Policy | Endpoints | Default | Setting |
|---|---|---|---|
| licence (per client IP, fixed 1-minute window) | trials, validate, deactivate, updates | 30 / min | `RateLimits__LicensePerMinute` |
| activation (per client IP) | activate | 10 / min | `RateLimits__ActivationPerMinute` |
| per key (any IP) | activate | 20 / hour | `RateLimits__ActivationsPerKeyPerHour` |

The client IP is taken from `X-Forwarded-For` only when the request comes from a trusted proxy (§6).

## 3. Tokens and keys

### Token claims (`LicenseClaims`)

| Claim | Licence | Trial |
|---|---|---|
| `tokenId` | random | random |
| `kind` | `license` | `trial` |
| `licenseId` | licence id | trial record id |
| `product`, `edition` | from the licence | product default edition |
| `licensedTo`, `email`, `keyHint` | company (or name), e-mail, last 4 key characters | — |
| `deviceHash` | requesting fingerprint | requesting fingerprint |
| `expiresAt` | licence expiry (null = perpetual) | trial end |
| `maintenanceUntil` | updates included until | trial end |
| `offlineValidUntil` | now + `offline_days` (default 30), capped at `expiresAt` | now + 3 days, capped at trial end |
| `trialStart`, `trialEnd` | — | trial dates |
| `features`, `seats` | from the licence | product default features, 1 |

### Key format

`RFST-XXXX-XXXX-XXXX-XXXX`: 15 random Crockford base32 characters (75 bits from the OS CSPRNG) and one
check character, Σ vᵢ·(i+1) mod 31 over the base-32 values of the 15 body characters. Because 31 is
prime, the check catches every single-character substitution and every adjacent transposition except
the pair “0” ↔ “Z” (whose values differ by 31), and every change to the check character itself (> 99.9 %
of single-character typos); keys that fail the check are refused by the desktop before any network
call. Input is normalised first: case is ignored, separators are dropped and O/I/L are read as 0/1. Keys are stored as `HMAC-SHA-256(Licensing:KeyPepper, canonical key)` in hex plus the last
four characters (`key_hint`).

### Signing keys

| Key | Purpose | Setting |
|---|---|---|
| Licence token key (ES256, P-256) | signs trial and licence tokens | `Licensing__SigningKeyPem` or `Licensing__SigningKeyPath` |
| Retired token public keys | still accepted by validate/deactivate during rotation | `Licensing__PreviousSigningPublicKeyPem` / `…Path` (PEM blocks concatenated) |
| Release key (ES256, P-256) | signs update manifests; must differ from the token key | `Licensing__ReleaseSigningKeyPem` / `…Path` |
| Key pepper (≥ 32 random bytes, base64) | HMAC of licence keys and IP addresses; outbox encryption key | `Licensing__KeyPepper` / `…Path` |

Generate keys on an offline machine:

```bash
dotnet run --project tools/RefractionStudio.Cli -- keys --out ./keys          # licence token key pair
dotnet run --project tools/RefractionStudio.Cli -- keys --out ./release-keys  # release key pair
openssl rand -base64 32 > key-pepper.txt
# or with OpenSSL only:
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out license-signing.private.pem
openssl pkey -in license-signing.private.pem -pubout -out license-signing.public.pem
```

The public PEM goes into the desktop application (`LicensingOptions.TrustedPublicKeys`, release public
key in the updater). Private keys and the pepper go only into the secret store / `server/secrets/`.
The server refuses to start when a key or the pepper is missing or malformed, and when the release key
equals the token key. Inline PEM values may use `\n` escapes for line breaks.

**The pepper must never change.** All stored key hashes depend on it; a new pepper makes every issued key
unrecognisable. Back it up with the same care as the database (§7).

### Key rotation

The desktop trusts a *list* of public keys (`TrustedPublicKeys`: current and next), so the token key can
be rotated without downtime:

1. Generate the next key pair. Ship a desktop release whose trusted list contains **current + next**.
2. When that release is widely installed, configure the server with the next private key as
   `SigningKey…` and put the old public key in `PreviousSigningPublicKey…`. New tokens are signed with the
   new key (`kid` changes); tokens signed with the old key are still accepted by validate and deactivate
   and are replaced by new-key tokens on their next validation.
3. After the longest offline window (30 days for licences) plus a safety margin, remove the old public key
   from the server and, in a later desktop release, from the trusted list.

If a private key is compromised: issue the next key immediately (step 2), remove the compromised public
key from the server's previous-keys list at once, and ship a desktop update that no longer trusts it.

The release key rotates the same way: the updater trusts current + next release public keys; switch the
server's `ReleaseSigningKey…` once the update is out. Manifests are signed per request, so the switch is
immediate.

## 4. Data model

PostgreSQL, snake_case names, all times `timestamptz` in UTC (stored with microsecond precision).
Migrations live in `server/RefractionStudio.Server.Data/Migrations` (EF Core; history table
`__ef_migrations_history`).

| Table | Content |
|---|---|
| `customers` | e-mail (unique, lower-case), name, company, payment-provider customer id, notes |
| `license_products` | product code (PK), name, key prefix, default edition and features, trial days, flags. Seeded with `refraction-studio` / `RFST`. |
| `licenses` | id, `license_key_hash` (unique HMAC), `key_hint`, customer, product, edition, `status` (`active`/`suspended`/`revoked`/`expired`), status reason, `seat_count`, `created_at`, `expires_at` (null = perpetual), `maintenance_until`, `features` (text[]), notes, `max_transfers_per_30_days` (3), `offline_days` (30) |
| `devices` | id, product, `fingerprint` (unique per product), `component_hashes` (text[]), machine name, OS, first/last seen, `blocked`, reason |
| `activations` | licence, device, `activated_at`, `deactivated_at` (null = holds a seat), `last_validated_at`, app version, `deactivation_reason` (`user`/`admin`/`admin_reset`). A partial unique index allows one active activation per licence and device. |
| `trial_devices` | product, fingerprint, `component_hashes` (text[], GIN index), `started_at`, `ends_at`, `extended_by_admin`, last seen, machine name, optional contact e-mail, reminder timestamp |
| `license_events` | append-only: type, actor, `ip_hash`, licence / trial / device ids, `details` (jsonb) |
| `audit_log` | append-only: admin user id, actor, action, entity, `ip_hash`, `details` (jsonb) |
| `software_versions` | product, version (unique per product), channel, released at, download URL, SHA-256, size, release notes, major, minimum version, published |
| `payments` | provider, provider payment id (unique per provider), customer, licence, amount (minor units), currency, status. **No card data is ever received or stored.** |
| `admin_users` | e-mail, PBKDF2 password hash (ASP.NET Core Identity `PasswordHasher`), role, failed attempts, locked until, disabled, security stamp, last sign-in |
| `email_outbox` | recipient, subject, text and HTML body (encrypted while queued when sensitive), template, status (`pending`/`sent`/`failed`/`skipped`), attempts, next attempt, error |
| `webhook_events` | (provider, event id) primary key — webhook idempotency — with type and outcome |

## 5. Background work

* **E-mail sender** (`EmailSenderService`): sends due outbox messages through SMTP (MailKit) every
  `Workers__EmailPollSeconds` (15 s). Rows are claimed with `FOR UPDATE SKIP LOCKED`, failures retry with
  exponential back-off (2, 4, 8 … minutes, 8 attempts). Without `Smtp__Host`, messages stay queued (and
  are marked `skipped` in Development).
* **Housekeeping** (`MaintenanceService`, every `Workers__MaintenanceMinutes` = 60): marks licences past
  their expiry as `expired`; queues the "trial ends soon" reminder (3 days before the end) for trials with
  a contact e-mail. Guarded by a PostgreSQL advisory lock, so several instances can run.

E-mail templates (plain text + simple HTML): purchase confirmation, licence key, activation
instructions, trial expiration reminder, licence renewal, device deactivation confirmation.

## 6. Configuration

Configuration comes from `appsettings.json` (no secrets) and environment variables (`__` separates
sections). Secrets come from environment variables or files mounted from a secret store.

| Variable | Default | Notes |
|---|---|---|
| `ConnectionStrings__Licensing` | — | Npgsql connection string |
| `Licensing__AutoMigrate` | `false` (`true` in Development) | apply migrations at start-up |
| `Licensing__SigningKeyPem` / `__SigningKeyPath` | — | **secret**, required by the licence server |
| `Licensing__PreviousSigningPublicKeyPem` / `__Path` | — | retired token public keys (rotation) |
| `Licensing__ReleaseSigningKeyPem` / `__Path` | — | **secret**, required by the licence server |
| `Licensing__KeyPepper` / `__KeyPepperPath` | — | **secret**, base64 ≥ 32 bytes, required by both apps, identical in both |
| `Licensing__TrialOfflineDays` | 3 | offline window of trial tokens |
| `RateLimits__LicensePerMinute` / `__ActivationPerMinute` / `__ActivationsPerKeyPerHour` | 30 / 10 / 20 | |
| `Stripe__WebhookSecret` | — | **secret**, `whsec_…` |
| `Stripe__ToleranceSeconds` | 300 | signature age tolerance |
| `Stripe__DefaultProduct`, `Stripe__DefaultMaintenanceDays` | `refraction-studio`, 365 | when the session metadata does not say |
| `Smtp__Host`, `__Port`, `__Security`, `__Username`, `__Password`, `__FromAddress`, `__FromName` | –, 587, `StartTls` | password is a **secret** |
| `Email__SupportAddress`, `Email__DownloadUrl`, `Email__CompanyName`, `Email__TrialReminderDays` | | used in e-mail texts |
| `ReverseProxy__KnownProxies` / `__KnownNetworks` | loopback only | IPs / CIDRs whose `X-Forwarded-*` headers are trusted |
| `Hosting__HttpsRedirection` | `true` | outside Development: HSTS (1 year) and redirect of plain HTTP |
| `Hosting__HttpsPort` | 443 | port used in HTTPS redirects |
| `Workers__EmailPollSeconds`, `Workers__MaintenanceMinutes` | 15, 60 | |
| `Logging__Console__FormatterName` | `json` | structured JSON logs to stdout |

Logs never contain licence keys (only the 4-character hint), tokens, passwords or e-mail bodies;
recipient addresses are masked (`j***@example.com`). EF Core sensitive-data logging is off.

## 7. Deployment

### Docker Compose

`server/docker-compose.yml` runs PostgreSQL 17, the licence server and the admin portal. Both
applications listen on plain HTTP bound to `127.0.0.1` (`8080` licence server, `8081` admin portal); a
TLS reverse proxy on the host terminates HTTPS.

```bash
cd server
cp .env.example .env          # fill in; .env is git-ignored
mkdir -p secrets && chmod 700 secrets
cp /secure/license-signing.private.pem   secrets/license-signing.private.pem
cp /secure/release-signing.private.pem   secrets/release-signing.private.pem
cp /secure/key-pepper.txt                secrets/key-pepper.txt
touch secrets/previous-signing.public.pem   # empty unless a key rotation is in progress
chmod 444 secrets/*                         # readable by the non-root container user; the directory stays 700
docker compose up -d --build
docker compose logs -f license-server
```

The licence server applies migrations on start (`Licensing__AutoMigrate=true`); the admin portal starts
after the licence server is healthy and creates the first administrator from `ADMIN_BOOTSTRAP_EMAIL` /
`ADMIN_BOOTSTRAP_PASSWORD` when no admin exists. Remove the bootstrap password from `.env` afterwards.
Containers run as a non-root user with a read-only root file system; the Docker health checks call
`dotnet <app>.dll --health-check`. Admin-portal data-protection keys persist in the `admin-keys` volume.

Images can also be built directly (build context = repository root):

```bash
docker build -f server/RefractionStudio.LicenseServer/Dockerfile -t quakelogic/refraction-license-server:1.0.0 .
docker build -f server/RefractionStudio.AdminPortal/Dockerfile   -t quakelogic/refraction-admin-portal:1.0.0 .
```

### TLS reverse proxy

TLS 1.2+ only. The proxy must set `X-Forwarded-For` and `X-Forwarded-Proto`, and its address must be in
`REVERSE_PROXY_NETWORKS` (with a host proxy connecting to the published ports this is the Docker bridge
range, default `172.16.0.0/12`). Example Caddyfile:

```caddyfile
licensing.quakelogic.net {
    reverse_proxy 127.0.0.1:8080
}
licensing-admin.quakelogic.net {
    @outside not remote_ip 203.0.113.0/24   # office / VPN range
    respond @outside 403
    reverse_proxy 127.0.0.1:8081
}
```

nginx equivalent for the API:

```nginx
server {
    listen 443 ssl http2;
    server_name licensing.quakelogic.net;
    ssl_certificate     /etc/letsencrypt/live/licensing.quakelogic.net/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/licensing.quakelogic.net/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    client_max_body_size 512k;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Expose the admin portal only to the office network or VPN.

### Backup and restore

* **Database**: nightly `pg_dump` in custom format, encrypted, copied off-site, kept 30 days; test a
  restore every quarter.

  ```bash
  docker compose exec -T postgres pg_dump -U licensing -Fc refraction_licensing | gpg --encrypt -r ops@quakelogic.net > licensing-$(date +%F).dump.gpg
  # restore into an empty database:
  gpg --decrypt licensing-2026-10-10.dump.gpg | docker compose exec -T postgres pg_restore -U licensing -d refraction_licensing --clean --if-exists
  ```

* **Secrets** (signing keys, release key, pepper) are backed up separately in the company secret store.
  Without the pepper no stored key can be verified; without the token key every desktop needs a new
  release to trust a replacement key.
* The `admin-keys` volume only holds session keys; losing it signs admins out.

### Upgrades

Pull/build the new images and `docker compose up -d`. Migrations are forward-only and run at start.
To run them by hand instead (`Licensing__AutoMigrate=false`):

```bash
dotnet tool restore
ConnectionStrings__Licensing="Host=…" dotnet ef database update --project server/RefractionStudio.Server.Data
dotnet ef migrations script --idempotent --project server/RefractionStudio.Server.Data -o migrate.sql   # for review
```

## 8. Stripe setup

1. In the Stripe Dashboard create the products/prices. The website creates **Checkout Sessions** with
   metadata describing the licence:

   | Metadata key | Example | Default |
   |---|---|---|
   | `product` | `refraction-studio` | `Stripe__DefaultProduct` |
   | `edition` | `Professional` | product default edition |
   | `seats` | `2` | 1 |
   | `license_term_days` | `365` (subscription-style term) | none = perpetual |
   | `maintenance_days` | `365` | `Stripe__DefaultMaintenanceDays` |
   | `company` | `Geo Consulting Ltd` | — |

   The customer e-mail and name come from `customer_details`.
2. Add a webhook endpoint `https://licensing.quakelogic.net/api/webhooks/stripe` for the events
   `checkout.session.completed`, `checkout.session.async_payment_succeeded` and
   `checkout.session.async_payment_failed`. Copy its signing secret into `STRIPE_WEBHOOK_SECRET`.
3. On a paid session the server finds or creates the customer by e-mail, creates the licence, records the
   payment (provider id = PaymentIntent id) and queues the purchase confirmation and licence-key e-mails.
   Sessions paid later (bank transfers) are recorded as `pending` and fulfilled on
   `async_payment_succeeded`. Other event types are acknowledged and recorded as `ignored`; sessions that
   cannot be fulfilled (no e-mail, unknown product) are acknowledged, recorded as `rejected` and logged as
   errors for follow-up in the admin portal.
4. Test locally with the Stripe CLI: `stripe listen --forward-to localhost:5080/api/webhooks/stripe`
   and `stripe trigger checkout.session.completed`.

Card data never reaches QuakeLogic systems; only Stripe ids, amounts and statuses are stored.

## 9. Development and tests

```bash
source .tools/env.sh
dotnet tool restore                                    # dotnet-ef (local tool, .config/dotnet-tools.json)
dotnet build server/RefractionStudio.LicenseServer
dotnet ef migrations add <Name> --project server/RefractionStudio.Server.Data --output-dir Migrations

# Tests (Docker required for the server tests: Testcontainers starts postgres:17-alpine)
dotnet test tests/RefractionStudio.Licensing.Tests
dotnet test tests/RefractionStudio.Server.Tests
```

Running locally (`ASPNETCORE_ENVIRONMENT=Development` via `dotnet run`, HTTP on port 5080, migrations
applied automatically, e-mail skipped unless SMTP is configured):

```bash
export ConnectionStrings__Licensing="Host=localhost;Database=refraction_licensing;Username=licensing;Password=…"
export Licensing__SigningKeyPath=$PWD/dev-keys/license-signing.private.pem
export Licensing__ReleaseSigningKeyPath=$PWD/dev-keys/release-signing.private.pem
export Licensing__KeyPepper=$(openssl rand -base64 32)
dotnet run --project server/RefractionStudio.LicenseServer
```

`server/RefractionStudio.LicenseServer` and `server/RefractionStudio.AdminPortal` both expose a
`public partial class Program` for `WebApplicationFactory` tests; the integration tests in
`tests/RefractionStudio.Server.Tests` run both applications against one PostgreSQL container and also
drive the real desktop `LicenseManager`/`HttpLicenseApi` against the server.
