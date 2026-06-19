using Sollatek.DataSync.Execution;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SyncStateCoordinatorTests
{
    [Fact]
    public async Task LoadLastSuccessfulEndsAsync_ReadsStateForEntitiesWithStoredTargetData()
    {
        var stateStore = new RecordingSyncStateStore
        {
            SavedEnds =
            {
                ["assets"] = new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero)
            }
        };
        var targetDataStore = new RecordingSyncTargetDataStore
        {
            EntitiesWithStoredData = { "assets", "customers" }
        };
        var metadata = new[]
        {
            Metadata("assets"),
            Metadata("customers")
        };

        var result = await SyncStateCoordinator.LoadLastSuccessfulEndsAsync(
            stateStore,
            targetDataStore,
            metadata,
            CancellationToken.None);

        Assert.Equal(["assets", "customers"], targetDataStore.Reads);
        Assert.Equal(["assets", "customers"], stateStore.Reads);
        Assert.Equal(new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero), result["assets"]);
        Assert.False(result.ContainsKey("customers"));
    }

    [Fact]
    public async Task LoadLastSuccessfulEndsAsync_PrefersLatestStoredWatermarkOverSavedCycleEnd()
    {
        var stateStore = new RecordingSyncStateStore
        {
            SavedEnds =
            {
                ["assets"] = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero)
            }
        };
        var storedWatermark = new DateTimeOffset(2026, 6, 19, 10, 30, 0, TimeSpan.Zero);
        var targetDataStore = new RecordingSyncTargetDataStore
        {
            EntitiesWithStoredData = { "assets" },
            StoredWatermarks =
            {
                ["assets"] = storedWatermark
            }
        };

        var result = await SyncStateCoordinator.LoadLastSuccessfulEndsAsync(
            stateStore,
            targetDataStore,
            [MetadataWithWatermark("assets", "modification.dateTime")],
            CancellationToken.None);

        Assert.Equal(storedWatermark, result["assets"]);
        Assert.Empty(stateStore.Reads);
        Assert.Equal(["assets"], targetDataStore.WatermarkReads);
    }

    [Fact]
    public async Task LoadLastSuccessfulEndsAsync_FallsBackToStateWhenStoredWatermarkIsUnavailable()
    {
        var savedEnd = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var stateStore = new RecordingSyncStateStore
        {
            SavedEnds =
            {
                ["assets"] = savedEnd
            }
        };
        var targetDataStore = new RecordingSyncTargetDataStore
        {
            EntitiesWithStoredData = { "assets" }
        };

        var result = await SyncStateCoordinator.LoadLastSuccessfulEndsAsync(
            stateStore,
            targetDataStore,
            [MetadataWithWatermark("assets", "modification.dateTime")],
            CancellationToken.None);

        Assert.Equal(savedEnd, result["assets"]);
        Assert.Equal(["assets"], stateStore.Reads);
        Assert.Equal(["assets"], targetDataStore.WatermarkReads);
    }

    [Fact]
    public async Task LoadLastSuccessfulEndsAsync_IgnoresStateForEntitiesWithoutStoredTargetData()
    {
        var stateStore = new RecordingSyncStateStore
        {
            SavedEnds =
            {
                ["assets"] = new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero)
            }
        };
        var targetDataStore = new RecordingSyncTargetDataStore();

        var result = await SyncStateCoordinator.LoadLastSuccessfulEndsAsync(
            stateStore,
            targetDataStore,
            [Metadata("assets")],
            CancellationToken.None);

        Assert.Equal(["assets"], targetDataStore.Reads);
        Assert.Empty(stateStore.Reads);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SaveSuccessfulEndsAsync_SavesEachJobEnd()
    {
        var stateStore = new RecordingSyncStateStore();
        var jobs = new[]
        {
            Job("assets", new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero)),
            Job("customers", new DateTimeOffset(2026, 6, 19, 8, 31, 0, TimeSpan.Zero))
        };

        await SyncStateCoordinator.SaveSuccessfulEndsAsync(
            stateStore,
            jobs,
            CancellationToken.None);

        Assert.Equal(
            [
                ("assets", new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero)),
                ("customers", new DateTimeOffset(2026, 6, 19, 8, 31, 0, TimeSpan.Zero))
            ],
            stateStore.Writes);
    }

    private static SyncJob Job(string entityKey, DateTimeOffset end)
    {
        return new SyncJob(
            Metadata(entityKey),
            new SyncDateRange(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), end),
            SyncTransferMode.PagedApi);
    }

    private static SwaggerSyncEntityMetadata Metadata(string entityKey)
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = entityKey,
            OperationIds = [$"{entityKey}_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }

    private static SwaggerSyncEntityMetadata MetadataWithWatermark(
        string entityKey,
        string watermarkField)
    {
        return Metadata(entityKey) with
        {
            Watermark = new SwaggerSyncWatermarkMetadata
            {
                Field = watermarkField,
                TieBreakers = []
            }
        };
    }

    private sealed class RecordingSyncStateStore : ISyncStateStore
    {
        public Dictionary<string, DateTimeOffset> SavedEnds { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Reads { get; } = [];

        public List<(string EntityKey, DateTimeOffset End)> Writes { get; } = [];

        public Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
            string entityKey,
            CancellationToken cancellationToken)
        {
            Reads.Add(entityKey);
            return Task.FromResult<DateTimeOffset?>(
                SavedEnds.TryGetValue(entityKey, out var end) ? end : null);
        }

        public Task SaveSuccessfulEndAsync(
            string entityKey,
            DateTimeOffset end,
            CancellationToken cancellationToken)
        {
            Writes.Add((entityKey, end));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSyncTargetDataStore : ISyncTargetDataStore
    {
        public HashSet<string> EntitiesWithStoredData { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DateTimeOffset> StoredWatermarks { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Reads { get; } = [];

        public List<string> WatermarkReads { get; } = [];

        public Task<bool> HasStoredDataAsync(
            SwaggerSyncEntityMetadata metadata,
            CancellationToken cancellationToken)
        {
            Reads.Add(metadata.Key);
            return Task.FromResult(EntitiesWithStoredData.Contains(metadata.Key));
        }

        public Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
            SwaggerSyncEntityMetadata metadata,
            CancellationToken cancellationToken)
        {
            WatermarkReads.Add(metadata.Key);
            return Task.FromResult<DateTimeOffset?>(
                StoredWatermarks.TryGetValue(metadata.Key, out var watermark)
                    ? watermark
                    : null);
        }
    }
}
