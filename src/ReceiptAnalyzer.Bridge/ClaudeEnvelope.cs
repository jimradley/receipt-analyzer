using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReceiptAnalyzer.Bridge;

/// <summary>Parsed result from the `claude -p --output-format json` CLI envelope.</summary>
public sealed record ClaudeCliResult(
    string? ResultText,
    string? Model,
    int InputTokens,
    int OutputTokens,
    bool IsError);

/// <summary>
/// Parses the JSON envelope <c>claude -p --output-format json</c> prints to stdout. Parsing is
/// defensive — the exact field set has shifted across CLI versions (top-level "usage" vs. a
/// per-model "modelUsage" map) — so everything is looked up by name with fallbacks rather than bound
/// to a rigid contract. Pure and I/O-free, so it's unit-testable without spawning `claude`.
/// </summary>
public static class ClaudeEnvelope
{
    public static ClaudeCliResult Parse(string json)
    {
        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Not JSON at all — e.g. the CLI printed a plain-text error to stdout. Surface the raw
            // text as the result so the caller sees *something* useful, flagged as an error.
            return new ClaudeCliResult(json, null, 0, 0, IsError: true);
        }

        if (doc is null) return new ClaudeCliResult(null, null, 0, 0, IsError: true);

        var isError = (bool?)doc["is_error"] ?? (bool?)doc["isError"] ?? false;
        var resultText = (string?)doc["result"] ?? (string?)doc["response"];
        var model = (string?)doc["model"];

        var usage = doc["usage"];
        var input = (int?)usage?["input_tokens"] ?? (int?)usage?["inputTokens"] ?? 0;
        var output = (int?)usage?["output_tokens"] ?? (int?)usage?["outputTokens"] ?? 0;

        // Newer CLI versions report usage per-model: "modelUsage": { "<model>": { "inputTokens": .. } }.
        if (input == 0 && output == 0 && doc["modelUsage"] is JsonObject modelUsage)
        {
            foreach (var (name, node) in modelUsage)
            {
                if (node is null) continue;
                input += (int?)node["inputTokens"] ?? (int?)node["input_tokens"] ?? 0;
                output += (int?)node["outputTokens"] ?? (int?)node["output_tokens"] ?? 0;
                model ??= name;
            }
        }

        return new ClaudeCliResult(resultText, model, input, output, isError);
    }
}
