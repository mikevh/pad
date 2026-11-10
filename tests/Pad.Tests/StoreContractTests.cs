using System.Text;
using Azure.Storage.Blobs;
using Pad.Api.Storage;

namespace Pad.Tests;

/// <summary>Runs the same behavior checks against the in-memory store and, when PAD_TEST_BLOB_CONNECTION is set (e.g. Azurite), the real blob store.</summary>
public class StoreContractTests
{
    public static IEnumerable<object[]> Kinds()
    {
        yield return ["memory"];
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAD_TEST_BLOB_CONNECTION"))) yield return ["blob"];
    }

    private static async Task<(IFileStore Store, Func<Task> Cleanup)> Create(string kind)
    {
        if (kind == "memory") return (new InMemoryFileStore(), () => Task.CompletedTask);
        var container = new BlobContainerClient(Environment.GetEnvironmentVariable("PAD_TEST_BLOB_CONNECTION"), "t" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        return (new BlobFileStore(container), async () => await container.DeleteIfExistsAsync());
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));
    private static async Task<string> Text(IFileStore s, string path)
    {
        await using var f = await s.OpenReadAsync(path, default) ?? throw new Exception("missing " + path);
        return await new StreamReader(f.Stream).ReadToEndAsync();
    }
    private static async Task Run(string kind, Func<IFileStore, Task> body)
    {
        var (store, cleanup) = await Create(kind);
        try { await body(store); } finally { await cleanup(); }
    }

    [Theory, MemberData(nameof(Kinds))]
    public Task Write_then_read_roundtrip(string kind) => Run(kind, async s =>
    {
        var etag = await s.WriteAsync("notes/a.md", Bytes("hello"), "text/markdown", new WriteCondition(), default);
        Assert.False(string.IsNullOrEmpty(etag));
        Assert.Equal("hello", await Text(s, "notes/a.md"));
        await using var f = await s.OpenReadAsync("notes/a.md", default);
        Assert.Equal(etag, f!.ETag);
        Assert.Equal("text/markdown", f.ContentType);
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Read_missing_returns_null(string kind) => Run(kind, async s =>
        Assert.Null(await s.OpenReadAsync("nope.txt", default)));

    [Theory, MemberData(nameof(Kinds))]
    public Task IfMatch_stale_etag_conflicts(string kind) => Run(kind, async s =>
    {
        var e1 = await s.WriteAsync("a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        var e2 = await s.WriteAsync("a.txt", Bytes("2"), "text/plain", new WriteCondition(IfMatch: e1), default);
        Assert.NotEqual(e1, e2);
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            s.WriteAsync("a.txt", Bytes("3"), "text/plain", new WriteCondition(IfMatch: e1), default));
        Assert.Equal("etag_mismatch", ex.Code);
        Assert.Equal("2", await Text(s, "a.txt"));
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task MustNotExist_conflicts_when_present(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("a.txt", Bytes("1"), "text/plain", new WriteCondition(MustNotExist: true), default);
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            s.WriteAsync("a.txt", Bytes("2"), "text/plain", new WriteCondition(MustNotExist: true), default));
        Assert.Equal("already_exists", ex.Code);
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task List_returns_one_level_and_hides_markers(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("root.txt", Bytes("r"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("work/x.md", Bytes("x"), "text/markdown", new WriteCondition(), default);
        await s.WriteAsync("work/deep/y.md", Bytes("y"), "text/markdown", new WriteCondition(), default);
        await s.CreateFolderAsync("empty", default);

        var root = await s.ListAsync("", default);
        Assert.Equal(["empty", "root.txt", "work"], root.Select(e => e.Name).Order().ToArray());
        Assert.True(root.Single(e => e.Name == "work").IsFolder);
        Assert.True(root.Single(e => e.Name == "empty").IsFolder);
        Assert.False(root.Single(e => e.Name == "root.txt").IsFolder);

        var work = await s.ListAsync("work", default);
        Assert.Equal(["deep", "x.md"], work.Select(e => e.Name).Order().ToArray());
        Assert.Empty(await s.ListAsync("empty", default)); // marker hidden
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task ListAll_is_recursive_and_hides_markers(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("x/y/b.md", Bytes("2"), "text/markdown", new WriteCondition(), default);
        await s.CreateFolderAsync("empty", default);
        var all = await s.ListAllAsync(default);
        Assert.Equal(["a.txt", "x/y/b.md"], all.Select(e => e.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.All(all, e => Assert.False(e.IsFolder));
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Delete_file(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        Assert.True(await s.DeleteFileAsync("a.txt", null, default));
        Assert.False(await s.DeleteFileAsync("a.txt", null, default));
        Assert.Null(await s.OpenReadAsync("a.txt", default));
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Delete_file_with_stale_etag_conflicts(string kind) => Run(kind, async s =>
    {
        var e1 = await s.WriteAsync("a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("a.txt", Bytes("2"), "text/plain", new WriteCondition(), default);
        await Assert.ThrowsAsync<ConflictException>(() => s.DeleteFileAsync("a.txt", e1, default));
        Assert.NotNull(await s.OpenReadAsync("a.txt", default));
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Delete_folder_is_recursive(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("d/a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("d/sub/b.txt", Bytes("2"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("keep.txt", Bytes("3"), "text/plain", new WriteCondition(), default);
        Assert.True(await s.DeleteFolderAsync("d", default));
        Assert.False(await s.DeleteFolderAsync("d", default));
        Assert.Equal(["keep.txt"], (await s.ListAsync("", default)).Select(e => e.Name).ToArray());
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Folder_name_clash_with_file_conflicts(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("thing", Bytes("1"), "text/plain", new WriteCondition(), default);
        await Assert.ThrowsAsync<ConflictException>(() => s.CreateFolderAsync("thing", default));
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Move_file(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("a.txt", Bytes("content"), "text/plain", new WriteCondition(), default);
        await s.MoveAsync("a.txt", "b/c.txt", default);
        Assert.Null(await s.OpenReadAsync("a.txt", default));
        Assert.Equal("content", await Text(s, "b/c.txt"));
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Move_folder_moves_everything(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("old/a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("old/sub/b.txt", Bytes("2"), "text/plain", new WriteCondition(), default);
        await s.CreateFolderAsync("old/emptydir", default);
        await s.MoveAsync("old", "new", default);
        Assert.Empty(await s.ListAsync("old", default));
        Assert.Equal("1", await Text(s, "new/a.txt"));
        Assert.Equal("2", await Text(s, "new/sub/b.txt"));
        Assert.Contains(await s.ListAsync("new", default), e => e.Name == "emptydir" && e.IsFolder);
    });

    [Theory, MemberData(nameof(Kinds))]
    public Task Move_onto_existing_conflicts_and_missing_source_404s(string kind) => Run(kind, async s =>
    {
        await s.WriteAsync("a.txt", Bytes("1"), "text/plain", new WriteCondition(), default);
        await s.WriteAsync("b.txt", Bytes("2"), "text/plain", new WriteCondition(), default);
        await Assert.ThrowsAsync<ConflictException>(() => s.MoveAsync("a.txt", "b.txt", default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.MoveAsync("zzz", "q", default));
        Assert.Equal("1", await Text(s, "a.txt"));
        Assert.Equal("2", await Text(s, "b.txt"));
    });
}
