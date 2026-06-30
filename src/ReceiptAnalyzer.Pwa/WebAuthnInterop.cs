using System.Text.Json;
using Fido2.BlazorWebAssembly;
using Fido2NetLib;
using Microsoft.JSInterop;

namespace ReceiptAnalyzer.Pwa;

/// <summary>
/// Thin wrapper over our own <c>js/webauthn.js</c> module (the Fido2.BlazorWebAssembly package ships
/// only the uncompiled TypeScript, so we host the JS ourselves). Mirrors the package's WebAuthn API:
/// drives the browser's <c>navigator.credentials</c> and returns the raw attestation/assertion
/// responses for the server to verify.
///
/// All Fido option/response types cross the JS boundary as JSON *strings*, (de)serialised with the
/// source-generated <see cref="FidoBlazorSerializerContext"/>. Marshalling the strongly-typed objects
/// directly would make Blazor's JS interop fall back to reflection-based System.Text.Json, which the
/// WASM trimmer breaks (it strips the Fido model constructors' parameter names → runtime
/// "ConstructorContainsNullParameterNames" on <c>PublicKeyCredentialRpEntity</c>).
/// </summary>
public sealed class WebAuthnInterop : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly FidoBlazorSerializerContext _ctx = new();
    private IJSObjectReference? _module;

    public WebAuthnInterop(IJSRuntime js) => _js = js;

    private async Task<IJSObjectReference> ModuleAsync()
        => _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/webauthn.js");

    public async Task<bool> IsSupportedAsync()
        => await (await ModuleAsync()).InvokeAsync<bool>("isWebAuthnPossible");

    public async Task<AuthenticatorAttestationRawResponse> CreateCredsAsync(CredentialCreateOptions options)
    {
        var optionsJson = JsonSerializer.Serialize(options, _ctx.CredentialCreateOptions);
        var resultJson = await (await ModuleAsync()).InvokeAsync<string>("createCreds", optionsJson);
        return JsonSerializer.Deserialize(resultJson, _ctx.AuthenticatorAttestationRawResponse)!;
    }

    public async Task<AuthenticatorAssertionRawResponse> VerifyAsync(AssertionOptions options)
    {
        var optionsJson = JsonSerializer.Serialize(options, _ctx.AssertionOptions);
        var resultJson = await (await ModuleAsync()).InvokeAsync<string>("verify", optionsJson);
        return JsonSerializer.Deserialize(resultJson, _ctx.AuthenticatorAssertionRawResponse)!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch { /* best effort */ }
        }
    }
}
