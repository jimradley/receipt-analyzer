using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Agent.ClaudeCode;

namespace ReceiptAnalyzer.Tests;

public class ClaudeCodeAgentTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "ra-cc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    private static ClaudeCodeAgent NewAgent(StubHandler handler, string? tmpDir = null) =>
        new(new HttpClient(handler),
            Options.Create(new AgentOptions { ClaudeCode = new ClaudeCodeOptions { TmpDir = tmpDir } }),
            NullLogger<ClaudeCodeAgent>.Instance);

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task Extract_writes_a_temp_image_passes_its_path_to_the_bridge_and_cleans_up()
    {
        string? capturedImagePath = null;
        var handler = new StubHandler
        {
            Responder = req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc = JsonNode.Parse(body)!;
                capturedImagePath = (string?)doc["imagePath"];
                Assert.NotNull(capturedImagePath);
                Assert.True(File.Exists(capturedImagePath)); // still present while the "bridge" is using it

                const string extractionJson = """
                    {"retailer":"Morrisons","receiptDate":"2026-07-16","items":[
                    {"name":"Bananas","quantity":1,"unitPrice":0.5,"lineTotal":0.5}],
                    "printedSubtotal":0.5,"printedTotal":0.5,"savings":null,"printedItemCount":1,
                    "isReceipt":true,"confidence":0.9,"notes":null}
                    """;
                return JsonResponse(new { resultText = extractionJson, model = "claude-sonnet-5", inputTokens = 100, outputTokens = 50, isError = false });
            }
        };
        var agent = NewAgent(handler, _tmpDir);

        using var usageScope = UsageReporter.BeginScope();
        var result = await agent.ExtractReceiptAsync(Encoding.UTF8.GetBytes("fake-image-bytes"), "image/jpeg", CancellationToken.None);

        Assert.Equal("Morrisons", result.Retailer);
        Assert.Equal(1, result.PrintedItemCount);
        Assert.NotNull(capturedImagePath);
        Assert.False(File.Exists(capturedImagePath)); // cleaned up once the call completes

        var usage = UsageReporter.Snapshot();
        Assert.Contains(usage, u => u.Stage == "extract" && u.Model == "claude-sonnet-5" && u.InputTokens == 100 && u.OutputTokens == 50);
    }

    [Fact]
    public async Task Extract_cleans_up_the_temp_image_even_when_the_bridge_call_fails()
    {
        string? capturedImagePath = null;
        var handler = new StubHandler
        {
            Responder = req =>
            {
                var doc = JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                capturedImagePath = (string?)doc["imagePath"];
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
            }
        };
        var agent = NewAgent(handler, _tmpDir);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            agent.ExtractReceiptAsync(Encoding.UTF8.GetBytes("fake"), "image/jpeg", CancellationToken.None));

        Assert.NotNull(capturedImagePath);
        Assert.False(File.Exists(capturedImagePath));
    }

    [Fact]
    public async Task PriceCheck_parses_outcomes_and_source_url()
    {
        var handler = new StubHandler
        {
            Responder = _ =>
            {
                const string resultText = """
                    {"items":[{"index":0,"found":true,"bestPrice":1.00,"bestPriceStore":"Asda","sourceUrl":"https://www.trolley.co.uk/product/x","notes":null}],"skippedSummary":null}
                    """;
                return JsonResponse(new { resultText, model = "claude-sonnet-5", inputTokens = 10, outputTokens = 5, isError = false });
            }
        };
        var agent = NewAgent(handler);

        var items = new[] { new BrandedItemForCheck(0, "Maltesers 100g", 1.50m, "Morrisons") };
        var result = await agent.PriceCheckAsync(items, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(1.00m, item.BestPrice);
        Assert.Equal("Asda", item.BestPriceStore);
        Assert.Equal("https://www.trolley.co.uk/product/x", item.SourceUrl);
    }

    [Fact]
    public async Task Bridge_http_error_throws_a_descriptive_exception()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad key") }
        };
        var agent = NewAgent(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            agent.ClassifyAsync(new[] { new RawItem("Test", 1, 1, 1) }, CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Bridge_isError_flag_throws()
    {
        var handler = new StubHandler
        {
            Responder = _ => JsonResponse(new { resultText = "claude CLI exited 1: something broke", isError = true })
        };
        var agent = NewAgent(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.ClassifyAsync(new[] { new RawItem("Test", 1, 1, 1) }, CancellationToken.None));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Responder(request));
    }
}
