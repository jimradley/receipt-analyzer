using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReceiptAnalyzer.Tests;

/// <summary>
/// Boots the real API host in-memory and checks the authorization gate: data endpoints require auth
/// (cookie or API key), while health and the auth ceremony endpoints stay anonymous. The live passkey
/// ceremony itself needs a real authenticator and is verified manually on a device.
/// </summary>
public class AuthGateTests : IClassFixture<AuthGateTests.Factory>
{
    private readonly Factory _factory;
    public AuthGateTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Data_endpoint_is_401_without_auth()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/usage");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Data_endpoint_is_200_with_a_valid_api_key()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-KEY", Factory.ApiKey);
        var resp = await client.GetAsync("/api/usage");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Data_endpoint_is_401_with_a_wrong_api_key()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-KEY", "wrong");
        var resp = await client.GetAsync("/api/usage");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Login_begin_is_anonymous_but_reports_no_passkey_enrolled()
    {
        // Reachable without auth (not 401); with no credential enrolled it returns 400, not 401.
        var resp = await _factory.CreateClient().PostAsync("/api/auth/login/begin", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string ApiKey = "test-api-key-abc123";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ra-authgate-" + Guid.NewGuid().ToString("N"));

        public Factory()
        {
            Directory.CreateDirectory(_dir);
            // The agent ctor needs a key present (provider-agnostic); the gate test never invokes the LLM.
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "dummy-not-used");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-not-used");
            Environment.SetEnvironmentVariable(ReceiptAnalyzer.Api.Auth.ApiKeyAuthenticationHandler.EnvVar, ApiKey);
            Environment.SetEnvironmentVariable("Reports__OutputDir", _dir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
