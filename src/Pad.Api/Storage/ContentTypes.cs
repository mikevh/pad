using Microsoft.AspNetCore.StaticFiles;

namespace Pad.Api.Storage;

public static class ContentTypes
{
    private static readonly FileExtensionContentTypeProvider Provider = Build();

    private static FileExtensionContentTypeProvider Build()
    {
        var p = new FileExtensionContentTypeProvider();
        foreach (var ext in new[] { ".md", ".markdown" }) p.Mappings[ext] = "text/markdown";
        foreach (var ext in new[] { ".txt", ".log", ".csv", ".ini", ".cfg", ".conf", ".env", ".sh", ".py", ".ts", ".tsx", ".jsx", ".cs", ".go", ".rs", ".java", ".sql", ".yaml", ".yml", ".toml" })
            p.Mappings[ext] = "text/plain";
        return p;
    }

    public static string ForPath(string path) =>
        Provider.TryGetContentType(path, out var ct) ? ct : "application/octet-stream";

    /// <summary>Only these types are served inline; everything else is a download (avoids stored XSS via html/svg/js).</summary>
    public static bool IsSafeInline(string contentType)
    {
        var t = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return t is "text/plain" or "text/markdown" or "application/pdf" or "application/json"
            or "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp";
    }
}
