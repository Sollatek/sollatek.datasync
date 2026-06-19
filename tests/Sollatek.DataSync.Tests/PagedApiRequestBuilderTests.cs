using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class PagedApiRequestBuilderTests
{
    [Fact]
    public void BuildPageRequest_UsesSwaggerPathPagingSortAndWatermarkFilter()
    {
        var request = PagedApiRequestBuilder.BuildPageRequest(
            AssetMetadata(),
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            top: 500,
            skip: 100);

        Assert.Equal("assets", request.EntityKey);
        Assert.Equal(
            "/api/Assets?$top=500&$skip=100&$orderby=modification%2FdateTime%20asc%2Cid%20asc&$filter=modification%2FdateTime%20ge%20datetimeoffset%272026-01-01T00%3A00%3A00.0000000%2B00%3A00%27%20and%20modification%2FdateTime%20lt%20datetimeoffset%272026-01-02T00%3A00%3A00.0000000%2B00%3A00%27",
            request.PathAndQuery);
    }

    [Fact]
    public void BuildPageRequest_CanUseOpenEndedWatermarkFilter()
    {
        var request = PagedApiRequestBuilder.BuildPageRequest(
            AssetMetadata(),
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                IncludeEndFilter: false),
            top: 500,
            skip: 100);

        Assert.Equal(
            "/api/Assets?$top=500&$skip=100&$orderby=modification%2FdateTime%20asc%2Cid%20asc&$filter=modification%2FdateTime%20ge%20datetimeoffset%272026-01-01T00%3A00%3A00.0000000%2B00%3A00%27",
            request.PathAndQuery);
    }

    [Fact]
    public void BuildPageRequest_SortsByPrimaryKeyWhenWatermarkIsMissing()
    {
        var metadata = AssetMetadata() with
        {
            Watermark = null
        };

        var request = PagedApiRequestBuilder.BuildPageRequest(
            metadata,
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            top: 500,
            skip: 0);

        Assert.Equal("/api/Assets?$top=500&$skip=0&$orderby=id%20asc", request.PathAndQuery);
    }

    private static SwaggerSyncEntityMetadata AssetMetadata()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            OperationIds = ["Assets_GetAssets"],
            Operations =
            [
                new SwaggerSyncOperationMetadata
                {
                    OperationId = "Assets_GetAssets",
                    Method = "get",
                    Path = "/api/Assets",
                    DocumentName = "data-v1"
                }
            ],
            PrimaryKey = ["id"],
            Watermark = new SwaggerSyncWatermarkMetadata
            {
                Field = "modification.dateTime",
                TieBreakers = ["id"]
            },
            References = [],
            DocumentNames = ["data-v1"]
        };
    }
}
