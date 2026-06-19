using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SwaggerSyncMetadataRegistryTests
{
    [Fact]
    public void Load_MergesEntitiesAcrossSwaggerDocumentsAndResolvesCrossDocumentReferences()
    {
        var registry = SwaggerSyncMetadataRegistry.Load(
            [
                new SwaggerSyncDocumentSource("data-v1", DataSwagger),
                new SwaggerSyncDocumentSource("portal-v1", PortalSwagger)
            ]);

        Assert.Equal(["assets", "firmwares"], registry.Entities.Keys.Order(StringComparer.Ordinal).ToArray());

        var assets = registry.GetEntity("assets");
        var firmwareReference = Assert.Single(assets.References);
        Assert.Equal("firmwares", firmwareReference.TargetEntity);

        var validation = registry.ValidateReferences();

        Assert.Empty(validation);
        Assert.True(registry.ContainsEntity("firmwares"));
    }

    [Fact]
    public void Load_AllowsDuplicateEntityKeysWhenMetadataMatches()
    {
        var registry = SwaggerSyncMetadataRegistry.Load(
            [
                new SwaggerSyncDocumentSource("data-v1", DataSwagger),
                new SwaggerSyncDocumentSource("data-copy", DataSwagger)
            ]);

        var assets = registry.GetEntity("assets");

        Assert.Equal("assets", assets.Key);
        Assert.Equal(["Assets_GetAssets"], assets.OperationIds);
        Assert.Equal(["data-v1", "data-copy"], assets.DocumentNames);
    }

    [Fact]
    public void Load_ReadsWatermarkMetadata()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", DataSwagger)]);

        var assets = registry.GetEntity("assets");

        Assert.NotNull(assets.Watermark);
        Assert.Equal("modification.dateTime", assets.Watermark.Field);
        Assert.Equal(["id"], assets.Watermark.TieBreakers);
    }

    [Fact]
    public void Load_ReadsCollectionMetadata()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", DataSwagger)]);

        var assets = registry.GetEntity("assets");

        Assert.Equal("assets", assets.Collection);
    }

    [Fact]
    public void Load_ReadsScalarFieldsFromEntitySchemaComponent()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", DataSwagger)]);

        var assets = registry.GetEntity("assets");

        Assert.Equal(
            ["id", "serial", "isDeleted"],
            assets.ScalarFields.Select(x => x.Source).ToArray());
        Assert.Equal(
            ["id", "serial", "is_deleted"],
            assets.ScalarFields.Select(x => x.LocalColumn).ToArray());
        Assert.DoesNotContain(assets.ScalarFields, x => x.Source == "firmware");
        Assert.DoesNotContain(assets.ScalarFields, x => x.Source == "tags");
    }

    [Fact]
    public void Load_ReadsReadSchemaMetadata()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", DataSwagger)]);

        var assetSchema = registry.GetSchema("Asset");

        Assert.Equal("Asset", assetSchema.SchemaName);
        Assert.Equal("#/components/schemas/Asset", assetSchema.Schema);
        Assert.Equal("Platform.Models.Assets.AssetDto", assetSchema.Type);
        Assert.Equal("schema", assetSchema.MetadataSource);
        Assert.Equal(["id", "serial"], assetSchema.ScalarFields.Select(x => x.Source).ToArray());
        Assert.Equal(["id"], assetSchema.PrimaryKey);
        Assert.NotNull(assetSchema.Watermark);
        Assert.Equal("modification.dateTime", assetSchema.Watermark.Field);
        var reference = Assert.Single(assetSchema.References);
        Assert.Equal("firmware.id", reference.Source);
        Assert.Equal("firmwares", reference.TargetEntity);
        var operation = Assert.Single(assetSchema.Operations);
        Assert.Equal("Assets_GetAssets", operation.OperationId);
        Assert.Equal("/api/Assets", operation.Path);
    }

    [Fact]
    public void Load_ReadsSchemasWhenEntityMetadataIsAbsent()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("schema-only", SchemaOnlySwagger)]);

        var schema = registry.GetSchema("Address");

        Assert.Empty(registry.Entities);
        Assert.Equal("Address", schema.SchemaName);
        Assert.Empty(schema.PrimaryKey);
        Assert.Empty(schema.References);
    }

    [Fact]
    public void Load_ResolvesEntityOperationPathsFromSwaggerOperationIds()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", DataSwagger)]);

        var assets = registry.GetEntity("assets");

        var operation = Assert.Single(assets.Operations);
        Assert.Equal("Assets_GetAssets", operation.OperationId);
        Assert.Equal("get", operation.Method);
        Assert.Equal("/api/Assets", operation.Path);
        Assert.Equal("data-v1", operation.DocumentName);
    }

    [Fact]
    public void Load_RejectsDuplicateEntityKeysWhenPrimaryKeysConflict()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncMetadataRegistry.Load(
                [
                    new SwaggerSyncDocumentSource("data-v1", DataSwagger),
                    new SwaggerSyncDocumentSource("conflict", ConflictingAssetsSwagger)
                ]));

        Assert.Contains("Conflicting sync metadata for entity 'assets'", exception.Message);
        Assert.Contains("primaryKey", exception.Message);
    }

    [Fact]
    public void Load_RejectsDuplicateEntityKeysWhenWatermarkConflicts()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncMetadataRegistry.Load(
                [
                    new SwaggerSyncDocumentSource("data-v1", DataSwagger),
                    new SwaggerSyncDocumentSource("conflict", ConflictingWatermarkSwagger)
                ]));

        Assert.Contains("Conflicting sync metadata for entity 'assets'", exception.Message);
        Assert.Contains("watermark", exception.Message);
    }

    [Fact]
    public void Load_RejectsUnsupportedMetadataVersion()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("bad", UnsupportedVersionSwagger)]));

        Assert.Contains("Unsupported x-sollatek-sync version '2'", exception.Message);
    }

    [Fact]
    public void ValidateReferences_ReportsTargetsMissingFromLoadedSwaggerDocuments()
    {
        var registry = SwaggerSyncMetadataRegistry.Load([new SwaggerSyncDocumentSource("data-v1", DataSwagger)]);

        var validation = registry.ValidateReferences();

        var error = Assert.Single(validation);
        Assert.Contains("assets", error);
        Assert.Contains("firmwares", error);
    }

    [Fact]
    public void PlanReferenceStorage_UsesForeignKeyOnlyWhenTargetEntityIsSelected()
    {
        var registry = SwaggerSyncMetadataRegistry.Load(
            [
                new SwaggerSyncDocumentSource("data-v1", DataSwagger),
                new SwaggerSyncDocumentSource("portal-v1", PortalSwagger)
            ]);
        var assets = registry.GetEntity("assets");
        var firmwareReference = Assert.Single(assets.References);

        var flatDecision = SyncReferenceStoragePlanner.Decide(firmwareReference, selectedEntityKeys: ["assets"]);
        var foreignKeyDecision = SyncReferenceStoragePlanner.Decide(firmwareReference, selectedEntityKeys: ["assets", "firmwares"]);

        Assert.Equal(SyncReferenceStorageMode.FlatValue, flatDecision.Mode);
        Assert.Equal("firmware_id", flatDecision.LocalColumn);
        Assert.Equal(SyncReferenceStorageMode.ForeignKey, foreignKeyDecision.Mode);
        Assert.Equal("firmwares", foreignKeyDecision.TargetEntity);
        Assert.Equal("id", foreignKeyDecision.TargetKey);
    }

    private const string DataSwagger = """
    {
      "openapi": "3.0.1",
      "paths": {
        "/api/Assets": {
          "get": { "operationId": "Assets_GetAssets" }
        }
      },
      "components": {
        "schemas": {
          "AssetDto": {
            "type": "object",
            "properties": {
              "id": { "type": "string", "format": "guid" },
              "serial": { "type": "string" },
              "isDeleted": { "type": "boolean" },
              "firmware": { "$ref": "#/components/schemas/FirmwareDto" },
              "tags": { "type": "array", "items": { "type": "string" } }
            }
          },
          "Asset": {
            "type": "object",
            "properties": {
              "id": { "type": "string", "format": "guid" },
              "serial": { "type": "string" },
              "firmware": { "$ref": "#/components/schemas/FirmwareDto" }
            }
          }
        }
      },
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": {
            "schema": "#/components/schemas/AssetDto",
            "operationId": "Assets_GetAssets",
            "operationIds": ["Assets_GetAssets"],
            "metadataSource": "fallback",
            "table": "assets",
            "collection": "assets",
            "primaryKey": ["id"],
            "watermark": {
              "field": "modification.dateTime",
              "tieBreakers": ["id"]
            },
            "references": [
              {
                "source": "firmware.id",
                "localColumn": "firmware_id",
                "targetEntity": "firmwares",
                "targetKey": "id",
                "nullability": "nullable",
                "enforce": "whenTargetInSyncPlan",
                "onDelete": "setNull",
                "flatFallbackColumns": []
              }
            ]
          }
        },
        "schemas": {
          "Asset": {
            "schemaName": "Asset",
            "schema": "#/components/schemas/Asset",
            "type": "Platform.Models.Assets.AssetDto",
            "operationId": "Assets_GetAssets",
            "operationIds": ["Assets_GetAssets"],
            "metadataSource": "schema",
            "primaryKey": ["id"],
            "watermark": {
              "field": "modification.dateTime",
              "tieBreakers": ["id"]
            },
            "references": [
              {
                "source": "firmware.id",
                "localColumn": "firmware_id",
                "targetEntity": "firmwares",
                "targetKey": "id",
                "nullability": "nullable",
                "enforce": "whenTargetInSyncPlan",
                "onDelete": "setNull",
                "flatFallbackColumns": []
              }
            ]
          }
        }
      }
    }
    """;

    private const string PortalSwagger = """
    {
      "openapi": "3.0.1",
      "paths": {},
      "components": { "schemas": {} },
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "firmwares": {
            "schema": "#/components/schemas/FirmwareDto",
            "operationId": "Firmwares_GetFirmwares",
            "operationIds": ["Firmwares_GetFirmwares"],
            "metadataSource": "fallback",
            "table": "firmwares",
            "primaryKey": ["id"],
            "references": []
          }
        }
      }
    }
    """;

    private const string SchemaOnlySwagger = """
    {
      "openapi": "3.0.1",
      "paths": {},
      "components": { "schemas": {} },
      "x-sollatek-sync": {
        "version": 1,
        "schemas": {
          "Address": {
            "schemaName": "Address",
            "schema": "#/components/schemas/Address",
            "type": "Platform.SharedKernel.Domain.DtoModels.AddressDto",
            "metadataSource": "schema",
            "primaryKey": [],
            "operationIds": []
          }
        }
      }
    }
    """;

    private const string ConflictingAssetsSwagger = """
    {
      "openapi": "3.0.1",
      "paths": {},
      "components": { "schemas": {} },
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": {
            "schema": "#/components/schemas/AssetDto",
            "operationId": "Assets_GetAssets",
            "operationIds": ["Assets_GetAssets"],
            "metadataSource": "fallback",
            "table": "assets",
            "collection": "assets",
            "primaryKey": ["serial"],
            "references": []
          }
        }
      }
    }
    """;

    private const string ConflictingWatermarkSwagger = """
    {
      "openapi": "3.0.1",
      "paths": {},
      "components": { "schemas": {} },
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": {
            "schema": "#/components/schemas/AssetDto",
            "operationId": "Assets_GetAssets",
            "operationIds": ["Assets_GetAssets"],
            "metadataSource": "fallback",
            "table": "assets",
            "collection": "assets",
            "primaryKey": ["id"],
            "watermark": {
              "field": "created.dateTime",
              "tieBreakers": ["id"]
            },
            "references": []
          }
        }
      }
    }
    """;

    private const string UnsupportedVersionSwagger = """
    {
      "openapi": "3.0.1",
      "paths": {},
      "components": { "schemas": {} },
      "x-sollatek-sync": {
        "version": 2,
        "entities": {}
      }
    }
    """;
}
