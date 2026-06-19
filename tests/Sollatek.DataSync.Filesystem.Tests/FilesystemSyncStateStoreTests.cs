using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;

namespace Sollatek.DataSync.Tests;

public sealed class FilesystemSyncStateStoreTests
{
    [Fact]
    public async Task GetLastSuccessfulEndAsync_ReturnsNullWhenEntityHasNoState()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new FilesystemSyncStateStore(new FileExportOptions { RootPath = directory });

            var result = await store.GetLastSuccessfulEndAsync("assets", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSuccessfulEndAsync_PersistsEntityStateUnderExportRoot()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new FilesystemSyncStateStore(new FileExportOptions { RootPath = directory });
            var end = new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero);

            await store.SaveSuccessfulEndAsync("assets", end, CancellationToken.None);

            var saved = await store.GetLastSuccessfulEndAsync("assets", CancellationToken.None);
            Assert.Equal(end, saved);
            Assert.True(File.Exists(Path.Combine(directory, "_state", "sync-state.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sollatek-datasync-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
