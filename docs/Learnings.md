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

## 13. A bridge/tool default granted to *every* stage let a vision-only call go web-searching for 10 minutes

- **What bit us:** two receipt uploads in a row hung for ~10 minutes and then failed with a 504 "claude CLI call timed out" — both at the `extract` stage, which is supposed to be a single vision read with no research involved.
- **False lead ruled out first:** `tasklist` showed several long-running `claude.exe` processes (some 6-8 hours old) and it was tempting to blame "stuck/orphaned bridge processes that never got killed on timeout." Tracing each one's parent process (`Get-CimInstance Win32_Process ... | Select ParentProcessId,CommandLine`) showed they belonged to an unrelated always-on remote-control session and to the investigating CLI session itself — nothing to do with the bridge. The bridge's own child process was already gone, correctly killed after the prior timeout. **Lesson: don't blame "orphaned" processes from a bare `tasklist`/`Get-Process` listing — trace the parent chain and command line before concluding a process belongs to the thing you're debugging.**
- **Root cause:** the bridge call for `ExtractReceiptAsync` (and `ClassifyAsync`/`AssessSeasonalityAsync`) didn't pass an explicit `allowedTools`, so it fell through to the bridge's `DefaultAllowedTools` (`"Read,WebSearch"`) plus `--max-turns 25`. A pure "read this photo and return JSON" task was therefore free to go chase `WebSearch` calls (e.g. trying to verify a retailer/product), each a real network round trip, easily consuming the whole 600s timeout. Only the price-check stage had correctly scoped itself to `allowedTools: ["WebSearch"]` explicitly; the others inherited a default meant for a different stage.
- **Fix:** pass `allowedTools: ["Read"]` explicitly on every non-search stage (`extract`, `classify`, `seasonality`) instead of relying on the bridge's default tool list.
- **Rules:**
  - When a host-side bridge/gateway has a "default tools if the caller doesn't specify" fallback, treat that default as dangerous for *every* call site — audit each call and pass an explicit, minimal tool list rather than trusting the default matches what that particular call needs.
  - "It's taking a long time" on an LLM-CLI-via-bridge task is a prompt/tool-permission question first, not a timeout-tuning question — raising the timeout would have let the same wasted web-search loop run even longer instead of failing fast.
  - A stage that shouldn't need a given tool (web search, bash, write, …) should have that tool actively withheld, not merely "not need it" — an idle capability is still a capability the model can decide to use.

## 14. A service worker that re-forwards an intercepted request breaks body-carrying uploads in an installed PWA

- **What bit us:** uploading a receipt from the installed (home-screen) PWA failed instantly with `TypeError: Failed to fetch`. The page looked healthy first — no "connecting to the server" banner — because `GET /api/health` worked fine.
- **False leads ruled out, in order:** the container was up and `GET /api/health` returned 200 both locally on `:10080` and publicly; the reverse proxy had no body-size limit anywhere near the app's 10 MB client / 15 MB server caps (global default was `2000m`) and its access log showed earlier `POST /api/analyses` calls succeeding with `202`; Kestrel had no explicit `MaxRequestBodySize` override below its 30 MB default. The decisive datapoint was that **the failing POST never appeared in the reverse-proxy access log at all** — proving the request died client-side rather than being rejected anywhere upstream. Confirming that first would have skipped every server-side check.
- **Root cause:** the published service worker intercepted *every* request and handled `/api/*` with `event.respondWith(fetch(event.request))`. Re-forwarding an intercepted `Request` that carries a body — a multipart file upload — is unreliable in standalone/installed PWA mode and throws a bare network-level `TypeError` before any HTTP response exists. A bodyless GET passes through the same code path fine, which is exactly why the health check masked it.
- **Fix:** don't intercept those requests at all. The `fetch` listener now returns early (without calling `respondWith`) for `/api/*`, so the browser handles them natively. Same net behaviour as before — API calls always hit the network — minus the fragile pass-through. The now-unreachable `/api/` branch inside `onFetch` was removed.
- **Rules:**
  - In a service worker, **"pass it through with `respondWith(fetch(event.request))`" is not a no-op** — for requests with a body it's an active risk. If you don't need to modify or cache a request, return early and let the browser do it.
  - **A browser `TypeError: Failed to fetch` means no HTTP response ever existed.** Check whether the request reached the origin *before* investigating server config — an absent access-log entry localises the fault to the client in one step.
  - Suspect the service worker whenever a bug reproduces in the **installed** PWA but not a normal browser tab, and don't let a passing bodyless health check stand in for "the API works."
  - Any change to client assets must also bump the build-stamp comment in `service-worker.published.js` (see §12) or installed clients will never pick it up.

## 15. `sc.exe`'s console password prompt can silently corrupt a service account's credentials — even after every other cause is ruled out

- **What bit us:** migrating the Bridge from a Scheduled Task to a real Windows Service (per §12), `sc.exe create ... obj= "<host>\<user>" password= *` reported success, but `sc.exe start` failed every time with **error 1069 — "The service did not start due to a logon failure."** Re-running `sc.exe config ... password= *` to re-enter the password also reported success and still failed to start.
- **False leads ruled out, in order, each independently confirmed and each NOT the cause:**
  - **Wrong account name format.** `sc.exe qc` showed `SERVICE_START_NAME : .\<user>` even after configuring `<host>\<user>` explicitly — this is expected Windows normalisation of the local computer name to `.`, not a misconfiguration. Not worth chasing.
  - **Missing "Log on as a service" right.** Granted via `secpol.msc` → Local Policies → User Rights Assignment. Still 1069. Verified the grant actually took effect with `secedit /export /areas USER_RIGHTS /cfg C:\check.cfg` — but plain `findstr` against that file silently found nothing because the export is UTF-16; switching to PowerShell's `Select-String -Path C:\check.cfg -Pattern "ServiceLogonRight"` showed the right genuinely was granted (the account was listed under `SeServiceLogonRight`) with no competing `SeDenyServiceLogonRight` entry.
  - **Wrong password.** Isolated this from everything else with `runas /user:<host>\<user> cmd` — it succeeded (a new shell opened), proving the password itself was correct outside the service-logon path.
  - **Near-false-lead:** running `sc.exe start` *inside* that runas-opened window gave a different error, "OpenService FAILED 5: Access is denied." That's not a service problem — `runas` opens a **non-elevated** shell even for an admin account, so any `sc.exe` call there hits UAC regardless of the service's real state. Had to go back to the original elevated window to get a meaningful result (which was still 1069).
- **Fix:** stopped using `sc.exe`'s console `password= *` prompt entirely and set the service's logon credentials via the **Services GUI** instead — `services.msc` → the service → Properties → **Log On** tab → This account → retype the password in both the Password and Confirm password fields → OK. The service started immediately on the next attempt with the exact same account and password. This strongly implies `sc.exe`'s console secure-input prompt was silently mis-capturing the password on every attempt when invoked from a PowerShell host — it always reports `SUCCESS` regardless, since `sc.exe config`/`create` never validates a password, only an actual start attempt does.
- **Also:** this whole migration had to be handed to the user to run themselves — the agent session's own shell tools were not elevated, and `sc.exe create`, `[Environment]::SetEnvironmentVariable(...,'Machine')`, and even reliably killing the old process all failed or errored without admin rights.
- **Rules:**
  - `sc.exe create`/`config ... password= *` reporting `SUCCESS` proves the syntax was valid, **not** that the password was accepted — only a real start attempt validates it. Don't stop troubleshooting on that success.
  - To isolate "is the password wrong" from "is something else wrong" for a 1069, use `runas /user:DOMAIN\user cmd` as an independent check — but remember it opens a **non-elevated** shell, so don't run further `sc.exe` diagnostics inside that window and mistake an Access-Denied-from-no-elevation for a service-specific error.
  - `SERVICE_START_NAME : .\user` after configuring `HOSTNAME\user` is expected local-account normalisation — don't chase it as a bug.
  - `findstr` against a `secedit /export` file can silently return nothing because the export is UTF-16; use PowerShell's `Select-String` instead when checking exported security policy for a specific right.
  - If a service account's password, "Log on as a service" right, and account-name resolution are all independently verified correct and `sc.exe ... password= *` from a console still won't start the service, stop fighting the CLI prompt — set the credentials via **`services.msc`'s GUI Log On tab** instead, which uses a real dialog field rather than a console secure-input prompt.
  - Windows service-account/credential setup is not something to attempt from an unelevated agent shell — hand it to the user as a script plus a clear step-by-step verification checklist, and expect to debug interactively over several rounds.

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
9. Every bridge/gateway call site passes its own explicit, minimal tool list — never rely on a shared "default tools" fallback.
10. When registering a Windows Service under a user account, set its Log On credentials via `services.msc`'s GUI, not `sc.exe ... password= *` — the console prompt can silently corrupt the password while still reporting success.
