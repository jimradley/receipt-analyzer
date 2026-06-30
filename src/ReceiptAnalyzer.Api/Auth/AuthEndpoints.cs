using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace ReceiptAnalyzer.Api.Auth;

/// <summary>
/// Passwordless passkey (WebAuthn) auth for the single owner. Enrolment is gated by a one-time enroll
/// code (env <c>RECEIPT_ANALYZER_ENROLL_CODE</c>) — which also serves as lost-device recovery — or by an
/// existing authenticated session (to add another device). A successful login/enrol issues a long-lived
/// persistent cookie. Pending challenges are held briefly in <see cref="IMemoryCache"/> keyed by challenge.
/// </summary>
public static class AuthEndpoints
{
    public const string EnrollCodeEnvVar = "RECEIPT_ANALYZER_ENROLL_CODE";

    // Stable owner identity shared with Server Control so ONE passkey maps to the same account across
    // both apps. This value must be identical in both apps' AuthEndpoints. It is kept as the ORIGINAL
    // "receipt-analyzer-owner" string (not renamed) so passkeys enrolled before SSO keep working — the
    // login owner-check compares the credential's stored UserHandle against these exact bytes.
    private static readonly byte[] OwnerUserId = Encoding.UTF8.GetBytes("receipt-analyzer-owner");
    private static readonly Fido2User Owner = new()
    {
        Id = OwnerUserId,
        Name = "owner",
        DisplayName = "James Radley",
    };

    private sealed record RegisterBeginRequest(string? EnrollCode, string? Nickname);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/me", (HttpContext ctx, AuthStore store) =>
            ctx.User.Identity?.IsAuthenticated == true
                ? Results.Ok(new { authenticated = true, devices = store.All().Count })
                : Results.Unauthorized());

        group.MapPost("/register/begin", (HttpContext ctx, RegisterBeginRequest? body, IFido2 fido2, AuthStore store, IMemoryCache cache) =>
        {
            var authed = ctx.User.Identity?.IsAuthenticated == true;
            if (!authed && !EnrollCodeValid(body?.EnrollCode))
                return Results.Json(new { error = "An enroll code or an existing session is required to add a passkey." }, statusCode: StatusCodes.Status401Unauthorized);

            var exclude = store.All().Select(c => new PublicKeyCredentialDescriptor(c.CredentialId)).ToList();
            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = Owner,
                ExcludeCredentials = exclude,
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Required,
                    UserVerification = UserVerificationRequirement.Required,
                },
                AttestationPreference = AttestationConveyancePreference.None,
                Extensions = new AuthenticationExtensionsClientInputs { CredProps = true },
            });

            cache.Set(ChallengeKey("reg", options.Challenge), (options, body?.Nickname ?? "passkey"), CacheEntry());
            return Results.Content(options.ToJson(), "application/json");
        });

        group.MapPost("/register/complete", async (HttpContext ctx, AuthenticatorAttestationRawResponse attestation, IFido2 fido2, AuthStore store, IMemoryCache cache, CancellationToken ct) =>
        {
            var challenge = ChallengeFrom(attestation.Response.ClientDataJson);
            if (!cache.TryGetValue(ChallengeKey("reg", challenge), out (CredentialCreateOptions Options, string Nickname) pending))
                return Results.BadRequest(new { error = "Registration challenge expired — start again." });
            cache.Remove(ChallengeKey("reg", challenge));

            try
            {
                var credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = attestation,
                    OriginalOptions = pending.Options,
                    IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(store.Find(args.CredentialId) is null),
                }, ct);

                store.Add(new StoredCredential(
                    credential.Id, credential.PublicKey, credential.User.Id, credential.SignCount,
                    pending.Nickname, credential.AaGuid, DateTimeOffset.UtcNow));

                await SignInAsync(ctx); // enrolling also logs you in
                return Results.Ok(new { ok = true });
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        group.MapPost("/login/begin", (IFido2 fido2, AuthStore store, IMemoryCache cache) =>
        {
            var allowed = store.All().Select(c => new PublicKeyCredentialDescriptor(c.CredentialId)).ToList();
            if (allowed.Count == 0)
                return Results.BadRequest(new { error = "No passkey enrolled yet — set up this device first." });

            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = allowed,
                UserVerification = UserVerificationRequirement.Required,
            });

            cache.Set(ChallengeKey("login", options.Challenge), options, CacheEntry());
            return Results.Content(options.ToJson(), "application/json");
        });

        group.MapPost("/login/complete", async (HttpContext ctx, AuthenticatorAssertionRawResponse assertion, IFido2 fido2, AuthStore store, IMemoryCache cache, CancellationToken ct) =>
        {
            var challenge = ChallengeFrom(assertion.Response.ClientDataJson);
            if (!cache.TryGetValue(ChallengeKey("login", challenge), out AssertionOptions? options) || options is null)
                return Results.BadRequest(new { error = "Login challenge expired — try again." });
            cache.Remove(ChallengeKey("login", challenge));

            var stored = store.Find(assertion.RawId);
            if (stored is null) return Results.BadRequest(new { error = "Unknown credential." });

            try
            {
                var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = assertion,
                    OriginalOptions = options,
                    StoredPublicKey = stored.PublicKey,
                    StoredSignatureCounter = stored.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                        Task.FromResult(args.UserHandle.SequenceEqual(OwnerUserId) && store.Find(args.CredentialId) is not null),
                }, ct);

                store.UpdateSignCount(result.CredentialId, result.SignCount);
                await SignInAsync(ctx);
                return Results.Ok(new { ok = true });
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        group.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        return app;
    }

    private static async Task SignInAsync(HttpContext ctx)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "owner") },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(90) });
    }

    private static bool EnrollCodeValid(string? provided)
    {
        // Trim both sides: mobile keyboards love to append a trailing space (and a stray space in the
        // server .env is just as easy to introduce). Case/character mangling is handled on the input field.
        var expected = Environment.GetEnvironmentVariable(EnrollCodeEnvVar)?.Trim();
        provided = provided?.Trim();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    private static byte[] ChallengeFrom(byte[] clientDataJson)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorResponse>(clientDataJson)
            ?? throw new InvalidOperationException("Could not parse client data.");
        return response.Challenge;
    }

    private static string ChallengeKey(string kind, byte[] challenge) => $"fido:{kind}:{Convert.ToBase64String(challenge)}";

    private static MemoryCacheEntryOptions CacheEntry() =>
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
}
