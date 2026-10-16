# Refraction Studio licensing admin portal

`server/RefractionStudio.AdminPortal` is the internal web application QuakeLogic staff use to manage
customers, licences, computers, trials, software releases and to read the audit log. It is an ASP.NET
Core Razor Pages application on the same PostgreSQL database as the licence server
([LICENSE-SERVER.md](LICENSE-SERVER.md)) and shares its domain services (`RefractionStudio.Server.Data`),
so every change made in the portal follows exactly the same rules as the API and the payment webhook.

Expose it only on the office network or VPN, behind the TLS reverse proxy (see LICENSE-SERVER.md §7).

## Security

* **Sign-in required everywhere.** A fallback authorisation policy protects every page; only the sign-in
  page, the access-denied page, `/health` and the stylesheet are anonymous.
* **Passwords** are hashed with ASP.NET Core Identity's `PasswordHasher` (PBKDF2-HMAC-SHA-512, salted,
  iterated; hashes are upgraded automatically on sign-in when the default parameters change). Minimum
  length 12 characters.
* **Lockout:** five consecutive failed sign-ins lock the account for 15 minutes. Unknown e-mail
  addresses take the same time to reject as wrong passwords. Sign-in attempts are also rate limited per
  client address (`Admin__LoginAttemptsPerMinute`, default 20).
* **Sessions** use an HTTP-only, `SameSite=Strict`, `Secure` cookie (8 hours, sliding). Every request
  re-checks the account: disabling a user or changing their password or role ends their sessions at
  once (security stamp).
* **Antiforgery** tokens protect every form; `X-Frame-Options: DENY`, a strict Content-Security-Policy
  (no inline script, no external resources), `nosniff` and `no-referrer` headers are set; HSTS and HTTPS
  redirection outside Development.
* **Licence keys** are displayed only once — in the response that creates (or replaces) them, with
  `Cache-Control: no-store` — and are never stored. Afterwards the portal shows only the last four
  characters (`…T3NB`).
* **Audit:** every state-changing action writes an `audit_log` entry with the admin user id, the action,
  the entity, a keyed hash of the client IP and the details. Sign-ins, failed sign-ins and lockouts are
  audited too. The audit log is append-only (database triggers) and read-only in the portal.

## Roles

| Role | Can |
|---|---|
| `administrator` | everything, including software releases and admin accounts |
| `support` | customers, licences, activations, devices and trials (all write operations except releases and accounts) |
| `viewer` | read-only access to every page except account management |

## First administrator

When the `admin_users` table is empty, the portal creates an administrator at start-up from
`Admin__BootstrapEmail` and `Admin__BootstrapPassword` (in Docker Compose: `ADMIN_BOOTSTRAP_EMAIL` and
`ADMIN_BOOTSTRAP_PASSWORD` in `server/.env`). The bootstrap is ignored once any account exists. Sign in,
change the password (click your e-mail address in the top bar), then remove the bootstrap password from
the environment. Further accounts are created under **Admin users**.

## Pages

### Dashboard

Active licences, computers activated (seats in use), activations in the last 30 days, active trials,
trials ending within 3 days, licences ending within 30 days, queued and failed e-mails, the trials that end
soon and the 25 most recent licence events.

### Customers

Search by e-mail, name or company. The detail page shows contact data (editable: name, company, internal
notes), the customer's licences, payments (provider reference, amount, status — never card data) and the
e-mails sent to them with their delivery status. **New licence for this customer** opens the licence form
pre-filled.

### Licences

* **List / search** by the last four key characters, customer e-mail, name, company or licence id;
  filter by status. Shows seats in use, expiry and maintenance dates.
* **New licence:** enter the customer e-mail (an existing customer is reused, otherwise created), product,
  edition, seats, optional expiry (blank = perpetual), maintenance date, features, transfer limit,
  offline days and notes. Optionally e-mail the key and activation instructions. The full key is shown
  **once** on the result page — copy it now if the customer needs it immediately.
* **Licence detail:** customer, seats in use, dates, transfers used in the last 30 days, payments and:
  * **Suspend** (temporary, e.g. payment dispute) / **Reinstate** / **Revoke** (permanent; refund or
    abuse). Activated computers learn about it at their next online validation (at the latest when their
    offline window ends) and become read-only viewers; project data is never affected.
  * **Extend / renew:** new expiry date, make perpetual, new maintenance date; optionally send the renewal
    e-mail. An expired licence becomes active again. Computers receive the new dates at their next
    validation.
  * **Override limits:** seats, transfers per 30 days, offline days.
  * **Reset activations:** deactivates every computer (does not count against the transfer limit).
  * **Deactivate** a single computer from the activation history.
  * **Replace key:** issues a new key (lost or leaked key) and shows it once; the old key stops working,
    already activated computers keep working.
  * **Send instructions:** e-mails the activation steps (without the key).
  * **Activation history** (computer, activation, last validation, app version, deactivation and reason)
    and the licence's **event history**.

### Activations

Every activation across all licences, newest first; search by key hint, customer e-mail or computer
name; filter to active seats only.

### Devices

Computers that activated a licence, identified by their salted fingerprint (no hardware identifiers are
stored). The detail page shows component hashes, activations and events, and lets support **block** a
computer (with a reason) — a blocked computer can no longer activate, validate or start a trial — or
**unblock** it.

### Trials

All trials with their computer, start, end and last contact; filter by active, ending within 3 days or
ended; search by fingerprint prefix, computer name or e-mail.

* **Extend** by N days (1–365): from the current end, or from today if the trial has already ended. The
  computer receives the new end at its next online contact.
* **Contact e-mail:** filled in automatically when the user gives an address when starting the trial;
  otherwise sales or support can record it. With a contact e-mail the server sends the "trial ends soon"
  reminder 3 days before the end.

### Software releases

Release entries of the update feed. Administrators create releases (product, SemVer version, channel
`stable`/`beta`, release date, HTTPS download URL of the code-signed installer, SHA-256, size, minimum
version that can update directly, Markdown release notes, "major" flag) as drafts or published, and can
publish or withdraw them. The feed signs manifests on request with the release key; nothing needs to be
signed in the portal. A release with a future date is offered from that date on.

### Audit log

Read-only, newest first; filter by action prefix (`license.`, `admin.`, `trial.`, `device.`,
`release.`, `customer.`, `admin_user.`) or actor. Each entry links to the licence, customer or device
it concerns.

### Admin users (administrators)

Create accounts (e-mail, name, initial password, role), change roles, unlock locked accounts, disable or
enable accounts and reset passwords. You cannot disable your own account or change your own role.

## Configuration

| Variable | Notes |
|---|---|
| `ConnectionStrings__Licensing` | same database as the licence server |
| `Licensing__KeyPepper` / `Licensing__KeyPepperPath` | **must be identical** to the licence server's pepper |
| `Licensing__AutoMigrate` | apply migrations at start (the licence server normally does this) |
| `Admin__BootstrapEmail`, `Admin__BootstrapPassword` | first administrator (only when no admin exists) |
| `Admin__LoginAttemptsPerMinute` | per-address sign-in rate limit (default 20) |
| `DataProtection__KeysPath` | directory for cookie/antiforgery keys (a persistent volume); without it, keys are in memory and everyone is signed out on restart |
| `Email__SupportAddress`, `Email__DownloadUrl` | used in the e-mails the portal queues |
| `ReverseProxy__KnownProxies`, `ReverseProxy__KnownNetworks`, `Hosting__HttpsRedirection`, `Hosting__HttpsPort` | as for the licence server |

E-mails queued by the portal (licence keys, renewals, instructions) are delivered by the licence
server's e-mail sender.

Run locally: `dotnet run --project server/RefractionStudio.AdminPortal` (Development, HTTP on port 5090)
with the connection string, pepper and bootstrap variables set.
