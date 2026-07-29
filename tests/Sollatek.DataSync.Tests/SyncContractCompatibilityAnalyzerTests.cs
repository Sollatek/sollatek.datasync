using Sollatek.DataSync.Sync.Contract;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SyncContractCompatibilityAnalyzerTests
{
    [Fact]
    public void SnapshotHash_IsStableWhenUnorderedMetadataMoves()
    {
        var entity = AssetEntity() with
        {
            ScalarFields =
            [
                Scalar("reading", "integer", "int64", isNullable: true),
                Scalar("serial", "string", null, isNullable: false)
            ],
            DocumentNames = ["portal-v1", "data-v1"]
        };
        var reordered = entity with
        {
            ScalarFields = entity.ScalarFields.Reverse().ToArray(),
            DocumentNames = entity.DocumentNames.Reverse().ToArray()
        };

        var first = Snapshot(entity);
        var second = Snapshot(reordered);

        Assert.Equal(first.ContractHash, second.ContractHash);
    }

    [Fact]
    public void Compare_IgnoresSchemaComponentRename()
    {
        var accepted = Snapshot(AssetEntity() with
        {
            Schema = "#/components/schemas/AssetSimple2"
        });
        var candidate = Snapshot(AssetEntity() with
        {
            Schema = "#/components/schemas/AssetSummary"
        });

        var result = SyncContractCompatibilityAnalyzer.Compare(accepted, candidate);

        Assert.True(result.IsCompatible);
        Assert.Empty(result.BreakingChanges);
    }

    [Fact]
    public void Compare_AllowsNullableScalarAddition()
    {
        var acceptedEntity = AssetEntity();
        var candidateEntity = acceptedEntity with
        {
            ScalarFields =
            [
                .. acceptedEntity.ScalarFields,
                Scalar("optionalCounter", "integer", "int64", isNullable: true)
            ]
        };

        var result = SyncContractCompatibilityAnalyzer.Compare(
            Snapshot(acceptedEntity),
            Snapshot(candidateEntity));

        Assert.True(result.IsCompatible);
        Assert.Contains(result.CompatibleChanges, change =>
            change.Contains("optionalCounter", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_RejectsRequiredScalarAddition()
    {
        var acceptedEntity = AssetEntity();
        var candidateEntity = acceptedEntity with
        {
            ScalarFields =
            [
                .. acceptedEntity.ScalarFields,
                Scalar("requiredCounter", "integer", "int64", isNullable: false)
            ]
        };

        var result = SyncContractCompatibilityAnalyzer.Compare(
            Snapshot(acceptedEntity),
            Snapshot(candidateEntity));

        Assert.False(result.IsCompatible);
        Assert.Contains(result.BreakingChanges, change =>
            change.Contains("not nullable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("integer", "int32", "integer", "int64")]
    [InlineData("integer", "int64", "integer", "uint64")]
    [InlineData("integer", "uint64", "integer", "int64")]
    [InlineData("integer", "int64", "number", "double")]
    [InlineData("number", "float", "number", "double")]
    [InlineData("string", null, "string", "uuid")]
    [InlineData("string", "date-time", "integer", "int64")]
    public void Compare_RejectsScalarTypeAndFormatChanges(
        string acceptedType,
        string? acceptedFormat,
        string candidateType,
        string? candidateFormat)
    {
        var accepted = AssetEntity() with
        {
            ScalarFields =
            [
                Scalar("reading", acceptedType, acceptedFormat, isNullable: true)
            ]
        };
        var candidate = accepted with
        {
            ScalarFields =
            [
                Scalar("reading", candidateType, candidateFormat, isNullable: true)
            ]
        };

        var result = SyncContractCompatibilityAnalyzer.Compare(
            Snapshot(accepted),
            Snapshot(candidate));

        Assert.False(result.IsCompatible);
        Assert.Contains(result.BreakingChanges, change =>
            change.Contains("type changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_RejectsRemovalKeyWatermarkAndOperationChanges()
    {
        var acceptedEntity = AssetEntity();
        var candidateEntity = acceptedEntity with
        {
            PrimaryKey = ["serial"],
            ScalarFields = acceptedEntity.ScalarFields
                .Where(field => field.Source != "reading")
                .ToArray(),
            Watermark = new SwaggerSyncWatermarkMetadata
            {
                Field = "createdAt",
                TieBreakers = ["serial"]
            },
            Operations =
            [
                new SwaggerSyncOperationMetadata
                {
                    OperationId = "Assets_Get",
                    Method = "GET",
                    Path = "/v2/assets",
                    DocumentName = "data-v1"
                }
            ]
        };

        var result = SyncContractCompatibilityAnalyzer.Compare(
            Snapshot(acceptedEntity),
            Snapshot(candidateEntity));

        Assert.False(result.IsCompatible);
        Assert.Contains(result.BreakingChanges, change =>
            change.Contains("primary key", StringComparison.Ordinal));
        Assert.Contains(result.BreakingChanges, change =>
            change.Contains("was removed or renamed", StringComparison.Ordinal));
        Assert.Contains(result.BreakingChanges, change =>
            change.Contains("watermark changed", StringComparison.Ordinal));
        Assert.Contains(result.BreakingChanges, change =>
            change.Contains("API operation changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_RejectsSyncPlanOrderChange()
    {
        var customers = AssetEntity() with
        {
            Key = "customers",
            Table = "customers",
            Collection = "customers"
        };
        var assets = AssetEntity();

        var result = SyncContractCompatibilityAnalyzer.Compare(
            Snapshot(customers, assets),
            Snapshot(assets, customers));

        Assert.False(result.IsCompatible);
        Assert.Contains("Sync plan entity order changed.", result.BreakingChanges);
    }

    private static SyncContractSnapshot Snapshot(
        params SwaggerSyncEntityMetadata[] entities)
    {
        return SyncContractSnapshot.Create(
            "1",
            entities,
            new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
    }

    private static SwaggerSyncEntityMetadata AssetEntity()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            Schema = "#/components/schemas/Asset",
            OperationId = "Assets_Get",
            OperationIds = ["Assets_Get"],
            Operations =
            [
                new SwaggerSyncOperationMetadata
                {
                    OperationId = "Assets_Get",
                    Method = "GET",
                    Path = "/v1/assets",
                    DocumentName = "data-v1"
                }
            ],
            MetadataSource = "schema",
            Table = "assets",
            Collection = "assets",
            PrimaryKey = ["id"],
            ScalarFields =
            [
                Scalar("id", "string", "uuid", isNullable: false),
                Scalar("reading", "integer", "int64", isNullable: true)
            ],
            Watermark = new SwaggerSyncWatermarkMetadata
            {
                Field = "modifiedAt",
                TieBreakers = ["id"]
            },
            References = [],
            DocumentNames = ["data-v1"]
        };
    }

    private static SwaggerSyncScalarFieldMetadata Scalar(
        string source,
        string type,
        string? format,
        bool isNullable)
    {
        return new SwaggerSyncScalarFieldMetadata
        {
            Source = source,
            LocalColumn = source,
            Type = type,
            Format = format,
            IsNullable = isNullable
        };
    }
}
