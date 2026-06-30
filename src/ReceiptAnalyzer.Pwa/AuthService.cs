using System.Net.Http.Json;
using System.Text.Json;
using Fido2.BlazorWebAssembly;
using Fido2NetLib;

namespace ReceiptAnalyzer.Pwa;

/// <summary>
/// Client side of the passkey flow: checks the session, runs the WebAuthn login/enrol ceremonies via
/// <see cref="WebAuthnInterop"/>, and posts the results to the API. Methods return <c>null</c> on success
/// or an error message to show the user — they never throw, so the UI never hits the generic error bar.
/// </summary>
public sealed class AuthService
{
    private readonly HttpClient _http;
    private readonly WebAuthnInterop _webauthn;
    private readonly FidoBlazorSerializerContext _ctx = new();
    private readonly JsonSerializerOptions _json = new FidoBlazorSerializerContext().Options;

    public AuthService(HttpClient http, WebAuthnInterop webauthn)
    {
        _http = http;
        _webauthn = webauthn;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        try { return (await _http.GetAsync("api/auth/me")).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<bool> IsSupportedAsync()
    {
        try { return await _webauthn.IsSupportedAsync(); }
        catch { return false; }
    }

    public async Task<string?> LoginAsync()
    {
        try
        {
            var begin = await _http.PostAsync("api/auth/login/begin", null);
            if (!begin.IsSuccessStatusCode) return await ErrorAsync(begin);
            var options = JsonSerializer.Deserialize(await begin.Content.ReadAsStringAsync(), _ctx.AssertionOptions)!;

            var assertion = await _webauthn.VerifyAsync(options);

            var complete = await _http.PostAsJsonAsync("api/auth/login/complete", assertion, _json);
            return complete.IsSuccessStatusCode ? null : await ErrorAsync(complete);
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public async Task<string?> EnrolAsync(string? enrollCode)
    {
        try
        {
            var begin = await _http.PostAsJsonAsync("api/auth/register/begin", new { enrollCode });
            if (!begin.IsSuccessStatusCode) return await ErrorAsync(begin);
            var options = JsonSerializer.Deserialize(await begin.Content.ReadAsStringAsync(), _ctx.CredentialCreateOptions)!;

            var attestation = await _webauthn.CreateCredsAsync(options);

            var complete = await _http.PostAsJsonAsync("api/auth/register/complete", attestation, _json);
            return complete.IsSuccessStatusCode ? null : await ErrorAsync(complete);
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public async Task LogoutAsync()
    {
        try { await _http.PostAsync("api/auth/logout", null); } catch { /* best effort */ }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var dto = await resp.Content.ReadFromJsonAsync<ErrorDto>();
            if (!string.IsNullOrWhiteSpace(dto?.Error)) return dto!.Error!;
        }
        catch { /* fall through */ }
        return resp.ReasonPhrase ?? $"Request failed ({(int)resp.StatusCode}).";
    }

    private sealed record ErrorDto(string? Error);
}
