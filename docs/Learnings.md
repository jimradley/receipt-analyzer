# Learnings — Receipt Analyzer build & deploy

Hard-won lessons from the "make it real" work (CI, containerise, go live behind a reverse proxy),
captured so we don't relearn them. Each entry: *what bit us → root cause → fix → rule for next time.*

> This file is committed, so it's deliberately scrubbed of secrets/infra specifics — domains,
> hosts, and IPs appear as placeholders (`<your-domain>`, `<origin-ip>`). The lesson is kept; the
> identifying detail is not.

---

## 1. Blazor WASM will not publish inside the `dotnet/sdk` container (`WASM0005`)

- **What bit us:** `docker compose build` failed with `WASM0005: Unable to resolve WebAssembly runtime pack version`. Persisted across multiple SDK image tags, pinned versions, dropping `--no-restore`, and `dotnet workload install wasm-tools`.
- **Root cause:** the in-container SDK image can't resolve the `browser-wasm` runtime pack; the host SDK published the same project fine.
- **Fix:** **runtime-only Dockerfile.** Publish on the host first (`dotnet publish … -o publish`), then the Dockerfile just `COPY publish/ ./` onto the ASP.NET runtime image.
- **Rule:** For Blazor WASM (or anything needing the wasm workload), **publish on the host and ship the output**; don't build WASM in a stock SDK container. Document the two-step build (`dotnet publish` → `docker compose build`) next to the Dockerfile, because the boot script uses `--no-build` and assumes the image already exists.

## 2. Don't pin SDK/runtime image tags you haven't confirmed exist

- **What bit us:** a pinned patch-level SDK tag 404'd — the registry only had a subset of patch versions.
- **Rule:** the host SDK version is **not** guaranteed to exist as a registry image tag. Check the tag exists before pinning, or use a floating minor (e.g. `9.0`). (Moot once we went runtime-only, but the trap is general.)

## 3. `appsettings.json` Kestrel endpoints silently override `ASPNETCORE_URLS`

- **What bit us:** the container mapped its published port but health checks hit nothing; the app was listening on a different (dev) port inside the container.
- **Root cause:** a leftover `"Kestrel": { "Endpoints": { "Http": { "Url": "…:<dev-port>" } } }` block in `appsettings.json` takes precedence over the `ASPNETCORE_URLS` env var.
- **Fix:** removed the Kestrel block; the container now honours `ASPNETCORE_URLS`.
- **Rule:** for containerised ASP.NET, **drive the listen port from `ASPNETCORE_URLS`/env, not a hardcoded `appsettings` Kestrel endpoint.** A committed dev port will quietly win over the container's env. Keep dev-only ports in `appsettings.Development.json` / launchSettings (gitignored).

## 4. Every project in a multi-project solution must be in the Dockerfile COPY/restore set

- **What bit us:** `NETSDK1004` — a project added after the Dockerfile was written wasn't copied, so restore couldn't find it.
- **Rule:** when adding a project to the solution, update the Dockerfile's csproj COPY list (or restore the whole `.sln`). A `.dockerignore` is mandatory too — without it the host `bin/`/`obj/` get copied in and clobber the container restore.

## 5. A lingering dev server locks build outputs

- **What bit us:** `MSB3027`/`MSB3021` — couldn't copy/overwrite DLLs.
- **Root cause:** a manual-test `dotnet run` server was still holding the build outputs.
- **Rule:** stop any running instance (check the dev port / kill the PID) before rebuilding. Don't leave manual-test servers running across tasks.

## 6. A `try/catch` around a pipeline stage can hide a `NotImplementedException` forever

- **What bit us:** the Seasonality report section was **always empty** and nobody noticed for ages.
- **Root cause:** the app defaulted to one provider, but that provider's `AssessSeasonalityAsync` just `throw new NotImplementedException()`. The pipeline wrapped the stage in try/catch, so the failure was swallowed silently. Only the *non-default* provider implemented it.
- **Fix:** implemented seasonality for the default provider (and de-duplicated the embedded-resource loader into a shared helper).
- **Rules:**
  - **Don't ship an interface impl that throws `NotImplementedException` on the default code path.** If a provider is selectable at runtime, the *default* provider must implement everything the pipeline calls.
  - **A catch-all around an optional stage must log loudly** (warn/error with the exception), never swallow silently. A "feature that's quietly always empty" is the result.
  - When adding a method to a provider-agnostic interface, implement it for **all** providers in the same change, or make the gap fail fast/visibly.

## 7. Reverse-proxy + CDN cert gotcha: can't issue an HTTP-01 cert while the record is proxied

- **What bit us:** browsing the new hostname gave **Error 525 — SSL handshake failed**; the Let's Encrypt cert request in the proxy manager wouldn't complete.
- **Root cause:** the host was a CDN-**proxied** CNAME. While proxied, the name resolves to the CDN's IPs, so the HTTP-01 challenge hits the CDN, not the origin — and 525 is the CDN failing to TLS to an origin that has no cert yet. Chicken-and-egg.
- **Fix / the dance:** set the record to **DNS-only** (un-proxied) so it resolves to the real origin IP → request the cert in the proxy manager (the challenge now reaches the origin on :80) → switch the record **back to proxied**. Verified: DNS-only resolved to `<origin-ip>`; proxied resolved to the CDN's IPs.
- **Rules:**
  - New proxied hostname → **un-proxy first, issue the cert, then re-proxy.** Don't request the cert while proxied.
  - If a dynamic-DNS script updates only the **root** A record, sub-hosts that are CNAMEs to the root follow it automatically — **no per-host DDNS edit is needed**.
  - Toggling the proxy flag via the DNS provider's API is faster and scriptable vs the dashboard.

## 8. Keep the domain / infra out of committed files (this file included)

- **Requirement:** the live hostname and infra specifics must never enter a public repo or its history.
- **What works:** every artifact that names the real host lives **outside** the repo — the deploy compose, the proxy/DNS config (GUI/API), and any local-only notes. Committed config stays neutral (relative paths, env-var *names* only; real keys/domain in the server `.env`).
- **Rules:**
  - **Before every push:** grep the working tree for the domain / host markers and run a sensitive-info scan over what will be committed — confirm zero hits in **files and history**.
  - In-repo docs use placeholders (`<your-domain>`, `<origin-ip>`), never the real values — as this file does.
  - Default new docs to scrubbed-and-scanned before committing.

---

## What worked / keep doing

- **Runtime-only image + host publish** for Blazor WASM — reliable and fast; keep this split.
- **Env-driven config** (`ASPNETCORE_URLS`, provider via env) over hardcoded settings — fewer container surprises.
- **Scrub + scan-gate before pushing** — caught the leak risk every time; cheap insurance.

## Quick checklist for the next "containerise + go live" job

1. Publish WASM on the host; runtime-only Dockerfile; keep `.dockerignore` current.
2. Listen port from `ASPNETCORE_URLS`/env — no hardcoded Kestrel endpoint in committed config.
3. All solution projects in the Dockerfile restore set; stop any dev server before building.
4. Default provider implements every interface method the pipeline calls; optional stages log on failure.
5. New proxied hostname: un-proxy → issue cert → re-proxy. Root-only DDNS; CNAMEs follow.
6. Wire the compose into the boot script (`--no-build`, so the image must pre-exist).
7. Scrub + sensitive-info scan before every push; use placeholders for any domain/host/IP in docs.
