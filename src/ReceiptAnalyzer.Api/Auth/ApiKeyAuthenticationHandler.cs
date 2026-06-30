using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReceiptAnalyzer.Api.Auth;

/// <summary>
/// A secondary auth scheme: a valid <c>X-API-KEY</c> header (matching the
/// <c>RECEIPT_ANALYZER_API_KEY</c> env var) authenticates a request, so automation can post receipts
/// without a browser passkey session. Returns NoResult (not Fail) when absent, so the cookie scheme
/// still applies for the PWA.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-API-KEY";
    public const string EnvVar = "RECEIPT_ANALYZER_API_KEY";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(expected)) return Task.FromResult(AuthenticateResult.NoResult());

        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || string.IsNullOrEmpty(provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        var a = Encoding.UTF8.GetBytes(provided.ToString());
        var b = Encoding.UTF8.GetBytes(expected);
        if (!CryptographicOperations.FixedTimeEquals(a, b))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "api-key") }, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
