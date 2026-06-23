using ImageMagick;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Jobs;
using ReceiptAnalyzer.Ledger;
using ReceiptAnalyzer.Reports;

var pwaIndexHtml = ResolvePwaIndexHtml();

var builder = WebApplication.CreateBuilder(args);

var outputDir = builder.Configuration["Reports:OutputDir"] ?? @"C:\AI\Projects\Shopping";

builder.Services.AddAnalysisAgent(builder.Configuration);
builder.Services.AddSingleton<LedgerStore>(sp =>
    new LedgerStore(outputDir, sp.GetRequiredService<ILogger<LedgerStore>>()));
builder.Services.AddSingleton<ReportLibrary>(_ => new ReportLibrary(outputDir));
builder.Services.AddAnalysisJobs(outputDir, builder.Configuration);

var app = builder.Build();

app.MapStaticAssets();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.MapGet("/api/reports", (ReportLibrary library) =>
    Results.Ok(library.ListReports().Select(r => new { name = r.Name, modified = r.Modified })));

app.MapGet("/api/reports/{name}", (string name, ReportLibrary library) =>
{
    var markdown = library.ReadReport(name);
    return markdown is null
        ? Results.NotFound()
        : Results.Text(markdown, "text/markdown; charset=utf-8");
});

app.MapGet("/api/ledgers/{which}", (string which, ReportLibrary library) =>
{
    var markdown = library.ReadLedger(which);
    return markdown is null
        ? Results.NotFound()
        : Results.Text(markdown, "text/markdown; charset=utf-8");
});

// Accepts an image and enqueues it for background analysis. Returns the job id; the same image
// always maps to the same job (idempotent), so retries after a timeout never re-run the pipeline.
app.MapPost("/api/analyses", async (
    HttpRequest request,
    JobStore jobStore,
    IJobQueue queue,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    var expectedKey = Environment.GetEnvironmentVariable("RECEIPT_ANALYZER_API_KEY");
    if (!string.IsNullOrEmpty(expectedKey))
    {
        var providedKey = request.Headers["X-API-KEY"].FirstOrDefault();
        if (providedKey != expectedKey)
            return Results.Unauthorized();
    }

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
});

// Polled by the client until the job reaches a terminal state.
app.MapGet("/api/analyses/{id}", (string id, JobStore jobStore) =>
{
    var job = jobStore.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(JobDto.From(job));
});

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

app.Run();

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
