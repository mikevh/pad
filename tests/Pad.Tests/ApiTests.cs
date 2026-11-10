using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pad.Api.Storage;

namespace Pad.Tests;

public class ApiTests : IClassFixture<ApiTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); // not Development: that would point storage at Azurite
            builder.ConfigureServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IFileStore>(new InMemoryFileStore()));
                s.Replace(ServiceDescriptor.Singleton<Pad.Api.Auth.ICredentialStore>(new Pad.Api.Auth.InMemoryCredentialStore()));
                // Stand in for a signed-in user so these tests exercise the file API, not the login flow.
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                s.PostConfigure<AuthenticationOptions>(o => { o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = "Test"; });
            });
        }
    }

    private readonly HttpClient _c;
    public ApiTests(Factory f) => _c = f.CreateClient();

    private static string U() => "t" + Guid.NewGuid().ToString("N")[..8];
    private static StringContent Body(string s) => new(s, Encoding.UTF8, "application/octet-stream");

    [Fact]
    public async Task Health_ok() => Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/health")).StatusCode);

    [Fact]
    public async Task Put_get_roundtrip_with_etag_and_content_type()
    {
        var p = $"{U()}/note.md";
        var put = await _c.PutAsync($"/api/files/{p}", Body("# hi"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var get = await _c.GetAsync($"/api/files/{p}");
        Assert.Equal("# hi", await get.Content.ReadAsStringAsync());
        Assert.Equal("text/markdown", get.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(get.Headers.ETag);
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Stale_if_match_returns_409_and_leaves_file()
    {
        var p = $"{U()}/a.txt";
        await _c.PutAsync($"/api/files/{p}", Body("v1"));
        var etag1 = (await _c.GetAsync($"/api/files/{p}")).Headers.ETag!.ToString();
        await _c.PutAsync($"/api/files/{p}", Body("v2"));

        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/files/{p}") { Content = Body("v3") };
        req.Headers.TryAddWithoutValidation("If-Match", etag1);
        var res = await _c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("etag_mismatch", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("v2", await (await _c.GetAsync($"/api/files/{p}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task If_none_match_star_prevents_overwrite()
    {
        var p = $"{U()}/a.txt";
        var mk = () => { var r = new HttpRequestMessage(HttpMethod.Put, $"/api/files/{p}") { Content = Body("x") }; r.Headers.TryAddWithoutValidation("If-None-Match", "*"); return r; };
        Assert.Equal(HttpStatusCode.OK, (await _c.SendAsync(mk())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _c.SendAsync(mk())).StatusCode);
    }

    [Fact]
    public async Task Get_if_none_match_returns_304()
    {
        var p = $"{U()}/a.txt";
        await _c.PutAsync($"/api/files/{p}", Body("x"));
        var etag = (await _c.GetAsync($"/api/files/{p}")).Headers.ETag!.ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{p}");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await _c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Html_is_served_as_download_not_inline()
    {
        var p = $"{U()}/page.html";
        await _c.PutAsync($"/api/files/{p}", Body("<script>alert(1)</script>"));
        var res = await _c.GetAsync($"/api/files/{p}");
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Download_flag_forces_attachment_for_inline_types()
    {
        var p = $"{U()}/note.md";
        await _c.PutAsync($"/api/files/{p}", Body("hi"));
        Assert.Null((await _c.GetAsync($"/api/files/{p}")).Content.Headers.ContentDisposition);
        var res = await _c.GetAsync($"/api/files/{p}?download=1");
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("note.md", res.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Missing_file_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/files/{U()}/nope.txt")).StatusCode);

    [Fact]
    public async Task Tree_lists_folders_first_one_level()
    {
        var root = U();
        await _c.PutAsync($"/api/files/{root}/b.txt", Body("1"));
        await _c.PutAsync($"/api/files/{root}/sub/c.txt", Body("2"));
        await _c.PostAsync($"/api/folders/{root}/empty", null);
        var tree = await _c.GetFromJsonAsync<JsonElement>($"/api/tree?prefix={root}");
        var names = tree.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "empty", "sub", "b.txt" }, names);
    }

    [Fact]
    public async Task Paths_lists_all_files_recursively()
    {
        var root = U();
        await _c.PutAsync($"/api/files/{root}/a.txt", Body("1"));
        await _c.PutAsync($"/api/files/{root}/deep/er/b.md", Body("2"));
        await _c.PostAsync($"/api/folders/{root}/empty", null);
        var res = await _c.GetFromJsonAsync<JsonElement>("/api/paths");
        var mine = res.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).Where(p => p.StartsWith(root + "/")).Order().ToArray();
        Assert.Equal(new[] { $"{root}/a.txt", $"{root}/deep/er/b.md" }, mine);
    }

    [Fact]
    public async Task Delete_file_then_404()
    {
        var p = $"{U()}/a.txt";
        await _c.PutAsync($"/api/files/{p}", Body("x"));
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/files/{p}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync($"/api/files/{p}")).StatusCode);
    }

    [Fact]
    public async Task Folder_create_and_recursive_delete()
    {
        var f = U();
        Assert.Equal(HttpStatusCode.Created, (await _c.PostAsync($"/api/folders/{f}/inner", null)).StatusCode);
        await _c.PutAsync($"/api/files/{f}/inner/x.txt", Body("x"));
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/folders/{f}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync($"/api/folders/{f}")).StatusCode);
    }

    [Fact]
    public async Task Move_file_and_conflict()
    {
        var d = U();
        await _c.PutAsync($"/api/files/{d}/a.txt", Body("a"));
        await _c.PutAsync($"/api/files/{d}/b.txt", Body("b"));
        var clash = await _c.PostAsJsonAsync("/api/move", new { from = $"{d}/a.txt", to = $"{d}/b.txt" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var ok = await _c.PostAsJsonAsync("/api/move", new { from = $"{d}/a.txt", to = $"{d}/z/a.txt" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("a", await (await _c.GetAsync($"/api/files/{d}/z/a.txt")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Move_folder_into_itself_is_400()
    {
        var d = U();
        await _c.PutAsync($"/api/files/{d}/a.txt", Body("a"));
        var res = await _c.PostAsJsonAsync("/api/move", new { from = d, to = $"{d}/inside" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/files/a/.padkeep")]
    [InlineData("/api/files/a%5Cb.txt")]
    public async Task Bad_paths_are_400(string url)
    {
        var res = await _c.PutAsync(url, Body("x"));
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{url} -> {res.StatusCode}");
        // Never succeeds
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Move_requires_both_paths()
    {
        var res = await _c.PostAsJsonAsync("/api/move", new { from = "a.txt" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "Test")), "Test")));
}
