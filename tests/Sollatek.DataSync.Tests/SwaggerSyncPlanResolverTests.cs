using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SwaggerSyncPlanResolverTests
{
    [Fact]
    public void Resolve_UsesConfiguredMetadataEntitiesInConfiguredOrder()
    {
        var registry = SwaggerSyncMetadataRegistry.Load(
            [
                new SwaggerSyncDocumentSource("data-v1", AssetsSwagger),
                new SwaggerSyncDocumentSource("portal-v1", FirmwaresSwagger)
            ]);

        var plan = SwaggerSyncPlanResolver.Resolve(registry, ["firmwares", "assets"]);

        Assert.Equal(["firmwares", "assets"], plan.Select(x => x.Key));
    }

    [Fact]
    public void Resolve_UsesLoadedMetadataOrderWhenPlanIsMissing()
    {
        var registry = SwaggerSyncMetadataRegistry.Load(
            [
                new SwaggerSyncDocumentSource("data-v1", AssetsSwagger),
                new SwaggerSyncDocumentSource("portal-v1", FirmwaresSwagger)
            ]);

        var plan = SwaggerSyncPlanResolver.Resolve(registry, configuredKeys: null);

        Assert.Equal(["assets", "firmwares"], plan.Select(x => x.Key));
    }

    [Fact]
    public void Resolve_RejectsUnknownMetadataEntity()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", AssetsSwagger)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncPlanResolver.Resolve(registry, ["assets", "missing"]));

        Assert.Contains("Unknown sync entity 'missing'", exception.Message);
        Assert.Contains("assets", exception.Message);
    }

    [Fact]
    public void Resolve_RejectsDuplicateMetadataEntity()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", AssetsSwagger)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncPlanResolver.Resolve(registry, ["assets", "Assets"]));

        Assert.Contains("Duplicate sync entity 'assets'", exception.Message);
    }

    [Theory]
    [InlineData("rawDataBatteryperiods")]
    [InlineData("rawdata/batteryperiods")]
    [InlineData("/api/RawData/batteryperiods")]
    [InlineData("batteryperiods")]
    [InlineData("raw_data_batteryperiods")]
    public void Resolve_UsesMetadataDerivedNamesAndPaths(string configuredKey)
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", RawDataSwagger)]);

        var plan = SwaggerSyncPlanResolver.Resolve(registry, [configuredKey]);

        Assert.Equal(["rawDataBatteryperiods"], plan.Select(x => x.Key));
    }

    [Fact]
    public void Resolve_RejectsDuplicateMetadataDerivedEntity()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", RawDataSwagger)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncPlanResolver.Resolve(registry, ["rawDataBatteryperiods", "rawdata/batteryperiods"]));

        Assert.Contains("Duplicate sync entity 'rawDataBatteryperiods'", exception.Message);
    }

    [Fact]
    public void Resolve_RejectsAmbiguousMetadataDerivedName()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", AmbiguousSwagger)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncPlanResolver.Resolve(registry, ["shared"]));

        Assert.Contains("matches multiple sync entities", exception.Message);
        Assert.Contains("first", exception.Message);
        Assert.Contains("second", exception.Message);
    }

    private const string AssetsSwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": {
            "operationId": "Assets_GetAssets",
            "operationIds": ["Assets_GetAssets"],
            "table": "assets",
            "primaryKey": ["id"],
            "references": []
          }
        }
      }
    }
    """;

    private const string FirmwaresSwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "firmwares": {
            "operationId": "Firmwares_GetFirmwares",
            "operationIds": ["Firmwares_GetFirmwares"],
            "table": "firmwares",
            "primaryKey": ["id"],
            "references": []
          }
        }
      }
    }
    """;

    private const string RawDataSwagger = """
    {
      "openapi": "3.0.1",
      "paths": {
        "/api/RawData/batteryperiods": {
          "get": {
            "operationId": "RawData_GetBatteryPeriodData2"
          }
        }
      },
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "rawDataBatteryperiods": {
            "operationId": "RawData_GetBatteryPeriodData2",
            "operationIds": ["RawData_GetBatteryPeriodData2"],
            "table": "raw_data_batteryperiods",
            "collection": "raw_data_batteryperiods",
            "primaryKey": ["id"],
            "references": []
          }
        }
      }
    }
    """;

    private const string AmbiguousSwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "first": {
            "operationId": "First_Get",
            "operationIds": ["First_Get"],
            "table": "shared",
            "primaryKey": ["id"],
            "references": []
          },
          "second": {
            "operationId": "Second_Get",
            "operationIds": ["Second_Get"],
            "collection": "shared",
            "primaryKey": ["id"],
            "references": []
          }
        }
      }
    }
    """;
}
