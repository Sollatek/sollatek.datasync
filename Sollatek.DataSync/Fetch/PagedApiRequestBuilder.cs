#nullable enable

using System.Globalization;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Fetch;

public static class PagedApiRequestBuilder
{
    public static PagedApiPageRequest BuildPageRequest(
        SwaggerSyncEntityMetadata metadata,
        SyncDateRange range,
        int top,
        int skip)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(range);

        if (top <= 0)
        {
            throw new InvalidOperationException("top must be greater than zero.");
        }

        if (skip < 0)
        {
            throw new InvalidOperationException("skip must not be negative.");
        }

        var operation = metadata.Operations.FirstOrDefault(x =>
            string.Equals(x.Method, "get", StringComparison.OrdinalIgnoreCase));
        if (operation == null)
        {
            throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' has no GET operation metadata for paged API fetch.");
        }

        if (operation.Path.Contains('{', StringComparison.Ordinal) ||
            operation.Path.Contains('}', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' paged API operation '{operation.OperationId}' requires route values and cannot be called from SyncPlan alone.");
        }

        var queryParameters = new List<(string Name, string Value)>
        {
            ("$top", top.ToString(CultureInfo.InvariantCulture)),
            ("$skip", skip.ToString(CultureInfo.InvariantCulture))
        };

        var orderBy = BuildOrderBy(metadata);
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            queryParameters.Add(("$orderby", orderBy));
        }

        var filter = BuildWatermarkFilter(metadata.Watermark, range);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            queryParameters.Add(("$filter", filter));
        }

        return new PagedApiPageRequest(
            metadata.Key,
            AppendQuery(operation.Path, queryParameters));
    }

    private static string? BuildOrderBy(SwaggerSyncEntityMetadata metadata)
    {
        var fields = metadata.Watermark == null
            ? metadata.PrimaryKey
            : new[] { metadata.Watermark.Field }
                .Concat(metadata.Watermark.TieBreakers)
                .ToArray();

        return fields.Count == 0
            ? null
            : string.Join(",", fields.Select(field => $"{ToQueryFieldPath(field)} asc"));
    }

    private static string? BuildWatermarkFilter(
        SwaggerSyncWatermarkMetadata? watermark,
        SyncDateRange range)
    {
        if (watermark == null)
        {
            return null;
        }

        var start = FormatDateTimeOffsetLiteral(range.Start);
        if (!range.IncludeEndFilter)
        {
            return $"{ToQueryFieldPath(watermark.Field)} ge {start}";
        }

        var field = ToQueryFieldPath(watermark.Field);
        return $"{field} ge {start} and {field} lt {FormatDateTimeOffsetLiteral(range.End)}";
    }

    private static string ToQueryFieldPath(string field)
    {
        return field.Replace(".", "/", StringComparison.Ordinal);
    }

    private static string FormatDateTimeOffsetLiteral(DateTimeOffset value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"datetimeoffset'{value.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffffffzzz}'");
    }

    private static string AppendQuery(
        string path,
        IReadOnlyList<(string Name, string Value)> queryParameters)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var query = string.Join(
            "&",
            queryParameters.Select(x => $"{x.Name}={Uri.EscapeDataString(x.Value)}"));

        return $"{path}{separator}{query}";
    }
}
