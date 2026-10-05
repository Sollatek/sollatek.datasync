#nullable enable

using System.Text;
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
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = response.Content == null
                ? string.Empty
                : await ReadAsStringWithTimeoutAsync(response.Content, cancellationToken);
            throw new InvalidOperationException(
                $"Fetching page for sync entity '{metadata.Key}' returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        using var document = response.Content == null
            ? JsonDocument.Parse(string.Empty)
            : await ReadJsonDocumentWithTimeoutAsync(response.Content, cancellationToken);
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

    private async Task<JsonDocument> ReadJsonDocumentWithTimeoutAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (RequiresTextTranscoding(content))
        {
            var responseBody = await ReadAsStringWithTimeoutAsync(content, cancellationToken);
            return JsonDocument.Parse(responseBody);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_httpClient.Timeout);
        }

        await using var stream = await content.ReadAsStreamAsync(timeout.Token);
        // Preserve the previous decoder's BOM and malformed-byte replacement behavior without a full UTF-16 string.
        await using var normalizedUtf8 = Encoding.CreateTranscodingStream(
            stream,
            Encoding.UTF8,
            Encoding.UTF8,
            leaveOpen: true);
        return await JsonDocument.ParseAsync(normalizedUtf8, cancellationToken: timeout.Token);
    }

    private static bool RequiresTextTranscoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return false;
        }

        try
        {
            return Encoding.GetEncoding(charset).CodePage != Encoding.UTF8.CodePage;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed record Pagination(
        int TotalCount,
        int PageSize,
        int CurrentPage,
        int TotalPages);
}
