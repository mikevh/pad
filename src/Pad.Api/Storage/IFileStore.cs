namespace Pad.Api.Storage;

public class PadException(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public class BadPathException(string message) : PadException("bad_path", message, 400);
public class NotFoundException(string message) : PadException("not_found", message, 404);
public class ForbiddenException(string message) : PadException("forbidden", message, 403);
public class UnauthorizedException(string message) : PadException("unauthorized", message, 401);
public class ConflictException(string code, string message) : PadException(code, message, 409);

public record FileEntry(string Name, string Path, bool IsFolder, long? Size, DateTimeOffset? Modified, string? ETag, string? ContentType);

public sealed class FileContent : IAsyncDisposable
{
    public required Stream Stream { get; init; }
    public required string ContentType { get; init; }
    public required string ETag { get; init; }
    public required long Length { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <param name="IfMatch">Write only if the current ETag equals this value.</param>
/// <param name="MustNotExist">Write only if the file does not exist yet.</param>
public record WriteCondition(string? IfMatch = null, bool MustNotExist = false);

/// <summary>All paths passed in are already normalized via <see cref="PadPath"/>. "" means the root.</summary>
public interface IFileStore
{
    /// <summary>One level of children under <paramref name="prefix"/>; folders first is the caller's concern.</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string prefix, CancellationToken ct);
    /// <summary>Every file under the root (recursive), without folder markers. For quick open.</summary>
    Task<IReadOnlyList<FileEntry>> ListAllAsync(CancellationToken ct);
    Task<FileContent?> OpenReadAsync(string path, CancellationToken ct);
    /// <returns>New ETag.</returns>
    Task<string> WriteAsync(string path, Stream content, string contentType, WriteCondition condition, CancellationToken ct);
    /// <returns>false if the file did not exist.</returns>
    Task<bool> DeleteFileAsync(string path, string? ifMatch, CancellationToken ct);
    Task CreateFolderAsync(string path, CancellationToken ct);
    /// <returns>false if the folder had no contents.</returns>
    Task<bool> DeleteFolderAsync(string path, CancellationToken ct);
    /// <summary>Moves a file or a whole folder. Fails with 409 if the destination exists.</summary>
    Task MoveAsync(string from, string to, CancellationToken ct);
}
