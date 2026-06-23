#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Fetch;

public sealed class PagedApiClient : IPagedApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public PagedApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PagedApiPage> GetPageAsync(
        SwaggerSyncEntityMetadata metadata,
        SyncDateRange range,
        int top,
        int skip,
        CancellationToken cancellationToken)
    {
        var request = PagedApiRequestBuilder.BuildPageRequest(metadata, range, top, skip);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.PathAndQuery);
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = response.Content == null
            ? string.Empty
            : await ReadAsStringWithTimeoutAsync(response.Content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Fetching page for sync entity '{metadata.Key}' returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Fetching page for sync entity '{metadata.Key}' returned JSON '{document.RootElement.ValueKind}' instead of an array.");
        }

        var rows = document.RootElement
            .EnumerateArray()
            .Select(row => row.Clone())
            .ToArray();
        var pagination = ReadPagination(response, rows.Length);

        return new PagedApiPage(rows, pagination.TotalCount, pagination.TotalPages);
    }

    private static Pagination ReadPagination(HttpResponseMessage response, int rowCount)
    {
        if (response.Headers.TryGetValues("x-pagination", out var headerValues))
        {
            var header = headerValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(header))
            {
                var pagination = JsonSerializer.Deserialize<Pagination>(header, JsonOptions);
                if (pagination != null)
                {
                    return pagination;
                }
            }
        }

        return new Pagination(rowCount, rowCount, CurrentPage: 1, TotalPages: 1);
    }

    private async Task<string> ReadAsStringWithTimeoutAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_httpClient.Timeout);
        }

        return await content.ReadAsStringAsync(timeout.Token);
    }

    private sealed record Pagination(
        int TotalCount,
        int PageSize,
        int CurrentPage,
        int TotalPages);
}
