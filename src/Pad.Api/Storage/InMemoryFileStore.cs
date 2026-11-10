namespace Pad.Api.Storage;

/// <summary>Test/dev implementation with the same semantics as <see cref="BlobFileStore"/>.</summary>
public sealed class InMemoryFileStore : IFileStore
{
    private sealed record Item(byte[] Data, string ContentType, string ETag, DateTimeOffset Modified);
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";

    public Task<IReadOnlyList<FileEntry>> ListAsync(string prefix, CancellationToken ct)
    {
        var pre = prefix.Length == 0 ? "" : prefix + "/";
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        var files = new List<FileEntry>();
        lock (_lock)
        {
            foreach (var (key, item) in _items)
            {
                if (!key.StartsWith(pre, StringComparison.Ordinal)) continue;
                var rest = key[pre.Length..];
                var slash = rest.IndexOf('/');
                if (slash >= 0) folders.Add(rest[..slash]);
                else if (rest != PadPath.KeepName)
                    files.Add(new FileEntry(rest, key, false, item.Data.Length, item.Modified, item.ETag, item.ContentType));
            }
        }
        var result = folders.Select(f => new FileEntry(f, pre + f, true, null, null, null, null)).Concat(files).ToList();
        return Task.FromResult<IReadOnlyList<FileEntry>>(result);
    }

    public Task<IReadOnlyList<FileEntry>> ListAllAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<FileEntry> all = _items
                .Where(kv => PadPath.Name(kv.Key) != PadPath.KeepName)
                .Select(kv => new FileEntry(PadPath.Name(kv.Key), kv.Key, false, kv.Value.Data.Length, kv.Value.Modified, kv.Value.ETag, kv.Value.ContentType))
                .OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            return Task.FromResult(all);
        }
    }

    public Task<FileContent?> OpenReadAsync(string path, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(path, out var i)) return Task.FromResult<FileContent?>(null);
            return Task.FromResult<FileContent?>(new FileContent
            {
                Stream = new MemoryStream(i.Data, writable: false), ContentType = i.ContentType,
                ETag = i.ETag, Length = i.Data.Length, LastModified = i.Modified,
            });
        }
    }

    public async Task<string> WriteAsync(string path, Stream content, string contentType, WriteCondition cond, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        lock (_lock)
        {
            var exists = _items.TryGetValue(path, out var cur);
            if (cond.MustNotExist && exists) throw new ConflictException("already_exists", "File already exists.");
            if (cond.IfMatch is not null && (!exists || cur!.ETag != cond.IfMatch))
                throw new ConflictException("etag_mismatch", "File changed since it was loaded.");
            var etag = NewETag();
            _items[path] = new Item(ms.ToArray(), contentType, etag, DateTimeOffset.UtcNow);
            return etag;
        }
    }

    public Task<bool> DeleteFileAsync(string path, string? ifMatch, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(path, out var cur)) return Task.FromResult(false);
            if (ifMatch is not null && cur.ETag != ifMatch) throw new ConflictException("etag_mismatch", "File changed since it was loaded.");
            _items.Remove(path);
            return Task.FromResult(true);
        }
    }

    public Task CreateFolderAsync(string path, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_items.ContainsKey(path)) throw new ConflictException("already_exists", "A file with that name exists.");
            _items.TryAdd(path + "/" + PadPath.KeepName, new Item([], "application/octet-stream", NewETag(), DateTimeOffset.UtcNow));
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteFolderAsync(string path, CancellationToken ct)
    {
        lock (_lock)
        {
            var keys = _items.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
            foreach (var k in keys) _items.Remove(k);
            return Task.FromResult(keys.Count > 0);
        }
    }

    public Task MoveAsync(string from, string to, CancellationToken ct)
    {
        lock (_lock)
        {
            var isFile = _items.ContainsKey(from);
            var keys = isFile ? [from] : _items.Keys.Where(k => k.StartsWith(from + "/", StringComparison.Ordinal)).ToList();
            if (keys.Count == 0) throw new NotFoundException("Source not found.");
            if (_items.ContainsKey(to) || _items.Keys.Any(k => k.StartsWith(to + "/", StringComparison.Ordinal)))
                throw new ConflictException("already_exists", "Destination already exists.");
            foreach (var k in keys)
            {
                var nk = isFile ? to : to + k[from.Length..];
                _items[nk] = _items[k];
                _items.Remove(k);
            }
        }
        return Task.CompletedTask;
    }
}
