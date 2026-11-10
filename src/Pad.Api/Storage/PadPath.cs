namespace Pad.Api.Storage;

/// <summary>Validates and normalizes user-supplied paths. Result has no leading/trailing slash.</summary>
public static class PadPath
{
    public const string KeepName = ".padkeep";
    public const int MaxLength = 1024;

    public static string Normalize(string? raw, bool allowEmpty = false)
    {
        var p = (raw ?? "").Trim('/');
        if (p.Length == 0)
        {
            if (allowEmpty) return "";
            throw new BadPathException("Path is required.");
        }
        if (p.Length > MaxLength) throw new BadPathException("Path is too long.");
        if (p.Contains('\\')) throw new BadPathException("Backslashes are not allowed.");
        foreach (var ch in p)
            if (ch < 0x20 || ch == 0x7F) throw new BadPathException("Control characters are not allowed.");
        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0) throw new BadPathException("Empty path segment.");
            if (seg is "." or "..") throw new BadPathException("Relative segments are not allowed.");
            if (seg == KeepName) throw new BadPathException($"'{KeepName}' is reserved.");
        }
        return p;
    }

    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
