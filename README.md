# Call Prep

Sales call-prep assistant. A rep asks a question about a customer (typed or spoken) and gets an answer from P21 sales history in seconds. Built 2026-09-04 as the phase-3 "AI gateway" from the AI Use and Security Policy, section 3.15: the model reaches company data only through a read-only service identity, and everything it does is logged.

## Layout

| Path | What |
|---|---|
| `CallPrep.Api/` | ASP.NET Core (.NET 10) minimal API. Anthropic C# SDK, manual tool loop, Npgsql. Serves the built UI from `wwwroot/`. |
| `CallPrep.Web/` | Vite + React + TypeScript chat page. Mic capture in the browser as 16 kHz WAV, transcribed by our own API (Whisper.net); no third-party speech service. |
| `models/ggml-base.en.bin` | whisper.cpp English base model (148 MB) for local speech-to-text. `CALLPREP_WHISPER_MODEL` overrides the path. |
| `run-local.ps1` | Local runner: loads secrets into the process, opens the SSH tunnel to the Hetzner Postgres, starts the API on http://localhost:5070. `-Build` rebuilds the UI into `wwwroot` first. |
| `rg-scripts/webapps/callprep/sql/001_callprep_schema.sql` | The semantic layer on `reportsdb` (Hetzner): schema `callprep`, nine views, `audit_log`, role `callprep_ro`. Additive, re-runnable. |
| `rg-scripts/webapps/callprep/sql/002_lookalikes.sql` | Behavior-based lookalikes (cosine on 12-month product-group mix). Superseded in part by 003. |
| `rg-scripts/webapps/callprep/sql/003_access_control.sql` | Per-user access control inside the database: `user_access` table, scope functions, scoped views, SECURITY DEFINER lookalike functions, admin guard trigger, seed. |

## Data

Source is the ReportsApp Postgres on the Hetzner box (`reportsdb`), which `reports_push.ps1` refreshes from P21 every 15 minutes. No P21 connection, no P21 lock exposure, no new SQL Server login.

Views (all read-only, owned by postgres):
- `customer` (id, name, market class, rep) · `sales_line` (invoices since 2022-03-30, sell price only) · `open_quote_line` · `open_order_line` · `cancelled_quote_line`
- `customer_pg_12m`, `customer_pg_ltd` (customer x product group rollups) · `class_pg_penetration` (market class x product group: share of active peers buying it)
- `audit_log` (question / tool / sql / answer / error per session)

Deliberately excluded: `cogs_amount` (cost/margin), credit and AR, commission data.

Role `callprep_ro`: LOGIN, `statement_timeout = 30s`, USAGE on `callprep` only, SELECT on its views, INSERT on `audit_log`. No access to `public`. Password in the secrets store as `PG_PASSWORD_CALLPREP`.

## Security: who can see what

Sign-in is the person's Microsoft 365 account through Entra ID (OpenID Connect, `Microsoft.Identity.Web`), on the same employee app registration RGCommerce uses (`RGCommerce`, client `c69f5373-bbaa-4282-85b5-7a00c64aed26`, tenant `6ea34d6a-...`). One employee sign-on for every internal app; customers are a separate population and cannot sign in here (single-tenant registration, employee-only access table). `/api/health` is the only anonymous endpoint; `/signin` and `/signout` are the top-level entry points; an API call without a session gets 401 and the page sends the browser to `/signin`.

The M365 UPN/email (lower-case, domain kept: `raritangroup.com` and `raritanvalve.com` both exist) must have an enabled row in `callprep.user_access`:

| role | sees | can also |
|---|---|---|
| `rep` | customers whose `salesrep_id` equals the row's `salesrep_id` | |
| `manager` | all customers | |
| `admin` | all customers | edit `user_access` from the page ("Access" button) |

An account with no row, or a disabled row, gets HTTP 403 and an audit row of kind `denied`.

Enforcement is in the database, not the app. Every statement the API runs is inside a transaction that first executes `set_config('callprep.login', <login>, true)`; every view in `callprep` filters on that setting through `callprep.scope_customer`. No login set means the views return nothing. `callprep_ro` has no SELECT on the unscoped `*_all` views, so the model's `run_select` tool is bound by the same rule. The lookalike functions are SECURITY DEFINER so a rep's peer group is still the whole population; a rep sees the peers' names, class and similarity, but their sales figures come back NULL.

`user_access` changes go through `PUT /api/admin/users` (admin only, audited as kind `access_change`) and a BEFORE trigger re-checks that the session login is an enabled admin. There is no delete: disable the row so the audit trail keeps its name. Seeded 2026-09-04: it@ + paul@ admin; joel@raritanvalve.com, bill@raritanvalve.com, jim@, tom@ manager; ddickman@, ftenerovich@, kperry@, tim@, patrick@, doug@, jconvery@ reps mapped to their P21 rep ids.

Conversation history is keyed by login + session id, so one person can never continue another person's session.

Config (environment): `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET`, `CALLPREP_BASE_URL` (pins the redirect_uri to the registered https address behind nginx). The registration needs redirect URI `https://callprep.raritangroup.com/signin-oidc` (and `http://localhost:5070/signin-oidc` for local runs).

## Deploy (Hetzner)

```
pwsh -File C:\Users\it\CallPrep\deploy-hetzner.ps1            # UI + API
pwsh -File C:\Users\it\CallPrep\deploy-hetzner.ps1 -WithModel # first time / model change (148 MB)
```
On the box: `/opt/callprep` (publish output, framework-dependent linux-x64, .NET 10 runtime already installed), `/etc/callprep.env` (root-only: Entra ids + secret, Anthropic key, `PG_PASSWORD_CALLPREP`, `CALLPREP_BASE_URL`, Postgres at 127.0.0.1:5432), systemd unit `callprep` (port 5080, `MemoryMax=700M`), nginx site `callprep` for `callprep.raritangroup.com` (proxy to 5080, `proxy_buffering off` for the SSE stream, 25 MB body for audio). TLS: `certbot --nginx -d callprep.raritangroup.com` once the A record points at 178.156.238.36. Whisper runs on the box (avx2, ~270 MB RSS).

## Tools the model can call

`find_customer` (exact, then trigram + double-metaphone fuzzy fallback for misheard names), `customer_snapshot`, `peer_gap` (what same-class peers buy that this customer doesn't), `recent_activity`, `open_quotes` (default 180 days; P21 never closes quotes), `cancelled_quotes`, `class_overview`, and `run_select` (guarded free-form SELECT: `callprep.*` only, single statement, no writes, 200-row cap).

## Run locally

```
pwsh -File C:\Users\it\CallPrep\run-local.ps1 -Build
```
then open http://localhost:5070 in Edge or Chrome. For UI development run `npm run dev` in `CallPrep.Web` (proxies `/api` to 5070).

Requires in the secrets store: `ANTHROPIC_API_KEY`, `PG_PASSWORD_CALLPREP`. SSH key `%USERPROFILE%\.ssh\it_portal`.

Environment overrides: `CALLPREP_MODEL` (default `claude-opus-5`), `CALLPREP_PG_HOST` / `CALLPREP_PG_PORT` (default tunnel 127.0.0.1:15432).

## Voice

The page records the mic with the Web Audio API, downsamples to 16 kHz mono 16-bit WAV, and POSTs it to `/api/transcribe`. The API runs whisper.cpp (Whisper.net, `base.en`) with a vocabulary prompt built at startup from the top 250 customer names and product groups, so names like "Buist" transcribe correctly. Audio is discarded after transcription; the transcript is written to `audit_log` as kind `transcript`. Do not switch back to the browser's `SpeechRecognition` API: Chrome sends that audio to Google and Edge to Microsoft.

## Known data caveats

- ~13% of customers active in the last 12 months have no market class, so `peer_gap` returns nothing for them; the model is told to say so.
- Open quotes are never closed in P21. Only the last ~6 months are live; the tool and prompt both enforce that.
- Sales history in reportsdb starts 2022-03-30.

## Not yet done

- TLS certificate for callprep.raritangroup.com (waits on the DNS A record) and the redirect URI on the Entra registration (needs Application.ReadWrite).
- MCP endpoint (official C# MCP SDK) so Claude Desktop / Claude Code can use the same tools.
- Server-side refusal fallback parameter (skipped in the POC).
- Whisper `small.en` if name accuracy needs another step up (about 3x slower than `base.en`).
- Migrate ReportsApp's own username/password table to the same Entra sign-on (one employee identity everywhere).
