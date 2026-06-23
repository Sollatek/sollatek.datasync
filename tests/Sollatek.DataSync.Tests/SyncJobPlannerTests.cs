using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SyncJobPlannerTests
{
    [Fact]
    public void Plan_PreservesConfiguredOrderAndMetadataAliases()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithSyncMetadata)]);
        var plan = SwaggerBackedSyncPlanResolver.Resolve(
            registry,
            ["rawDataLocationdata", "assets"]);
        var options = SyncOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sync:startFrom"] = "2026-02-03T04:05:06Z"
                })
                .Build(),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        var jobs = SyncJobPlanner.Plan(
            plan,
            options,
            new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(["rawDataLocationdata", "assets"], jobs.Select(x => x.Metadata.Key));
        Assert.Equal(SyncTransferMode.AsyncExport, jobs[0].TransferMode);
        Assert.Equal(SyncTransferMode.AsyncExport, jobs[1].TransferMode);
        Assert.All(jobs, job =>
        {
            Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), job.Range.Start);
            Assert.Equal(new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero), job.Range.End);
        });
    }

    [Fact]
    public void Plan_UsesPersistedEntityStateWhenAvailable()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithSyncMetadata)]);
        var plan = SwaggerBackedSyncPlanResolver.Resolve(
            registry,
            ["rawDataLocationdata", "assets"]);
        var options = SyncOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sync:startFrom"] = "2026-02-03T04:05:06Z"
                })
                .Build(),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        var lastSuccessfulEnds = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["rawDataLocationdata"] = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero)
        };

        var jobs = SyncJobPlanner.Plan(
            plan,
            options,
            lastSuccessfulEnds,
            new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero), jobs[0].Range.Start);
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), jobs[1].Range.Start);
        Assert.All(jobs, job => Assert.Equal(new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero), job.Range.End));
    }

    [Fact]
    public void Plan_AllowsMetadataOnlyJobs()
    {
        var metadata = new SwaggerSyncEntityMetadata
        {
            Key = "firmwares",
            OperationIds = ["Firmwares_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["portal-v1"]
        };
        var options = SyncOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sync:startFrom"] = "2026-06-01T00:00:00Z"
                })
                .Build(),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        var job = Assert.Single(SyncJobPlanner.Plan(
            new SwaggerBackedSyncPlan([metadata]),
            options,
            new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("firmwares", job.Metadata.Key);
        Assert.Equal(SyncTransferMode.AsyncExport, job.TransferMode);
    }

    [Fact]
    public void Plan_UsesFullDataModeWhenInitialPolicyIsFullAndTargetIsEmpty()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithSyncMetadata)]);
        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, ["assets"]);
        var startFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);
        var options = SyncOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sync:startFrom"] = startFrom.ToString("O")
                })
                .Build(),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        var syncPlanOptions = new SyncPlanOptions
        {
            Entities = new Dictionary<string, SyncPlanEntityOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["assets"] = new() { Initial = SyncInitialDataMode.Full }
            }
        };
        var planningStates = new Dictionary<string, SyncEntityPlanningState>(StringComparer.OrdinalIgnoreCase)
        {
            ["assets"] = new(HasStoredData: false, LastSuccessfulEnd: null)
        };

        var job = Assert.Single(SyncJobPlanner.Plan(
            plan,
            options,
            syncPlanOptions,
            planningStates,
            rangeEnd));

        Assert.True(job.IsInitial);
        Assert.Equal(SyncDataMode.Full, job.DataMode);
        Assert.Equal(SyncTransferMode.AsyncExport, job.TransferMode);
        Assert.Equal(startFrom, job.Range.Start);
        Assert.Equal(rangeEnd, job.Range.End);
    }

    [Fact]
    public void Plan_UsesPagedTransferModeWhenConfigured()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithSyncMetadata)]);
        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, ["assets"]);
        var options = SyncOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sync:startFrom"] = "2026-01-01T00:00:00Z",
                    ["Sync:transferMode"] = "pagedApi"
                })
                .Build(),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        var job = Assert.Single(SyncJobPlanner.Plan(
            plan,
            options,
            new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero)));

        Assert.Equal(SyncTransferMode.PagedApi, job.TransferMode);
    }

    [Fact]
    public void Plan_UsesDifferentialDataModeWhenInitialPolicyIsFullAndTargetHasData()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithSyncMetadata)]);
        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, ["assets"]);
        var lastSuccessfulEnd = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);
        var options = SyncOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sync:startFrom"] = "2026-01-01T00:00:00Z"
                })
                .Build(),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        var syncPlanOptions = new SyncPlanOptions
        {
            Entities = new Dictionary<string, SyncPlanEntityOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["assets"] = new() { Initial = SyncInitialDataMode.Full }
            }
        };
        var planningStates = new Dictionary<string, SyncEntityPlanningState>(StringComparer.OrdinalIgnoreCase)
        {
            ["assets"] = new(HasStoredData: true, LastSuccessfulEnd: lastSuccessfulEnd)
        };

        var job = Assert.Single(SyncJobPlanner.Plan(
            plan,
            options,
            syncPlanOptions,
            planningStates,
            rangeEnd));

        Assert.False(job.IsInitial);
        Assert.Equal(SyncDataMode.Differential, job.DataMode);
        Assert.Equal(lastSuccessfulEnd, job.Range.Start);
        Assert.Equal(rangeEnd, job.Range.End);
    }

    private const string SwaggerWithSyncMetadata = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": { "operationId": "Assets_Get", "primaryKey": ["id"] },
          "rawDataLocationdata": {
            "operationId": "RawData_GetLocationData",
            "primaryKey": ["id"],
            "watermark": { "field": "deviceTimestamp", "tieBreakers": ["id"] }
          }
        }
      }
    }
    """;
}
