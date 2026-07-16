using ReceiptAnalyzer.Bridge;

namespace ReceiptAnalyzer.Tests;

public class ClaudeEnvelopeTests
{
    [Fact]
    public void Parses_the_documented_envelope_shape()
    {
        const string json = """
            {
              "result": "{\"retailer\":\"Morrisons\"}",
              "is_error": false,
              "model": "claude-sonnet-5",
              "usage": { "input_tokens": 1234, "output_tokens": 56 }
            }
            """;

        var parsed = ClaudeEnvelope.Parse(json);

        Assert.Equal("{\"retailer\":\"Morrisons\"}", parsed.ResultText);
        Assert.Equal("claude-sonnet-5", parsed.Model);
        Assert.Equal(1234, parsed.InputTokens);
        Assert.Equal(56, parsed.OutputTokens);
        Assert.False(parsed.IsError);
    }

    [Fact]
    public void Falls_back_to_per_model_usage_when_top_level_usage_is_absent()
    {
        const string json = """
            {
              "result": "ok",
              "modelUsage": {
                "claude-sonnet-5": { "inputTokens": 100, "outputTokens": 20 }
              }
            }
            """;

        var parsed = ClaudeEnvelope.Parse(json);

        Assert.Equal(100, parsed.InputTokens);
        Assert.Equal(20, parsed.OutputTokens);
        Assert.Equal("claude-sonnet-5", parsed.Model);
    }

    [Fact]
    public void Flags_is_error_true()
    {
        const string json = """{ "result": "something went wrong", "is_error": true }""";

        var parsed = ClaudeEnvelope.Parse(json);

        Assert.True(parsed.IsError);
        Assert.Equal("something went wrong", parsed.ResultText);
    }

    [Fact]
    public void Non_json_stdout_is_surfaced_as_an_error_result_rather_than_throwing()
    {
        var parsed = ClaudeEnvelope.Parse("command not found: claude");

        Assert.True(parsed.IsError);
        Assert.Equal("command not found: claude", parsed.ResultText);
    }
}
