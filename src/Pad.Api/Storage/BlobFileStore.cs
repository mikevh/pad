using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Pad.Api.Storage;

public sealed class BlobFileStore(BlobContainerClient container) : IFileStore
{
    public async Task<IReadOnlyList<FileEntry>> ListAsync(string prefix, CancellationToken ct)
    {
        var pre = prefix.Length == 0 ? "" : prefix + "/";
        var folders = new List<FileEntry>();
        var files = new List<FileEntry>();
        await foreach (var item in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", pre, ct))
        {
            if (item.IsPrefix)
            {
                var full = item.Prefix.TrimEnd('/');
                folders.Add(new FileEntry(PadPath.Name(full), full, true, null, null, null, null));
            }
            else
            {
                var name = PadPath.Name(item.Blob.Name);
                if (name == PadPath.KeepName) continue;
                var p = item.Blob.Properties;
                files.Add(new FileEntry(name, item.Blob.Name, false, p.ContentLength, p.LastModified, p.ETag?.ToString(), p.ContentType));
            }
        }
        return folders.Concat(files).ToList();
    }

    public async Task<IReadOnlyList<FileEntry>> ListAllAsync(CancellationToken ct)
    {
        var files = new List<FileEntry>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "", ct))
        {
            var name = PadPath.Name(b.Name);
            if (name == PadPath.KeepName) continue;
            var p = b.Properties;
            files.Add(new FileEntry(name, b.Name, false, p.ContentLength, p.LastModified, p.ETag?.ToString(), p.ContentType));
        }
        return files;
    }

    public async Task<FileContent?> OpenReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var r = await container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct);
            var d = r.Value;
            return new FileContent
            {
                Stream = d.Content, ContentType = d.Details.ContentType ?? ContentTypes.ForPath(path),
                ETag = d.Details.ETag.ToString(), Length = d.Details.ContentLength, LastModified = d.Details.LastModified,
            };
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<string> WriteAsync(string path, Stream content, string contentType, WriteCondition cond, CancellationToken ct)
    {
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } };
        if (cond.MustNotExist) options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        else if (cond.IfMatch is not null) options.Conditions = new BlobRequestConditions { IfMatch = new ETag(cond.IfMatch) };
        try
        {
            var r = await container.GetBlobClient(path).UploadAsync(content, options, ct);
            return r.Value.ETag.ToString();
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            throw cond.MustNotExist
                ? new ConflictException("already_exists", "File already exists.")
                : new ConflictException("etag_mismatch", "File changed since it was loaded.");
        }
    }

    public async Task<bool> DeleteFileAsync(string path, string? ifMatch, CancellationToken ct)
    {
        var conds = ifMatch is null ? null : new BlobRequestConditions { IfMatch = new ETag(ifMatch) };
        try
        {
            var r = await container.GetBlobClient(path).DeleteIfExistsAsync(conditions: conds, cancellationToken: ct);
            return r.Value;
        }
        catch (RequestFailedException e) when (e.Status == 412)
        {
            throw new ConflictException("etag_mismatch", "File changed since it was loaded.");
        }
    }

    public async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        if (await container.GetBlobClient(path).ExistsAsync(ct))
            throw new ConflictException("already_exists", "A file with that name exists.");
        try
        {
            await container.GetBlobClient(path + "/" + PadPath.KeepName)
                .UploadAsync(BinaryData.FromBytes([]), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { /* folder marker already there */ }
    }

    public async Task<bool> DeleteFolderAsync(string path, CancellationToken ct)
    {
        var names = await ListNamesAsync(path + "/", ct);
        foreach (var n in names) await container.GetBlobClient(n).DeleteIfExistsAsync(cancellationToken: ct);
        return names.Count > 0;
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        List<string> sources;
        bool isFile = await container.GetBlobClient(from).ExistsAsync(ct);
        if (isFile) sources = [from];
        else sources = await ListNamesAsync(from + "/", ct);
        if (sources.Count == 0) throw new NotFoundException("Source not found.");

        if (await container.GetBlobClient(to).ExistsAsync(ct) || (await ListNamesAsync(to + "/", ct)).Count > 0)
            throw new ConflictException("already_exists", "Destination already exists.");

        // Copy everything first, delete sources only after all copies succeeded.
        var copied = new List<string>();
        try
        {
            foreach (var src in sources)
            {
                var dst = isFile ? to : to + src[from.Length..];
                var op = await container.GetBlobClient(dst).StartCopyFromUriAsync(container.GetBlobClient(src).Uri, cancellationToken: ct);
                await op.WaitForCompletionAsync(ct);
                copied.Add(dst);
            }
        }
        catch
        {
            foreach (var dst in copied) await container.GetBlobClient(dst).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            throw;
        }
        foreach (var src in sources) await container.GetBlobClient(src).DeleteIfExistsAsync(cancellationToken: ct);
    }

    private async Task<List<string>> ListNamesAsync(string prefix, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct)) names.Add(b.Name);
        return names;
    }
}
