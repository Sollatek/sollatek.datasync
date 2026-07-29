using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Contract;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class FilesystemSyncContractStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsValidatedSnapshot()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Sollatek.DataSync.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var store = new FilesystemSyncContractStore(new FileExportOptions
            {
                StatePath = Path.Combine(testDirectory, "sync-state.json")
            });
            var snapshot = SyncContractSnapshot.Create("7", [AssetEntity()]);

            await store.SaveAsync(snapshot, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(snapshot.ContractHash, loaded.ContractHash);
            Assert.Equal("7", loaded.SchemaVersion);
            Assert.True(File.Exists(Path.Combine(testDirectory, "sync-contract.json")));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static SwaggerSyncEntityMetadata AssetEntity()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            OperationIds = ["Assets_Get"],
            Operations = [],
            PrimaryKey = ["id"],
            ScalarFields =
            [
                new SwaggerSyncScalarFieldMetadata
                {
                    Source = "id",
                    LocalColumn = "id",
                    Type = "string",
                    Format = "uuid",
                    IsNullable = false
                }
            ],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }
}
