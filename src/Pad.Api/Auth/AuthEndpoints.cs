using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Pad.Api.Storage;

namespace Pad.Api.Auth;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    private const string FidoCookie = "pad_fido";
    // Stable WebAuthn user handle: there is exactly one user.
    private static readonly byte[] UserId = Encoding.UTF8.GetBytes("pad-owner");

    public record RegisterOptionsRequest(string? SetupToken);
    public record RegisterRequest(string? Name, AuthenticatorAttestationRawResponse? Credential);

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

        auth.MapGet("/status", async (HttpContext ctx, ICredentialStore store, IConfiguration cfg, CancellationToken ct) =>
        {
            var creds = await store.LoadAsync(ct);
            return Results.Ok(new
            {
                authenticated = ctx.User.Identity?.IsAuthenticated == true,
                registered = creds.Count > 0,
                setupAvailable = creds.Count == 0 && !string.IsNullOrEmpty(cfg["Auth:SetupToken"]),
            });
        });

        auth.MapPost("/register/options", async (RegisterOptionsRequest? body, HttpContext ctx, IFido2 fido2, ICredentialStore store, IConfiguration cfg, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var creds = await store.LoadAsync(ct);
            EnsureMayRegister(ctx, creds, cfg, body?.SetupToken);
            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = new Fido2User { Id = UserId, Name = "owner", DisplayName = "pad owner" },
                ExcludeCredentials = creds.Select(c => new PublicKeyCredentialDescriptor(c.IdBytes)).ToList(),
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Required,
                    UserVerification = UserVerificationRequirement.Required,
                },
                AttestationPreference = AttestationConveyancePreference.None,
            });
            SetFidoCookie(ctx, dp, options.ToJson());
            return Results.Content(options.ToJson(), "application/json");
        });

        auth.MapPost("/register", async (RegisterRequest body, HttpContext ctx, IFido2 fido2, ICredentialStore store, IConfiguration cfg, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var creds = await store.LoadAsync(ct);
            // Re-check: the state may have changed since /register/options. Cookie proves we issued the options,
            // not that registration is still allowed. First-credential path needs the options cookie only.
            var wasAuthenticated = ctx.User.Identity?.IsAuthenticated == true;
            if (!wasAuthenticated && creds.Count > 0) throw new ForbiddenException("Registration is closed.");
            if (!wasAuthenticated && string.IsNullOrEmpty(cfg["Auth:SetupToken"])) throw new ForbiddenException("Registration is disabled.");
            if (body.Credential is null) throw new BadPathException("Credential is required.");
            var options = CredentialCreateOptions.FromJson(TakeFidoCookie(ctx, dp));

            var reg = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = body.Credential,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(creds.All(c => !c.IdBytes.AsSpan().SequenceEqual(args.CredentialId))),
            }, ct);

            var name = (body.Name ?? "").Trim();
            if (name.Length == 0) name = $"Passkey {creds.Count + 1}";
            creds.Add(new StoredCredential(Base64Url.Encode(reg.Id), reg.PublicKey, reg.SignCount, name[..Math.Min(50, name.Length)], DateTimeOffset.UtcNow, null));
            await store.SaveAsync(creds, ct);
            if (!wasAuthenticated) await SignIn(ctx);
            return Results.Ok(new { ok = true });
        });

        auth.MapPost("/login/options", (HttpContext ctx, IFido2 fido2, IDataProtectionProvider dp) =>
        {
            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = [], // discoverable credentials: nothing about stored credentials leaks pre-login
                UserVerification = UserVerificationRequirement.Required,
            });
            SetFidoCookie(ctx, dp, options.ToJson());
            return Results.Content(options.ToJson(), "application/json");
        });

        auth.MapPost("/login", async (AuthenticatorAssertionRawResponse body, HttpContext ctx, IFido2 fido2, ICredentialStore store, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var options = AssertionOptions.FromJson(TakeFidoCookie(ctx, dp));
            var creds = await store.LoadAsync(ct);
            var cred = creds.FirstOrDefault(c => c.IdBytes.AsSpan().SequenceEqual(body.RawId))
                ?? throw new UnauthorizedException("Unknown passkey.");
            try
            {
                var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = body,
                    OriginalOptions = options,
                    StoredPublicKey = cred.PublicKey,
                    StoredSignatureCounter = cred.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(UserId)),
                }, ct);
                creds[creds.IndexOf(cred)] = cred with { SignCount = result.SignCount, LastUsedAt = DateTimeOffset.UtcNow };
                await store.SaveAsync(creds, ct);
            }
            catch (Fido2VerificationException)
            {
                throw new UnauthorizedException("Passkey verification failed.");
            }
            await SignIn(ctx);
            return Results.Ok(new { ok = true });
        });

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        // Managing passkeys requires being signed in (falls under the API's default authorization).
        var manage = api.MapGroup("/auth/credentials").RequireRateLimiting(RateLimitPolicy);
        manage.MapGet("", async (ICredentialStore store, CancellationToken ct) =>
            Results.Ok((await store.LoadAsync(ct)).Select(c => new { c.Id, c.Name, c.CreatedAt, c.LastUsedAt })));
        manage.MapDelete("/{id}", async (string id, ICredentialStore store, CancellationToken ct) =>
        {
            var creds = await store.LoadAsync(ct);
            var cred = creds.FirstOrDefault(c => c.Id == id) ?? throw new NotFoundException("Passkey not found.");
            if (creds.Count == 1) throw new ConflictException("last_credential", "You can't remove your only passkey.");
            creds.Remove(cred);
            await store.SaveAsync(creds, ct);
            return Results.NoContent();
        });
    }

    private static void EnsureMayRegister(HttpContext ctx, List<StoredCredential> creds, IConfiguration cfg, string? token)
    {
        if (ctx.User.Identity?.IsAuthenticated == true) return;
        var expected = cfg["Auth:SetupToken"];
        if (creds.Count > 0) throw new ForbiddenException("Registration is closed. Sign in to add another passkey.");
        if (string.IsNullOrEmpty(expected)) throw new ForbiddenException("Registration is disabled.");
        if (string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            throw new ForbiddenException("Wrong setup token.");
    }

    private static async Task SignIn(HttpContext ctx)
    {
        var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static IDataProtector Protector(IDataProtectionProvider dp) => dp.CreateProtector("pad.fido-ceremony");

    private static void SetFidoCookie(HttpContext ctx, IDataProtectionProvider dp, string json) =>
        ctx.Response.Cookies.Append(FidoCookie, Protector(dp).ToTimeLimitedDataProtector().Protect(json, TimeSpan.FromMinutes(5)), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/api/auth", MaxAge = TimeSpan.FromMinutes(5),
            Secure = ctx.Request.IsHttps,
        });

    private static string TakeFidoCookie(HttpContext ctx, IDataProtectionProvider dp)
    {
        var raw = ctx.Request.Cookies[FidoCookie] ?? throw new BadPathException("No ceremony in progress. Start again.");
        ctx.Response.Cookies.Delete(FidoCookie, new CookieOptions { Path = "/api/auth" });
        try { return Protector(dp).ToTimeLimitedDataProtector().Unprotect(raw); }
        catch (CryptographicException) { throw new BadPathException("The ceremony expired. Start again."); }
    }
}
