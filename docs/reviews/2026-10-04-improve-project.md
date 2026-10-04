---
title: "Receipt Analyzer - Improve-Project Review"
description: "Evidence-based review of code, deployment, security, resources and features, with a prioritised backlog."
status: published
created: 2026-10-04
updated: 2026-10-04
tags: [review, backlog, receipt-analyzer]
---

# Receipt Analyzer - Improve-Project Review (2026-10-04)

## Overview

Full review of `C:\Src\Receipt Analyzer` (branch `fix/bridge-restricted-readonly`, HEAD `6f014c6`, 11 files modified and uncommitted) plus the live deployment (container `receipt-analyzer`, host Bridge service, `C:\ReceiptAnalyzer`, state under `C:\AI\Projects\Shopping\.state`). Review only: nothing was changed, deployed, deleted or committed except this file. No earlier review exists, so every item is new.

**Headline:** the system is healthy and well tested (247/247 pass, live `/api/health` 200, Bridge service running, container 95 MiB / ~0% CPU). The real risks are (1) unthrottled anonymous auth endpoints guarding a full-takeover enrol code, (2) a Bridge that listens on all interfaces with only a shared key, (3) uncommitted work that the next `docker compose build` will ship, and (4) several small drift/hygiene items.

## Findings

Severity: High / Medium / Low. Basis: **R** reproduced/measured, **C** code-supported, **H** hypothesis.

### Medium

**M1. No rate limiting on `register/begin` enrol-code check or any anonymous auth endpoint (C).**
`src/ReceiptAnalyzer.Api/Auth/AuthEndpoints.cs:46-50`, `:99-113`. `EnrollCodeValid` uses a constant-time compare but there is no attempt limit, lockout or `AddRateLimiter` anywhere in `Program.cs`. A correct code lets an anonymous caller enrol a passkey (full access to Receipts and, via SSO, Server Control). Trigger: unlimited guesses against the public hostname. Consequence: bounded only by code entropy. Fix: ASP.NET Core `AddRateLimiter` fixed window (e.g. 5/min/IP, 20/hour) on `/api/auth/register/*` and `/login/*`; consider a long random code and rotating it after first use. Note Cloudflare sits in front, so `RemoteIpAddress` needs correct forwarded handling (see L1).

**M2. Bridge listens on `0.0.0.0:5095`, firewall rules for it allow the Public profile (R).**
`src/ReceiptAnalyzer.Bridge/Program.cs:10`; `Get-NetTCPConnection` shows `0.0.0.0:5095`; inbound allow rules named `ReceiptAnalyzer.Bridge` exist on the Public profile. The endpoint runs an LLM CLI with the account's subscription and reads files via `PathMap`. Only protection is `X-BRIDGE-KEY` (constant-time compare, good). Consequence: anyone on the LAN who learns the key can spend the Max/Codex allowance. Fix: bind to the Docker bridge/host-gateway address or `127.0.0.1` plus `host.docker.internal` reachability test, and scope the firewall rule to the Docker subnet. Verify container reachability after the change.

**M3. Uncommitted work will ship on the next rebuild; the Bridge diff may not match what is deployed (R).**
`git status` shows 11 modified files (PWA `Home.razor`, `Stores.razor`, CSS, `ReportRenderer`, `PurchaseHistoryStore`, `WineCatalog`, Bridge `Program.cs`). The running image is 24 h old; the deployed Bridge DLL is dated 2026-09-27 while the Bridge source diff (prompt via stdin instead of `-p <prompt>`) is uncommitted, so the live Bridge probably lacks it. The diff also cites `UtilitiesSummaryService`, i.e. the Bridge now serves another app's long prompts, which `CLAUDE.md` does not mention. Fix: review, test and commit or stash; bump the service-worker stamp if any `.razor`/`.css` ships (CLAUDE.md rule); record the shared-Bridge consumer in docs.

### Low

**L1. `UseForwardedHeaders` trusts any sender (C).** `Program.cs:84-87` clears `KnownProxies/KnownNetworks`. The container port `10080` is host-published, so a LAN client can spoof `X-Forwarded-For/Proto`. Impact is limited today (no IP-based logic) but it blocks safe per-IP rate limiting (M1). Fix: allow only the NPM address/subnet.

**L2. Cookie-auth mutations have no CSRF defence beyond SameSite=Lax (C).** `jr_auth` has `Domain=.jamesradley.co.uk`, so any sibling subdomain can issue same-site POSTs. Mutations (`POST /api/analyses`, `/api/inventory/price-refresh`, `DELETE ...`) accept multipart/simple requests. Fix: require a custom header (e.g. `X-Requested-With`) or antiforgery token on cookie-authenticated mutations; API-key requests exempt.

**L3. Anonymous challenge cache is unbounded (C).** `login/begin` writes an `IMemoryCache` entry per call (5-minute TTL) with no `SizeLimit`. Slow memory growth under abuse; the rate limiter in M1 also closes this.

**L4. Transitive `Microsoft.Bcl.Memory 9.0.0` has a High advisory (R).** GHSA-73j8-2gch-69rq / CVE-2026-26127, out-of-bounds read on malformed Base64Url, fixed in 9.0.14. It resolves only in `ReceiptAnalyzer.Pwa` (client WASM), not the server, so reachable impact is a client-side crash at most. Fix: pin `Microsoft.Bcl.Memory` 9.0.14+ in the Pwa csproj, or bump `Microsoft.AspNetCore.Components.WebAssembly` within 9.0.x (installed 9.0.15 yet still resolves 9.0.0).

**L5. No container healthcheck (R).** `docker inspect` health is `null`; restart policy is `unless-stopped` with no memory cap. `autoheal` runs on the host but cannot act on this container. Also `/api/health` reports only liveness, not Bridge reachability, so "Bridge down" shows as healthy until a receipt fails. Fix: compose `healthcheck` on `/api/health`; add an optional authenticated `/api/health/deep` that pings Bridge `/health`.

**L6. Stale diagnostic files in the Bridge temp folder (R).** `.state/bridge-tmp` holds 7 JPEGs (7.7 MB, dated 2026-08-06 to 08-08, manual `crop*/rotated*` probes). Not produced by the pipeline (which deletes its temp files) but they are receipt images retained indefinitely. Delete after a glance; add a startup sweep of files older than 1 day.

**L7. Receipt archive has no retention or backup note (C).** 60.9 MB in `Receipts/`, growing roughly one image per receipt. Fine now; document that it is the only copy and confirm it is in the backup set.

### Documentation and drift

- `CLAUDE.md` says 158 tests (twice) and 182 tests; actual is **247**. Magick.NET documented as 14.14.0; installed **14.16.0** (14.17.2 available). `Learnings.md` §23's rule vs the line "Test suite is 158 tests" is stale text, not a defect.
- `CLAUDE.md` Project-layout table omits `Spend.razor`/`/api/spend` and the shared Bridge consumers.
- `docs/Learnings.md` §15 mentions `sc.exe` credential handling but `Install-BridgeService.ps1` in `C:\ReceiptAnalyzer` is not referenced from the repo; keep a scrubbed copy in `scripts/` or document it.

## Resource use and cleanup candidates (measured)

| Item | Size / state | Verdict |
|---|---|---|
| Container `receipt-analyzer` | 94.8 MiB RAM, 0.01% CPU, image 811 MB | Fine; no limit set (L5) |
| Bridge process | ~28 MB working set | Fine |
| Image `receipt-analyzer:pre-20261003` | 810 MB, 6 days old | Rollback tag; keep until next release is verified, then remove (disk only) |
| `.state/` total | ~2.4 MB (+ `bridge-tmp` 7.7 MB) | Small |
| `.state/purchase-history.json.*.bak` x10, `price-cache.json.bak-*`, `Agent.md.bak-20260828-143715` | ~1.3 MB, dated Jun-Sep | Superseded backups sitting in live state dir; move into `.state/backups/` or delete once the Oct ledger is trusted. Never delete without confirming current file is healthy |
| `C:\ReceiptAnalyzer\Bridge-backup-20260927-112402`, `Bridge-staging`, `docker-compose.yml.pre-batchdelay.bak`, `swap-bridge.ps1`, `bridge-swap.log` | small | Rollback artefacts; remove after Bridge is verified (`Bridge` is the active publish target, do not touch) |
| `C:\ReceiptAnalyzer\.playwright-cli`, `stores-page.png`, `docs/upload-mobile.png` | ~70 KB each | Low value; `docs/upload-mobile.png` is tracked, confirm it is referenced |
| `Receipts/` archive | 60.9 MB | Keep (only copy) |
| Vendored `wwwroot/lib/bootstrap` | ~55k lines tracked | Not used by the custom CSS? Verify references before removal; saves repo size only |

Disk savings here are tiny; none of this affects RAM.

## Workflow and feature critique

Primary workflows (upload -> staged durable pipeline -> reports; Stores/Staples/Spend; history) are solid: resumable stages, idempotent content-hash jobs, per-item price outcomes, sanity guards and tests for each. Gaps worth closing:

1. **Failure visibility.** A receipt that fails (extract mismatch, Bridge down, allowance limit) is only visible if the user opens the app. Add an outcome notification (the user already has Telegram) on Failed jobs and on a refresh queue stalling, plus a "retry" action for terminal failed jobs (Learnings §17 notes identical re-uploads replay the cached failure).
2. **Input archived before the first fallible stage.** Learnings §17 flagged this; confirm it is implemented. `failed-jobs-archive` exists, which suggests a partial fix. If not done, do it.
3. **Provider health on the Costs page.** Show last successful Claude vs Codex-fallback use and current Bridge reachability, so a silent switch to the fallback is noticed.
4. **Duplicate/near-duplicate receipt handling.** Content hash catches only byte-identical images; a re-photographed receipt creates a second purchase. A same retailer+date+total check before ledger merge would prevent double-counting in Spend and Staples cadence.
5. **Not recommended now:** a second provider rewrite, multi-user support, or a generic admin shell. They broaden authority without a stated need.

## Prioritised backlog

| # | Item | Type | Impact | Effort | Prereq | Accepted when |
|---|---|---|---|---|---|---|
| 1 | M3: review/commit/stash the 11 modified files; decide on Bridge stdin change; redeploy Bridge if kept | Hygiene | High (avoids shipping unreviewed work) | S | Tests green | `git status` clean; deployed Bridge DLL built from committed source; SW stamp bumped if PWA ships |
| 2 | M1+L3: rate limit auth endpoints | Defect | High | S | L1 for per-IP accuracy | 6th `register/begin` in a minute returns 429; login still works after a pause; test via `WebApplicationFactory` |
| 3 | M2: bind Bridge narrowly + scope firewall | Defect | High | S-M | Container reachability test | Bridge unreachable from another LAN host; a receipt still processes end to end |
| 4 | L1: restrict forwarded-header trust to NPM | Defect | Medium | S | NPM address | Spoofed `X-Forwarded-For` from LAN ignored |
| 5 | L5: compose healthcheck + deep health incl. Bridge | Defect/feature | Medium | S | none | `docker ps` shows healthy/unhealthy correctly; stopping Bridge turns deep health red |
| 6 | Failure notification + retry for failed jobs | Feature | Medium-High | M | Telegram hook reuse | A forced extract failure sends one message; retry reprocesses without re-upload |
| 7 | L2: header/antiforgery check on cookie mutations | Defect | Low-Medium | S-M | PWA client sends header | Cross-origin form POST rejected; PWA uploads unaffected |
| 8 | L4: pin `Microsoft.Bcl.Memory` >= 9.0.14; Magick.NET to 14.17.2 | Modernise | Low | S | tests | `dotnet list package --vulnerable` clean |
| 9 | Docs drift (test counts, Magick version, layout table, Bridge consumers) | Docs | Low | S | none | CLAUDE.md matches reality |
| 10 | Cleanup: `bridge-tmp` strays, old `.bak` files, Bridge staging/backup, old image tag | Cleanup | Low | S | Confirm current state healthy | Items removed; app and Bridge unaffected |
| 11 | Near-duplicate receipt detection | Feature | Medium | M | none | Same retailer/date/total re-photo is flagged, not double-counted |
| 12 | Provider health on Costs page | Feature | Low-Medium | S-M | Bridge status endpoint | Page shows last Claude/Codex success and Bridge state |

**Do Now:** 1, 2, 3, 4. **Next:** 5, 6, 7, 8, 9. **Later:** 10, 11, 12. Item 10 waits so rollback artefacts remain until the release in item 1 is verified.

## Checks performed

| Category | Status | Notes |
|---|---|---|
| Correctness and state | Partial | Read auth, upload, bridge and job flow code and Learnings; did not re-trace every pipeline stage or concurrency path in `InventoryPriceRefreshService` (296 lines) |
| Security and authority | Checked | Route auth audited (all data routes `RequireAuthorization`; health and auth anonymous); Bridge exposure measured; no secret values read or printed |
| Dependencies, delivery, recovery | Partial | NuGet vulnerable/outdated run; advisory verified at GitHub; backup/restore of `.state` **not verified**; CI only builds and tests (no vulnerability or format step) |
| Performance, resources, observability | Checked | Container/process/disk measured; log rotation and Docker log size not inspected |
| UX and features | Partial | Code/doc critique only. **No browser session used**, so mobile layout, accessibility and error states were not exercised; uploads and enrolment were not run against live (no paid or mutating calls) |
| Tests and maintainability | Checked | 247/247 pass locally; agent classes remain untested against HTTP mocks (known, per CLAUDE.md) |
| Files and knowledge hygiene | Checked | Sizes and dates above; nothing removed |
| Sensitive-info scan | Not run | Not requested; repo is public, so run `sensitive-info-scan` before the next push |

## References

- `CLAUDE.md`, `docs/Learnings.md` (sections 12-25 relevant)
- GHSA-73j8-2gch-69rq / CVE-2026-26127 (Microsoft.Bcl.Memory)

## Changelog

- 2026-10-04 - Initial review.
