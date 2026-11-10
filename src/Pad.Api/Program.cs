using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Storage.Blobs;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Pad.Api.Auth;
using Pad.Api.Storage;

const long MaxUploadBytes = 100L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = MaxUploadBytes);

var cfg = builder.Configuration;
var isDev = builder.Environment.IsDevelopment();
var storageConfigured = !string.IsNullOrEmpty(cfg["Storage:ConnectionString"]) || !string.IsNullOrEmpty(cfg["Storage:AccountName"]);

BlobContainerClient Container(string name)
{
    var local = !string.IsNullOrEmpty(cfg["Storage:ConnectionString"]); // Azurite (local dev) only
    var c = local
        ? new BlobContainerClient(cfg["Storage:ConnectionString"], name)
        : new BlobContainerClient(
            new Uri($"https://{cfg["Storage:AccountName"] ?? throw new InvalidOperationException("Storage:AccountName is not configured.")}.blob.core.windows.net/{name}"),
            new DefaultAzureCredential());
    if (local) c.CreateIfNotExists(); // Azurite starts empty; Azure's containers come from Bicep
    return c;
}
var filesContainerName = cfg["Storage:FilesContainer"] ?? "pad";
var systemContainerName = cfg["Storage:SystemContainer"] ?? "pad-system";

builder.Services.AddSingleton<IFileStore>(_ => new BlobFileStore(Container(filesContainerName)));
builder.Services.AddSingleton<ICredentialStore>(_ => new BlobCredentialStore(Container(systemContainerName)));

// Session cookies and ceremony cookies are protected with keys kept in blob storage so they survive restarts.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("pad");
if (storageConfigured)
    dataProtection.PersistKeysToAzureBlobStorage(Container(systemContainerName).GetBlobClient("dataprotection-keys.xml"));

builder.Services.AddFido2(o =>
{
    o.ServerDomain = cfg["Auth:RelyingPartyId"] ?? "localhost";
    o.ServerName = "pad";
    o.Origins = new HashSet<string> { cfg["Auth:Origin"] ?? "http://localhost" };
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "pad_session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

// App Service terminates TLS in front of the app; trust its forwarded headers (rightmost value only).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (PadException e) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = e.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = e.Code, message = e.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Everything under /api requires a signed-in session unless explicitly opened up (health, auth ceremony).
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/health", () => Results.Ok(new { status = "ok", app = "pad" })).AllowAnonymous();
AuthEndpoints.Map(api);

api.MapGet("/tree", async (string? prefix, IFileStore store, CancellationToken ct) =>
{
    var p = PadPath.Normalize(prefix, allowEmpty: true);
    var entries = (await store.ListAsync(p, ct))
        .OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
    return Results.Ok(new { path = p, entries });
});

api.MapGet("/paths", async (IFileStore store, CancellationToken ct) =>
    Results.Ok(new { files = (await store.ListAllAsync(ct)).Select(e => new { e.Path, e.Size, e.Modified }) }));

api.MapGet("/files/{**path}", async (string path, string? download, HttpContext ctx, IFileStore store, CancellationToken ct) =>
{
    var p = PadPath.Normalize(path);
    var file = await store.OpenReadAsync(p, ct) ?? throw new NotFoundException("File not found.");
    ctx.Response.Headers.ETag = file.ETag;
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (file.LastModified is { } lm) ctx.Response.Headers.LastModified = lm.ToString("R");
    if (ctx.Request.Headers.IfNoneMatch.Contains(file.ETag))
    {
        await file.DisposeAsync();
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }
    var type = ContentTypes.ForPath(p) is var t && t != "application/octet-stream" ? t : file.ContentType;
    var name = PadPath.Name(p);
    return download is not ("1" or "true") && ContentTypes.IsSafeInline(type)
        ? Results.Stream(file.Stream, type)
        : Results.Stream(file.Stream, type, fileDownloadName: name);
});

api.MapPut("/files/{**path}", async (string path, HttpRequest req, IFileStore store, CancellationToken ct) =>
{
    var p = PadPath.Normalize(path);
    var type = ContentTypes.ForPath(p);
    if (type == "application/octet-stream" && !string.IsNullOrEmpty(req.ContentType)
        && !req.ContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        type = req.ContentType;

    var ifNoneMatch = req.Headers.IfNoneMatch.ToString();
    var ifMatch = req.Headers.IfMatch.ToString();
    var cond = new WriteCondition(
        IfMatch: string.IsNullOrEmpty(ifMatch) || ifMatch == "*" ? null : ifMatch,
        MustNotExist: ifNoneMatch == "*");

    var etag = await store.WriteAsync(p, req.Body, type, cond, ct);
    return Results.Ok(new { path = p, etag });
});

api.MapDelete("/files/{**path}", async (string path, HttpRequest req, IFileStore store, CancellationToken ct) =>
{
    var p = PadPath.Normalize(path);
    var ifMatch = req.Headers.IfMatch.ToString();
    var deleted = await store.DeleteFileAsync(p, string.IsNullOrEmpty(ifMatch) ? null : ifMatch, ct);
    return deleted ? Results.NoContent() : throw new NotFoundException("File not found.");
});

api.MapPost("/folders/{**path}", async (string path, IFileStore store, CancellationToken ct) =>
{
    var p = PadPath.Normalize(path);
    await store.CreateFolderAsync(p, ct);
    return Results.Created($"/api/tree?prefix={Uri.EscapeDataString(p)}", new { path = p });
});

api.MapDelete("/folders/{**path}", async (string path, IFileStore store, CancellationToken ct) =>
{
    var p = PadPath.Normalize(path);
    return await store.DeleteFolderAsync(p, ct) ? Results.NoContent() : throw new NotFoundException("Folder not found.");
});

api.MapPost("/move", async (MoveRequest body, IFileStore store, CancellationToken ct) =>
{
    var from = PadPath.Normalize(body.From);
    var to = PadPath.Normalize(body.To);
    if (from == to) throw new BadPathException("Source and destination are the same.");
    if (to.StartsWith(from + "/", StringComparison.Ordinal)) throw new BadPathException("Cannot move a folder into itself.");
    await store.MoveAsync(from, to, ct);
    return Results.Ok(new { from, to });
});

app.Run();

public record MoveRequest(string? From, string? To);

public partial class Program;
