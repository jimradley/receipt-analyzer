using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ReceiptAnalyzer.Bridge;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5095");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/agent/run", async (
    AgentRunRequest req,
    IOptions<BridgeOptions> optionsAccessor,
    ILogger<Program> logger,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var options = optionsAccessor.Value;

    // Refuse everything if the shared secret isn't configured — an unset key must never be
    // interpreted as "no auth required".
    var expectedKey = Environment.GetEnvironmentVariable("RECEIPT_BRIDGE_KEY");
    if (string.IsNullOrEmpty(expectedKey))
    {
        logger.LogError("RECEIPT_BRIDGE_KEY is not set; refusing all /agent/run requests.");
        return Results.Problem("Bridge is not configured (RECEIPT_BRIDGE_KEY unset).", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!ctx.Request.Headers.TryGetValue("X-BRIDGE-KEY", out var provided) || !FixedTimeEquals(provided.ToString(), expectedKey))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Prompt))
        return Results.BadRequest(new { error = "prompt is required." });

    var prompt = req.Prompt;
    var hasImage = !string.IsNullOrWhiteSpace(req.ImagePath);
    if (hasImage)
    {
        var hostImagePath = PathMapper.Map(req.ImagePath!, options.PathMap);
        prompt = $"Read the receipt image at {hostImagePath} first.\n\n" + prompt;
    }

    // Never honour a caller-supplied tool list against a host CLI — intersect it with the server
    // allowlist so the bridge can't be coerced into running Bash/Write/Edit on the host.
    var permitted = ToolFilter.Resolve(req.AllowedTools, options, needsRead: hasImage);
    if (permitted.Count == 0)
        return Results.BadRequest(new { error = "no permitted tools requested." });

    var allowedTools = string.Join(",", permitted);

    var model = string.IsNullOrWhiteSpace(req.Model) ? options.DefaultModel : req.Model;
    var timeoutSeconds = req.TimeoutSeconds is > 0 ? req.TimeoutSeconds.Value : options.DefaultTimeoutSeconds;

    var sw = Stopwatch.StartNew();
    try
    {
        var stdout = await RunClaudeAsync(options, prompt, model, allowedTools, timeoutSeconds, ct);
        var parsed = ClaudeEnvelope.Parse(stdout);
        return Results.Ok(new AgentRunResponse(
            parsed.ResultText, parsed.Model ?? model, parsed.InputTokens, parsed.OutputTokens,
            (int)sw.ElapsedMilliseconds, parsed.IsError));
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("claude CLI call timed out after {Seconds}s.", timeoutSeconds);
        return Results.Problem("claude CLI call timed out.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "claude CLI call failed.");
        return Results.Problem($"claude CLI call failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

static async Task<string> RunClaudeAsync(
    BridgeOptions options, string prompt, string model, string allowedTools, int timeoutSeconds, CancellationToken ct)
{
    var psi = new ProcessStartInfo
    {
        FileName = options.ClaudeExecutable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    // ArgumentList (not a concatenated command line) avoids shell-quoting pitfalls for a prompt that
    // may contain quotes, newlines, or receipt-derived text.
    psi.ArgumentList.Add("-p");
    psi.ArgumentList.Add(prompt);
    psi.ArgumentList.Add("--output-format");
    psi.ArgumentList.Add("json");
    psi.ArgumentList.Add("--model");
    psi.ArgumentList.Add(model);
    psi.ArgumentList.Add("--allowedTools");
    psi.ArgumentList.Add(allowedTools);
    psi.ArgumentList.Add("--max-turns");
    psi.ArgumentList.Add(options.MaxTurns.ToString());

    using var process = new Process { StartInfo = psi };
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

    process.Start();
    var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
    var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

    try
    {
        await process.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        TryKillTree(process);
        throw;
    }

    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        throw new InvalidOperationException($"claude exited {process.ExitCode}: {stderr}");
    return stdout;
}

static void TryKillTree(Process process)
{
    try { process.Kill(entireProcessTree: true); } catch { /* best effort — process may have already exited */ }
}

// Constant-time comparison so the shared-secret check doesn't leak the key via response timing.
static bool FixedTimeEquals(string? a, string? b)
{
    if (a is null || b is null) return false;
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Request body for POST /agent/run.</summary>
public sealed record AgentRunRequest(
    string Prompt,
    string? ImagePath = null,
    string? Model = null,
    List<string>? AllowedTools = null,
    int? TimeoutSeconds = null);

/// <summary>Response body for POST /agent/run — the CLI's JSON envelope, parsed defensively.</summary>
public sealed record AgentRunResponse(
    string? ResultText,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int DurationMs,
    bool IsError);
