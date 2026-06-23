using System.Text;
using Sollatek.DataSync.AzureBlob;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Fetch;

namespace Sollatek.DataSync.AzureBlob.Tests;

public sealed class AzureBlobStateStoreTests
{
    [Fact]
    public async Task SyncStateStore_PersistsWatermarksUnderConfiguredRoot()
    {
        var container = new RecordingBlobStateContainer();
        var store = new AzureBlobSyncStateStore(
            container,
            new StateOptions
            {
                RootPath = "jobs/datasync-state"
            });
        var end = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);

        await store.SaveSuccessfulEndAsync("assets", end, CancellationToken.None);
        var saved = await store.GetLastSuccessfulEndAsync("assets", CancellationToken.None);

        Assert.Equal(end, saved);
        Assert.True(container.Blobs.ContainsKey("jobs/datasync-state/sync-state.json"));
    }

    [Fact]
    public async Task SyncStateStore_ReturnsNullWhenBlobDoesNotExist()
    {
        var store = new AzureBlobSyncStateStore(
            new RecordingBlobStateContainer(),
            new StateOptions());

        var saved = await store.GetLastSuccessfulEndAsync("assets", CancellationToken.None);

        Assert.Null(saved);
    }

    [Fact]
    public async Task AsyncExportStateStore_PersistsListsReadsAndDeletesStateDocuments()
    {
        var container = new RecordingBlobStateContainer();
        var store = new AzureBlobAsyncExportStateStore(
            container,
            new StateOptions
            {
                RootPath = "jobs/datasync-state"
            });
        await using var state = new MemoryStream(Encoding.UTF8.GetBytes("""{ "status": "polling" }"""));

        await store.SaveAsync("request-1", state, CancellationToken.None);
        var keys = new List<string>();
        await foreach (var key in store.ListKeysAsync(CancellationToken.None))
        {
            keys.Add(key);
        }

        await using var read = await store.OpenReadAsync("request-1", CancellationToken.None);
        using var reader = new StreamReader(read!);
        await store.DeleteAsync("request-1", CancellationToken.None);
        var deleted = await store.OpenReadAsync("request-1", CancellationToken.None);

        Assert.Equal(["request-1"], keys);
        Assert.Equal("""{ "status": "polling" }""", await reader.ReadToEndAsync());
        Assert.Null(deleted);
        Assert.False(container.Blobs.ContainsKey("jobs/datasync-state/async-exports/state/request-1.json"));
    }

    private sealed class RecordingBlobStateContainer : IBlobStateContainer
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public async Task UploadAsync(
            string blobName,
            Stream content,
            string? contentType,
            CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Blobs[blobName] = buffer.ToArray();
        }

        public Task<Stream?> OpenReadAsync(
            string blobName,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream?>(
                Blobs.TryGetValue(blobName, out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
        }

        public Task DeleteIfExistsAsync(
            string blobName,
            CancellationToken cancellationToken)
        {
            Blobs.Remove(blobName);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(
            string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var name in Blobs.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
            }

            await Task.CompletedTask;
        }
    }
}
