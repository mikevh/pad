using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pad.Api.Auth;
using Pad.Api.Storage;

namespace Pad.Tests;

/// <summary>No auth stand-in here: this is the real cookie pipeline.</summary>
public class AuthGateTests : IClassFixture<AuthGateTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        public string? SetupToken { get; set; } = "correct-setup-token";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:SetupToken", SetupToken);
            builder.UseSetting("Auth:RelyingPartyId", "localhost");
            builder.UseSetting("Auth:Origin", "https://localhost");
            builder.ConfigureServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IFileStore>(new InMemoryFileStore()));
                s.Replace(ServiceDescriptor.Singleton<ICredentialStore>(new InMemoryCredentialStore()));
            });
        }
    }

    private readonly HttpClient _c;
    public AuthGateTests(Factory f) => _c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("GET", "/api/tree")]
    [InlineData("GET", "/api/paths")]
    [InlineData("GET", "/api/files/a.txt")]
    [InlineData("PUT", "/api/files/a.txt")]
    [InlineData("DELETE", "/api/files/a.txt")]
    [InlineData("POST", "/api/folders/x")]
    [InlineData("DELETE", "/api/folders/x")]
    [InlineData("POST", "/api/move")]
    [InlineData("GET", "/api/auth/credentials")]
    [InlineData("DELETE", "/api/auth/credentials/abc")]
    public async Task Api_requires_a_session(string method, string url)
    {
        var res = await _c.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Health_and_status_are_public()
    {
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/health")).StatusCode);
        var status = await _c.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(status.GetProperty("authenticated").GetBoolean());
        Assert.False(status.GetProperty("registered").GetBoolean());
        Assert.True(status.GetProperty("setupAvailable").GetBoolean());
    }

    [Fact]
    public async Task Register_options_rejects_wrong_or_missing_setup_token()
    {
        foreach (var body in new[] { new { setupToken = "nope" }, new { setupToken = "" }, new { setupToken = (string?)null } })
        {
            var res = await _c.PostAsJsonAsync("/api/auth/register/options", body);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await _c.PostAsync("/api/auth/register/options", null)).StatusCode);
    }

    [Fact]
    public async Task Register_options_with_correct_token_returns_discoverable_uv_required_options()
    {
        var res = await _c.PostAsJsonAsync("/api/auth/register/options", new { setupToken = "correct-setup-token" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var o = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("localhost", o.GetProperty("rp").GetProperty("id").GetString());
        var sel = o.GetProperty("authenticatorSelection");
        Assert.Equal("required", sel.GetProperty("residentKey").GetString());
        Assert.Equal("required", sel.GetProperty("userVerification").GetString());
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith("pad_fido=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Register_without_a_started_ceremony_fails()
    {
        var res = await _c.PostAsJsonAsync("/api/auth/register", new { name = "x", credential = new { } });
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _c.GetAsync("/api/tree")).StatusCode);
    }

    [Fact]
    public async Task Login_without_a_started_ceremony_does_not_sign_in()
    {
        var res = await _c.PostAsJsonAsync("/api/auth/login", new { id = "AAAA", rawId = "AAAA", type = "public-key", response = new { authenticatorData = "AA", clientDataJSON = "AA", signature = "AA", userHandle = "AA" } });
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _c.GetAsync("/api/tree")).StatusCode);
    }

    [Fact]
    public async Task Login_options_expose_no_credential_ids()
    {
        var res = await _c.PostAsync("/api/auth/login/options", null);
        var o = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(o.TryGetProperty("allowCredentials", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0);
        Assert.Equal("required", o.GetProperty("userVerification").GetString());
    }

    [Fact]
    public async Task Static_files_stay_public() =>
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _c.GetAsync("/")).StatusCode);
}

public class AuthDisabledRegistrationTests : IClassFixture<AuthDisabledRegistrationTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:SetupToken", ""); // no token configured: bootstrap must be off
            builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ICredentialStore>(new InMemoryCredentialStore())));
        }
    }

    private readonly HttpClient _c;
    public AuthDisabledRegistrationTests(Factory f) => _c = f.CreateClient();

    [Fact]
    public async Task Without_a_configured_token_nobody_can_register()
    {
        var res = await _c.PostAsJsonAsync("/api/auth/register/options", new { setupToken = "" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var status = await _c.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(status.GetProperty("setupAvailable").GetBoolean());
    }
}
