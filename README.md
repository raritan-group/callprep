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
| `sql/005_rep_list.sql` | The week's list: `list_settings` thresholds, five trigger views/functions over the scoped views. |
| `sql/006_customer_address.sql` | Customer address, city, state, zip, phone on `customer_all` / `customer`, from the P21 address the reports sync carries since 2026-09-09. |

## Data

Source is the ReportsApp Postgres on the Hetzner box (`reportsdb`), which `reports_push.ps1` refreshes from P21 every 15 minutes. No P21 connection, no P21 lock exposure, no new SQL Server login.

Views (all read-only, owned by postgres):
- `customer` (id, name, market class, rep, address/city/state/zip/phone from P21 `address.id = customer_id`, synced by `reports_push.ps1` since 9/9/2026) · `sales_line` (invoices since 2022-03-30, sell price only) · `open_quote_line` · `open_order_line` · `cancelled_quote_line`
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

## The week's list (dashboard)

The landing screen is a list, not an empty chat. Five hard-coded triggers run over the same scoped views the chat uses (`sql/005_rep_list.sql`), so a rep's list is their book and a manager's is everyone. No model is involved in building it: the same account is on the list for the same reason every morning, and every row carries the question that opens the chat ("Prep").

| Section | Rule | Knobs (`callprep.list_settings`) |
|---|---|---|
| Quotes to chase | open quotes 7 to 90 days old, at least $2,500, biggest first | `quote_min_age_days`, `quote_max_age_days`, `quote_min_value` |
| Gone quiet | days since last invoice exceeds the account's own median gap × 1.5 (min 21 days, max 180), 6+ invoice days in 12 months | `quiet_*` |
| Due to reorder | same cadence math per customer × product group, 4+ buys in 24 months, $2,000+ a year in the group, not past 4× the usual gap | `reorder_*` |
| What similar customers buy that they don't | `lookalike_gap()` for the top 10 accounts by 12-month sales, one row per account, recapture before never-bought | `gap_top_accounts`, `gap_min_pct` |
| New accounts | first invoice within 45 days | `new_account_days` |

`GET /api/today` builds it (cached 30 minutes per login, `?refresh=true` to rebuild; about 7 s for a rep's book), `GET /api/admin/list-settings` reads the knobs. Thresholds are rows in a table so sales can change them without a deploy; an admin UI for that is not built yet (edit the table).

## Tools the model can call

`find_customer` (exact, then trigram + double-metaphone fuzzy fallback for misheard names; returns the customer's own address), `customers_near` (town, 5-digit zip with its neighbours, or state: "I'm visiting X, who else is in the area"), `customer_snapshot`, `peer_gap` (what same-class peers buy that this customer doesn't), `recent_activity`, `open_quotes` (default 180 days; P21 never closes quotes), `cancelled_quotes`, `class_overview`, and `run_select` (guarded free-form SELECT: `callprep.*` only, single statement, no writes, 200-row cap).

## Run locally

```
pwsh -File C:\Users\it\CallPrep\run-local.ps1 -Build
```
then open http://localhost:5070 in Edge or Chrome. For UI development run `npm run dev` in `CallPrep.Web` (proxies `/api` to 5070).

Requires in the secrets store: `ANTHROPIC_API_KEY`, `PG_PASSWORD_CALLPREP`. SSH key `%USERPROFILE%\.ssh\it_portal`.

Environment overrides: `CALLPREP_MODEL` (default `claude-opus-5`), `CALLPREP_PG_HOST` / `CALLPREP_PG_PORT` (default tunnel 127.0.0.1:15432).

## Tests

Three tiers in `CallPrep.Tests/` (xUnit, .NET 10) plus a browser suite in `CallPrep.Web/e2e/` (Playwright). Runner: `tests
un-tests.ps1` (loads the secrets into the process, opens the SSH tunnel, writes a `.trx` to `tests
esults\`).

| Tier | What it proves | Needs | Run |
|---|---|---|---|
| Unit (`Unit/`) | `SqlGuard` refuses writes, other schemas, comments, `set_config`, `lo_*`, `pg_read_*`; login normalisation; all nine tool schemas are well-formed | nothing | `pwsh -File tests
un-tests.ps1 -Unit` |
| DB integration (`Integration/`) | every tool, for every demo customer, returns valid JSON with no `{error}` (the deterministic half of a demo rehearsal); fuzzy name fallback; `run_select` row cap and the revoked `*_all` views; rep vs admin scoping, fail-closed with no login, `user_access` write refused for a rep | tunnel to reportsdb | `pwsh -File tests
un-tests.ps1` |
| HTTP e2e (`E2E/ApiTests`) | the real `Program.cs` hosted in-process with only the Microsoft sign-in swapped for an `X-Test-Login` header: health, static UI, 401 JSON on `/api/*`, `/signin` redirect to Microsoft with the pinned `redirect_uri` + PKCE, 403 for unknown/disabled/service logins, `/api/me` scope, admin-only endpoints and every `PUT /api/admin/users` validation path, Whisper transcription of `models/test_buist.wav` | tunnel | same |
| Live chat (`E2E/ChatTests`) | full `/api/chat` SSE stream with the model: gap question + open-quotes follow-up per demo customer, rep asking about another rep's account, a free-form `run_select` question, Stop mid-answer then recovery in the same session | tunnel + Anthropic credits (about $0.15 per question) | `pwsh -File tests
un-tests.ps1 -Live` |
| Browser (`CallPrep.Web/e2e/`) | the page in Chromium against the live site (or `CALLPREP_URL=http://localhost:5070`): sign-in state, chips, mic button, transcription through the browser session, the demo questions for several customers, Stop, New conversation | one interactive Microsoft sign-in saved to `e2e/.auth/state.json` (`npx playwright test --project=setup`), then `npx playwright test` | see `playwright.config.ts` |

Demo customer set: `-Customers 'Buist;Coppola Services;Middlesex Water'` (default covers every market class, the null-class case and the hero account). Test runs write to `callprep.audit_log` like any other use, with session ids prefixed `test-`.

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
- Answers are shown as plain text: the model's `**bold**` markers appear literally in the bubble. A markdown renderer (react-markdown) would fix it.
- Whisper `small.en` if name accuracy needs another step up (about 3x slower than `base.en`).
- Migrate ReportsApp's own username/password table to the same Entra sign-on (one employee identity everywhere).
