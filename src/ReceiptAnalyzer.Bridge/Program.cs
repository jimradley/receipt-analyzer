using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ReceiptAnalyzer.Bridge;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Receipt Analyzer Bridge");
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

    var provider = string.IsNullOrWhiteSpace(req.Provider) ? "claude" : req.Provider.Trim().ToLowerInvariant();
    if (provider is not ("claude" or "codex"))
        return Results.BadRequest(new { error = "provider must be claude or codex." });

    var prompt = req.Prompt;
    var hasImage = !string.IsNullOrWhiteSpace(req.ImagePath);
    // Under --restricted the CLI's file tools are confined to the working directory, so it becomes
    // the sandbox boundary: the image's own folder when there is one, otherwise an empty scratch
    // dir. Never the reports folder - a CLI that can list it starts reasoning about work it thinks
    // has already been done, and answers with prose instead of the JSON the pipeline asked for.
    string? workingDirectory = null;
    string? hostImagePath = null;
    if (hasImage)
    {
        hostImagePath = PathMapper.Map(req.ImagePath!, options.PathMap);
        prompt = $"Read the receipt image at {hostImagePath} first.\n\n" + prompt;
        workingDirectory = Path.GetDirectoryName(hostImagePath);
    }

    workingDirectory = ResolveWorkingDirectory(workingDirectory, options);

    // Never honour a caller-supplied tool list against a host CLI — intersect it with the server
    // allowlist so the bridge can't be coerced into running Bash/Write/Edit on the host.
    var explicitlyToolFree = provider == "codex" && req.AllowedTools is { Count: 0 } && !hasImage;
    var permitted = explicitlyToolFree
        ? Array.Empty<string>()
        : ToolFilter.Resolve(req.AllowedTools, options, needsRead: hasImage);
    if (permitted.Count == 0 && !explicitlyToolFree)
        return Results.BadRequest(new { error = "no permitted tools requested." });

    var allowedTools = string.Join(",", permitted);

    var model = string.IsNullOrWhiteSpace(req.Model) ? options.DefaultModel : req.Model;
    var timeoutSeconds = req.TimeoutSeconds is > 0 ? req.TimeoutSeconds.Value : options.DefaultTimeoutSeconds;

    var sw = Stopwatch.StartNew();
    try
    {
        if (provider == "codex")
        {
            var codexModel = string.IsNullOrWhiteSpace(req.Model) ? options.CodexMatcherModel : req.Model;
            if (!codexModel.Equals(options.CodexMatcherModel, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "model is not permitted for direct Codex calls." });
            var effort = req.ReasoningEffort?.ToLowerInvariant() is "low" or "medium"
                ? req.ReasoningEffort!.ToLowerInvariant() : "low";
            var codexResult = await RunCodexAsync(
                options, prompt, hostImagePath, permitted, timeoutSeconds, workingDirectory,
                codexModel, effort, ct);
            return Results.Ok(new AgentRunResponse(
                codexResult, codexModel, 0, 0, (int)sw.ElapsedMilliseconds, IsError: false));
        }

        var stdout = await RunClaudeAsync(options, prompt, model, allowedTools, timeoutSeconds, workingDirectory, ct);
        var parsed = ClaudeEnvelope.Parse(stdout);

        if (parsed.IsError && options.CodexFallbackEnabled && ClaudeLimitDetector.IsLimitError(parsed.ResultText))
        {
            logger.LogWarning(
                "Claude Code allowance exhausted; falling back to signed-in Codex model {Model}.",
                options.CodexFallbackModel);
            var codexResult = await RunCodexAsync(
                options, prompt, hostImagePath, permitted, timeoutSeconds, workingDirectory,
                options.CodexFallbackModel, options.CodexReasoningEffort, ct);
            return Results.Ok(new AgentRunResponse(
                codexResult, options.CodexFallbackModel, 0, 0,
                (int)sw.ElapsedMilliseconds, IsError: false));
        }

        return Results.Ok(new AgentRunResponse(
            parsed.ResultText, parsed.Model ?? model, parsed.InputTokens, parsed.OutputTokens,
            (int)sw.ElapsedMilliseconds, parsed.IsError));
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Bridge agent call timed out after {Seconds}s.", timeoutSeconds);
        return Results.Problem("Bridge agent call timed out.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Bridge agent call failed.");
        return Results.Problem($"Bridge agent call failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

static string ResolveWorkingDirectory(string? preferred, BridgeOptions options)
{
    if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred))
        return preferred!;

    var scratch = string.IsNullOrWhiteSpace(options.ScratchDirectory)
        ? Path.Combine(Path.GetTempPath(), "receipt-analyzer-bridge")
        : options.ScratchDirectory!;
    Directory.CreateDirectory(scratch);
    return scratch;
}

static async Task<string> RunClaudeAsync(
    BridgeOptions options, string prompt, string model, string allowedTools, int timeoutSeconds,
    string workingDirectory, CancellationToken ct)
{
    var psi = new ProcessStartInfo
    {
        FileName = options.ClaudeExecutable,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
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
    // --allowedTools only *adds* permissions; it never withholds any. The host settings file the
    // service account inherits can still grant Write/Edit (a defaultMode of "auto" does exactly
    // that), so the deny list, the permission mode and --restricted below are what actually keep
    // this call read-only.
    if (!string.IsNullOrWhiteSpace(options.DisallowedTools))
    {
        psi.ArgumentList.Add("--disallowedTools");
        psi.ArgumentList.Add(options.DisallowedTools);
    }
    if (!string.IsNullOrWhiteSpace(options.PermissionMode))
    {
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add(options.PermissionMode);
    }
    if (options.Restricted)
        psi.ArgumentList.Add("--restricted");
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
    {
        // Some Claude CLI versions put allowance errors only on stderr and emit no JSON envelope.
        // Convert that one terminal condition into the normal envelope shape so the caller's
        // narrowly-scoped Codex failover path still runs.
        if (ClaudeLimitDetector.IsLimitError(stderr))
            return System.Text.Json.JsonSerializer.Serialize(new { result = stderr, is_error = true });
        throw new InvalidOperationException($"claude exited {process.ExitCode}: {stderr}");
    }
    return stdout;
}

static async Task<string> RunCodexAsync(
    BridgeOptions options, string prompt, string? imagePath, IReadOnlyCollection<string> permittedTools,
    int timeoutSeconds, string workingDirectory, string model, string reasoningEffort, CancellationToken ct)
{
    var outputPath = Path.Combine(Path.GetTempPath(), $"receipt-analyzer-codex-{Guid.NewGuid():N}.txt");
    try
    {
        // The prompt is sent via stdin: receipt/rules payloads can exceed Windows' command-line
        // limit and must never be interpolated into the command arguments.
        var args = new List<string>();
        if (permittedTools.Contains("WebSearch", StringComparer.OrdinalIgnoreCase) ||
            permittedTools.Contains("WebFetch", StringComparer.OrdinalIgnoreCase))
            args.Add("--search");

        args.AddRange([
            "exec",
            "--cd", workingDirectory,
            "--sandbox", "read-only",
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--color", "never",
            "--output-last-message", outputPath,
            "--model", model,
            "--config", $"model_reasoning_effort=\"{reasoningEffort}\"",
        ]);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            args.Add("--image");
            args.Add(imagePath);
        }
        args.Add("-");

        var psi = new ProcessStartInfo
        {
            // Use the native executable, not the npm .cmd shim: ArgumentList can safely quote each
            // option for CreateProcess, while cmd.exe adds a second, fragile quoting layer.
            FileName = CodexExecutableResolver.Resolve(options.CodexExecutable),
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await process.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token);
        process.StandardInput.Close();

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
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Codex fallback exited {process.ExitCode}: {ErrorExcerpt(stdout + Environment.NewLine + stderr)}");

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Codex fallback produced no final-response file.");

        var result = await File.ReadAllTextAsync(outputPath, Encoding.UTF8, ct);
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("Codex fallback returned an empty response.");
        return result;
    }
    finally
    {
        try { File.Delete(outputPath); } catch { /* best effort */ }
    }
}

static string ErrorExcerpt(string value)
{
    const int half = 1_000;
    value = value.Trim();
    return value.Length <= half * 2
        ? value
        : value[..half] + "\n... [middle truncated] ...\n" + value[^half..];
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
    int? TimeoutSeconds = null,
    string? Provider = null,
    string? ReasoningEffort = null);

/// <summary>Response body for POST /agent/run — the CLI's JSON envelope, parsed defensively.</summary>
public sealed record AgentRunResponse(
    string? ResultText,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int DurationMs,
    bool IsError);
