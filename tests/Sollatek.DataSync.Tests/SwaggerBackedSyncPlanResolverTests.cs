using Sollatek.DataSync.Sync;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SwaggerBackedSyncPlanResolverTests
{
    [Fact]
    public void Resolve_UsesDefaultMetadataOrderWhenPlanIsMissing()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithDefaultMetadata)]);

        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, configuredKeys: null);

        Assert.Equal(
            [
                "customers",
                "devices",
                "pointsOfInterest",
                "assets",
                "rawDataLocationdata",
                "rawDataExtrainfodata",
                "rawDataTemperaturedata",
                "rawDataDooropeningdata",
                "rawDataBatteryperiods"
            ],
            plan.MetadataEntities.Select(x => x.Key));
    }

    [Fact]
    public void Resolve_UsesConfiguredLegacyAliasForMetadata()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithDefaultMetadata)]);

        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, ["locationData", "assets"]);

        Assert.Equal(["rawDataLocationdata", "assets"], plan.MetadataEntities.Select(x => x.Key));
    }

    [Fact]
    public void Resolve_UsesConfiguredRawDataPathForMetadata()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", SwaggerWithDefaultMetadata)]);

        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, ["rawdata/batteryperiods", "assets"]);

        Assert.Equal(["rawDataBatteryperiods", "assets"], plan.MetadataEntities.Select(x => x.Key));
    }

    [Fact]
    public void Resolve_RejectsSelectedEntityWithoutSwaggerMetadata()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", AssetsOnlySwagger)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerBackedSyncPlanResolver.Resolve(registry, ["locationData"]));

        Assert.Contains("Unknown sync entity 'locationData'", exception.Message);
    }

    [Fact]
    public void Resolve_AllowsAnyConfiguredMetadataEntity()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("portal-v1", FirmwareSwagger)]);

        var plan = SwaggerBackedSyncPlanResolver.Resolve(registry, ["firmwares"]);

        Assert.Equal(["firmwares"], plan.MetadataEntities.Select(x => x.Key));
    }

    private const string SwaggerWithDefaultMetadata = """
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
          "customers": { "operationId": "Customers_Get", "primaryKey": ["id"] },
          "devices": { "operationId": "Devices_Get", "primaryKey": ["id"] },
          "pointsOfInterest": { "operationId": "PointsOfInterest_Get", "primaryKey": ["id"] },
          "assets": { "operationId": "Assets_Get", "primaryKey": ["id"] },
          "rawDataLocationdata": { "operationId": "RawData_GetLocationData", "primaryKey": ["id"] },
          "rawDataExtrainfodata": { "operationId": "RawData_GetExtraInfoData", "primaryKey": ["id"] },
          "rawDataTemperaturedata": { "operationId": "RawData_GetTemperatureData", "primaryKey": ["id"] },
          "rawDataDooropeningdata": { "operationId": "RawData_GetDoorOpeningData", "primaryKey": ["id"] },
          "rawDataBatteryperiods": { "operationId": "RawData_GetBatteryPeriodData2", "primaryKey": ["id"] }
        }
      }
    }
    """;

    private const string AssetsOnlySwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": { "operationId": "Assets_Get", "primaryKey": ["id"] }
        }
      }
    }
    """;

    private const string FirmwareSwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "firmwares": { "operationId": "Firmwares_Get", "primaryKey": ["id"] }
        }
      }
    }
    """;
}
