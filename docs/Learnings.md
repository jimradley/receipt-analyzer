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
- **Addendum — killing a scheduled-task-owned process from a sandboxed agent tool can silently no-op:** the Bridge's host process (started by a logon-triggered Scheduled Task) held a file lock that blocked `dotnet publish`. `Stop-Process -Force`, `taskkill /F`, and even a `dangerouslyDisableSandbox` PowerShell call all reported success (or "no running instance") while `tasklist`/`Get-Process` kept showing the same PID and the publish kept failing with the same lock. **Root cause:** the agent's shell tools run in a context that can *see* the interactive session's process table but can't reliably *act* on it — termination calls return without error yet don't take effect. **Rule:** don't trust a green exit code from `Stop-Process`/`taskkill` run via an agent tool as proof a process is actually gone — verify by retrying the operation that needed it dead (here, the publish), not by re-querying the process list, which can also be stale/inconsistent across tool invocations. If a stuck process blocks a rebuild and repeated kill attempts don't clear the lock, stop fighting it from the agent session and have the human kill it (or reboot the host) directly.

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

## 9. Blazor WASM trimming breaks reflection-based System.Text.Json on the WebAuthn (Fido2) types

- **What bit us:** the passkey ceremony failed in production with `ConstructorContainsNullParameterNames, Fido2NetLib.PublicKeyCredentialRpEntity` / `SerializationNotSupportedParentType, System.Object Path: $.` Dev (un-trimmed) worked; only the published WASM broke — so it looked like a stale-cache problem and wasn't.
- **Root cause:** Release WASM is **trimmed**, which strips constructor *parameter names* from the Fido2 model assembly. System.Text.Json's reflection serializer needs those names to bind parameterised constructors, so it throws at runtime. Two reflection paths hit it: (a) marshalling the strongly-typed `CredentialCreateOptions` / `AssertionOptions` / raw-response objects across **JS interop** (Blazor uses the JSRuntime's default reflection serializer), and (b) the models' own `.FromJson()` / `.ToJson()`.
- **What did NOT fix it:** `<TrimmerRootAssembly Include="Fido2.Models" />` — rooting the assembly keeps the *types* but the trimmer still drops parameter-name metadata. Verified ineffective against a genuinely fresh deploy.
- **Fix:** keep reflection STJ off the Fido types entirely. Use the package's source-generated `FidoBlazorSerializerContext` for every (de)serialisation, and pass **JSON strings** (not objects) across the JS-interop boundary — `JsonSerializer.Serialize(opts, ctx.CredentialCreateOptions)` → JS `JSON.parse` → ceremony → JS `JSON.stringify` → `JsonSerializer.Deserialize(json, ctx.AuthenticatorAttestationRawResponse)`. Source-gen contracts are trim-safe because the metadata is emitted at compile time.
- **How it was caught:** a CDP **virtual authenticator** (`WebAuthn.addVirtualAuthenticator`, `transport:'internal'`, `isUserVerified:true`) driven via Playwright reproduced the full enrol + unlock ceremony headlessly against the live site — no phone needed. A fresh in-memory browser profile also rules out service-worker cache as the cause.
- **Rules:**
  - In Blazor WASM, anything crossing JS interop or STJ that isn't your own simple POCO needs a **source-generated `JsonSerializerContext`** — assume reflection serialization will break under trimming.
  - Don't trust `TrimmerRootAssembly` to preserve constructor parameter names; prefer source-gen over fighting the trimmer.
  - Reproduce WebAuthn flows with a **CDP virtual authenticator** before declaring a passkey bug a caching issue.

## 10. One batched LLM web-search call silently starves most of the items in it

- **What bit us:** price checking felt "weak — skipping or ignoring items". Every cache-miss item went into **one** agent call sharing a fixed web-search budget (Claude `max_uses: 8` for a whole receipt; one OpenAI Responses call), so on a 15+ item receipt the model quietly nulled whatever it couldn't afford to search. Three amplifiers hid it: a null price meant *both* "nothing cheaper" and "couldn't find it" and got **cached for 7 days**; one malformed response threw away the whole batch (job still "succeeded"); and items the model omitted from its JSON weren't back-filled, so they vanished without trace.
- **Root cause:** batch size and search budget didn't scale together, and the result schema had no way to say *why* an item had no price — absence of evidence was indistinguishable from evidence of absence, then durably cached.
- **Fix:** chunk the batch (default 4/call) with a per-chunk search budget; one **individual retry pass** with a "search harder" hint; a per-item `Outcome` (`cheaper-elsewhere` / `already-best` / `not-found` / `unchecked`) with validator back-fill so every requested item comes back exactly once; ask for the best price found **even when it isn't cheaper**; cache not-found on a 1-day TTL (vs 7 for prices) and never cache errors; render a coverage line so gaps are visible.
- **Rules:**
  - When an LLM call fans out over N items with a shared tool budget, **chunk so budget ∝ items** and make failure lose only its chunk.
  - Never let "no answer" share a representation with "answer: nothing found" — and give negative results a much shorter cache TTL than positive ones.
  - Validate LLM list responses by **back-filling against the request list**, not by trusting the response to be complete.

## 11. Per-stage LLM plumbing assumptions break when a stage makes multiple calls

- **What bit us (latent):** usage merging did `RemoveAll(stage) + Add(entry)` per call, so a stage with >1 call kept only the **last** call's tokens — re-extraction was already being undercounted, and chunked price checks would have made the cost telemetry badly wrong.
- **Fix:** group the attempt's usage by stage and **sum per (stage, model)** before replacing; the report footer lists distinct models (stages can now run different models via `Agent:PriceCheckModel`).
- **Also learned:** on the OpenAI Responses API the web-search tool type is **model-dependent** (`web_search` for gpt-5-family, `web_search_preview` for gpt-4o — the new type 400s on old models and vice versa), and search results bill as *input* tokens (~60–90K per searching call), so a cheap-per-token model matters more than it looks. Each searching call runs 1–3 min; budget pipeline latency accordingly.
- **Rule:** any "one entry per stage" assumption (usage, retries, logging) must survive a stage making N calls — sum, don't overwrite; and pin tool variants per model family, not globally.

## 12. A logon-triggered Scheduled Task is not a reliable way to run a headless background host

- **What bit us:** the Bridge was started via a Scheduled Task (`LogonType=Interactive`, action `powershell.exe -WindowStyle Hidden -File Start-Bridge.ps1` → launches the console-subsystem `.exe`) so it would run in the background at boot. In practice a console window kept appearing, and the process only started once someone actually logged on interactively.
- **Root cause:** `-WindowStyle Hidden` only hides the PowerShell host's own window; a console-subsystem child process launched from it is not guaranteed to stay attached/hidden (flaky in practice on Windows 11/Windows Terminal). And an "at logon" trigger inherently depends on an interactive session existing at all.
- **Fix:** host the app with ASP.NET Core's `Microsoft.Extensions.Hosting.WindowsServices` package (`builder.Host.UseWindowsService(o => o.ServiceName = "...")`, a no-op outside the Service Control Manager so `dotnet run` is unaffected) and register it as a real Windows Service (`sc.exe create ... start= delayed-auto`). A service has no window ever, doesn't need anyone logged on, and gets SCM-managed crash-restart (`sc.exe failure ... actions= restart/...`) instead of a Scheduled Task's weaker restart semantics. Because a service doesn't inherit an interactive session's env vars, a secret previously set by the launcher script (`RECEIPT_BRIDGE_KEY`) had to move to a **machine-level** env var (`setx NAME value /M`) for the service process to see it.
- **Rule:** for any "must always be running in the background, no user should ever see a window" host process on Windows, reach for `UseWindowsService()` + a real service from the start — don't try to make a Scheduled Task behave like one.

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
8. Any always-on headless host process → `UseWindowsService()` + a real Windows Service, not a Scheduled Task.
