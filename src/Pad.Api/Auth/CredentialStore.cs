using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Pad.Api.Auth;

public record StoredCredential(string Id, byte[] PublicKey, uint SignCount, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public byte[] IdBytes => Base64Url.Decode(Id);
}

public static class Base64Url
{
    public static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

public interface ICredentialStore
{
    Task<List<StoredCredential>> LoadAsync(CancellationToken ct);
    Task SaveAsync(List<StoredCredential> credentials, CancellationToken ct);
}

/// <summary>Credentials live in a small JSON blob in the private system container.</summary>
public sealed class BlobCredentialStore(BlobContainerClient container) : ICredentialStore
{
    private BlobClient Blob => container.GetBlobClient("credentials.json");

    public async Task<List<StoredCredential>> LoadAsync(CancellationToken ct)
    {
        try
        {
            var r = await Blob.DownloadContentAsync(ct);
            return JsonSerializer.Deserialize<List<StoredCredential>>(r.Value.Content.ToStream()) ?? [];
        }
        catch (RequestFailedException e) when (e.Status == 404) { return []; }
    }

    public async Task SaveAsync(List<StoredCredential> credentials, CancellationToken ct) =>
        await Blob.UploadAsync(BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(credentials)),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } }, ct);
}

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private List<StoredCredential> _items = [];
    public Task<List<StoredCredential>> LoadAsync(CancellationToken ct) => Task.FromResult(_items.ToList());
    public Task SaveAsync(List<StoredCredential> credentials, CancellationToken ct) { _items = credentials.ToList(); return Task.CompletedTask; }
}
