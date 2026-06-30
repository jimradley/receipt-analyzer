using ImageMagick;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Api.Auth;
using ReceiptAnalyzer.Jobs;
using ReceiptAnalyzer.Ledger;
using ReceiptAnalyzer.Reports;

var pwaIndexHtml = ResolvePwaIndexHtml();

var builder = WebApplication.CreateBuilder(args);

var outputDir = builder.Configuration["Reports:OutputDir"] ?? @"C:\AI\Projects\Shopping";
var wineDir = builder.Configuration["Wine:Directory"] ?? @"C:\AI\Projects\Wine";

builder.Services.AddAnalysisAgent(builder.Configuration);
builder.Services.AddSingleton(_ => new WineCatalog(wineDir));
builder.Services.AddSingleton<LedgerStore>(sp =>
    new LedgerStore(outputDir, sp.GetRequiredService<ILogger<LedgerStore>>()));
builder.Services.AddSingleton(_ => new PriceCacheStore(outputDir));
builder.Services.AddSingleton(_ => new UsageLedgerStore(outputDir));
builder.Services.AddSingleton<PurchaseHistoryStore>(sp =>
    new PurchaseHistoryStore(outputDir, sp.GetRequiredService<ILogger<PurchaseHistoryStore>>()));
builder.Services.AddSingleton<ReportLibrary>(_ => new ReportLibrary(outputDir));
builder.Services.AddAnalysisJobs(outputDir, builder.Configuration);

// --- Auth: passkeys (WebAuthn) + long-lived cookie, SHARED with Server Control ---
// Auth:Directory points at a folder shared (bind-mounted) with the Server Control app so both share
// one credential store + one set of Data-Protection keys. With a parent-domain cookie + parent RP id,
// one passkey and one session cover both apps. Falls back to outputDir when unset (pre-SSO behaviour).
var authDir = builder.Configuration["Auth:Directory"] ?? outputDir;

builder.Services.AddSingleton(_ => new AuthStore(authDir));
builder.Services.AddMemoryCache();

// Persist Data Protection keys to the bind-mounted volume so auth cookies survive container restarts.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(authDir, ".state", "dp-keys")))
    .SetApplicationName("jamesradley-auth"); // must match Server Control so cookies are interchangeable

builder.Services.AddFido2(options =>
{
    options.ServerDomain = builder.Configuration["Auth:RpId"] ?? "localhost";
    options.ServerName = "James Radley";
    options.Origins = (builder.Configuration["Auth:Origins"] ?? "https://localhost:5080,http://localhost:5080")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "jr_auth"; // shared name across both apps
        var cookieDomain = builder.Configuration["Auth:CookieDomain"];
        if (!string.IsNullOrWhiteSpace(cookieDomain)) options.Cookie.Domain = cookieDomain;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(90);
        options.SlidingExpiration = true;
        // API calls expect 401/403, never an HTML login redirect.
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

// Either a passkey cookie or a valid X-API-KEY satisfies authorization.
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(
            CookieAuthenticationDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

// Trust the reverse-proxy hop (NPM) so X-Forwarded-Proto is honoured and Secure cookies work behind it.
var forwardedOptions = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();

app.MapStaticAssets();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.MapGet("/api/reports", (ReportLibrary library) =>
    Results.Ok(library.ListReports().Select(r => new { name = r.Name, modified = r.Modified })))
    .RequireAuthorization();

app.MapGet("/api/reports/{name}", (string name, ReportLibrary library) =>
{
    var markdown = library.ReadReport(name);
    return markdown is null
        ? Results.NotFound()
        : Results.Text(markdown, "text/markdown; charset=utf-8");
}).RequireAuthorization();

// Running cost/usage summary for the PWA Costs tab. Estimated spend we compute ourselves
// (web-search surcharges aren't modelled); not a provider "remaining credit" figure.
app.MapGet("/api/usage", (UsageLedgerStore usage, JobsOptions jobs) =>
    Results.Ok(UsageAggregator.Summarise(usage.All(), jobs.Pricing, jobs.UsdToGbp, DateTimeOffset.UtcNow)))
    .RequireAuthorization();

app.MapGet("/api/ledgers/{which}", (string which, ReportLibrary library) =>
{
    var markdown = library.ReadLedger(which);
    return markdown is null
        ? Results.NotFound()
        : Results.Text(markdown, "text/markdown; charset=utf-8");
}).RequireAuthorization();

// Per-store "what to buy here" list: the buy-elsewhere ledger pivoted by recommended store,
// merged with the per-store wine recommendations from the Wine project.
app.MapGet("/api/shopping-list", (LedgerStore ledgerStore, WineCatalog wines) =>
    Results.Ok(ShoppingListBuilder.Build(ledgerStore.Load(), wines.Load())))
    .RequireAuthorization();

// Replenishment: learned cadence per regularly-bought staple, flagged Overdue / DueSoon / OnTrack.
app.MapGet("/api/staples", (PurchaseHistoryStore history) =>
    Results.Ok(ReplenishmentBuilder.Build(history.Load(), LondonToday())))
    .RequireAuthorization();

// Spend dashboard + repeat-offender habits (US-owned / ultra-processed) over the purchase history.
app.MapGet("/api/spend", (PurchaseHistoryStore history) =>
    Results.Ok(SpendInsightsBuilder.Build(history.Load(), LondonToday())))
    .RequireAuthorization();

// Accepts an image and enqueues it for background analysis. Returns the job id; the same image
// always maps to the same job (idempotent), so retries after a timeout never re-run the pipeline.
app.MapPost("/api/analyses", async (
    HttpRequest request,
    JobStore jobStore,
    IJobQueue queue,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No image uploaded." });

    if (file.Length > 15 * 1024 * 1024) // 15MB limit
        return Results.BadRequest(new { error = "Image too large (max 15MB)." });

    var mediaType = NormaliseMediaType(file.ContentType, file.FileName);
    if (mediaType is null)
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var bytes = ms.ToArray();

    if (mediaType is "image/heic" or "image/heif")
    {
        bytes = ConvertHeicToJpeg(bytes, log);
        mediaType = "image/jpeg";
    }

    // Bake in EXIF rotation and gently stretch contrast so faint/creased thermal receipts read better
    // for the vision model. Best-effort: a failure leaves the image untouched.
    if (TryNormaliseImage(bytes, log, out var normalised))
    {
        bytes = normalised;
        mediaType = "image/jpeg";
    }

    var (job, created) = jobStore.GetOrCreate(bytes, mediaType);
    if (created)
    {
        await queue.EnqueueAsync(job.Id, ct);
        log.LogInformation("Queued analysis job {Id}: {Name} ({Bytes} bytes, {Media})",
            job.Id, file.FileName, bytes.Length, mediaType);
    }
    else if (!job.IsTerminal)
    {
        // Known but unfinished (e.g. an earlier crash) — make sure it is queued to resume.
        await queue.EnqueueAsync(job.Id, ct);
    }

    return Results.Accepted($"/api/analyses/{job.Id}", JobDto.From(job));
}).RequireAuthorization();

// Polled by the client until the job reaches a terminal state.
app.MapGet("/api/analyses/{id}", (string id, JobStore jobStore) =>
{
    var job = jobStore.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(JobDto.From(job));
}).RequireAuthorization();

app.MapFallback(async ctx =>
{
    if (pwaIndexHtml is null || !File.Exists(pwaIndexHtml))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(pwaIndexHtml);
});

// Warm up the native image library once at startup so the FIRST upload after a (re)start isn't slowed
// by Magick.NET's cold native-init — that cold start was occasionally resetting the upload connection.
WarmUpImageProcessing(app.Logger);

app.Run();

static void WarmUpImageProcessing(ILogger logger)
{
    try
    {
        using var img = new MagickImage(MagickColors.White, 8, 8);
        img.AutoOrient();
        img.Resize(new MagickGeometry("4x4>"));
        img.ContrastStretch(new Percentage(1), new Percentage(1));
        img.Format = MagickFormat.Jpeg;
        _ = img.ToByteArray();
        logger.LogInformation("Image processing warmed up.");
    }
    catch (Exception e)
    {
        logger.LogWarning(e, "Image warm-up failed (non-fatal).");
    }
}

static DateOnly LondonToday()
{
    try
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
    }
    catch (TimeZoneNotFoundException)
    {
        return DateOnly.FromDateTime(DateTime.Now);
    }
}

static string? ResolvePwaIndexHtml()
{
    var fromEnv = Environment.GetEnvironmentVariable("PWA_INDEX_HTML");
    if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

    var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ReceiptAnalyzer.Pwa", "wwwroot", "index.html"));
    return File.Exists(dev) ? dev : null;
}

static string? NormaliseMediaType(string? contentType, string fileName)
{
    var ct = contentType?.ToLowerInvariant();
    if (ct is "image/jpeg" or "image/jpg" or "image/png" or "image/webp" or "image/gif"
           or "image/heic" or "image/heif")
        return ct == "image/jpg" ? "image/jpeg" : ct;

    var ext = Path.GetExtension(fileName).ToLowerInvariant();
    return ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".heic" or ".heif" => "image/heic",
        _ => null
    };
}

static byte[] ConvertHeicToJpeg(byte[] input, ILogger log)
{
    log.LogInformation("Converting HEIC/HEIF to JPEG ({Bytes} bytes).", input.Length);
    using var image = new MagickImage(input);
    image.Format = MagickFormat.Jpeg;
    image.Quality = 92;
    return image.ToByteArray();
}

/// <summary>
/// Normalises a receipt photo before the vision step: applies EXIF orientation (so sideways phone
/// shots are read upright) and a gentle 1% contrast stretch (so faded thermal print is legible).
/// Deterministic, so the job-id hash stays stable across re-uploads. Best-effort — returns false and
/// leaves the bytes untouched if processing throws.
/// </summary>
static bool TryNormaliseImage(byte[] input, ILogger log, out byte[] output)
{
    try
    {
        using var image = new MagickImage(input);
        image.AutoOrient();
        // Shrink oversized phone photos (the "2600x2600>" greater-flag only ever scales DOWN). 2600px
        // long edge is at/above every model's effective input resolution, so no legibility is lost, and
        // it keeps per-upload CPU/memory low on the home server (and trims model input tokens).
        image.Resize(new MagickGeometry("2600x2600>"));
        image.ContrastStretch(new Percentage(1), new Percentage(1));
        image.Format = MagickFormat.Jpeg;
        image.Quality = 90;
        output = image.ToByteArray();
        return true;
    }
    catch (Exception e)
    {
        log.LogWarning(e, "Image normalisation failed; using the image as-is.");
        output = input;
        return false;
    }
}

/// <summary>Client-facing view of a job (no raw image bytes).</summary>
internal sealed record JobDto(
    string Id, string Status, string? Error,
    string? Markdown, string? ReportPath, string? Retailer, string? ReceiptDate, int ItemCount)
{
    public static JobDto From(ReceiptAnalyzer.Jobs.AnalysisJob j) => new(
        j.Id, j.Status.ToString(), j.Error,
        j.Markdown, j.ReportPath, j.Retailer, j.ReceiptDate, j.ItemCount);
}

public partial class Program;
