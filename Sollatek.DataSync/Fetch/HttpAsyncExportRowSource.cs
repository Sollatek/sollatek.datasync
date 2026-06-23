#nullable enable

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Parquet;
using Parquet.Schema;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Fetch;

public sealed class HttpAsyncExportRowSource : IAsyncExportRowSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly AsyncExportOptions _options;
    private readonly IAsyncExportStateStore _stateStore;
    private readonly ILogger<HttpAsyncExportRowSource> _logger;
    private readonly ISyncMonitor? _monitor;
    private readonly ConcurrentDictionary<string, string> _stateStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _submitRateLimitLock = new(1, 1);
    private readonly Queue<DateTimeOffset> _submitTimestamps = new();

    public HttpAsyncExportRowSource(
        HttpClient httpClient,
        AsyncExportOptions options,
        ILogger<HttpAsyncExportRowSource> logger,
        ISyncMonitor? monitor = null)
        : this(
            httpClient,
            options,
            logger,
            new FilesystemAsyncExportStateStore(options),
            monitor)
    {
    }

    [ActivatorUtilitiesConstructor]
    public HttpAsyncExportRowSource(
        HttpClient httpClient,
        AsyncExportOptions options,
        ILogger<HttpAsyncExportRowSource> logger,
        IAsyncExportStateStore stateStore,
        ISyncMonitor? monitor = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _monitor = monitor;
    }

    public async Task<IReadOnlyList<AsyncExportActiveRequestState>> GetActiveRequestsAsync(
        CancellationToken cancellationToken)
    {
        EnsureDownloadDirectory();

        var activeStates = new List<AsyncExportActiveRequestState>();
        await foreach (var stateKey in _stateStore.ListKeysAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ReadStateAsync(stateKey, cancellationToken);
            if (state is null)
            {
                continue;
            }

            TrackStateStatus(state);
            if (IsTerminalState(state.Status))
            {
                continue;
            }

            activeStates.Add(new AsyncExportActiveRequestState(
                state.EntityKey,
                state.RangeStart,
                state.RangeEnd,
                state.Status));
        }

        return activeStates;
    }

    public async Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var files = new List<AsyncExportDownloadedFile>();
        await foreach (var file in PrepareOrderedAsync(requests, cancellationToken))
        {
            files.Add(file);
        }

        return files;
    }

    public async IAsyncEnumerable<AsyncExportDownloadedFile> PrepareUnorderedAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in PrepareAsCompletedAsync(requests, cancellationToken))
        {
            yield return file;
        }
    }

    public async IAsyncEnumerable<AsyncExportDownloadedFile> PrepareOrderedAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var orderedRequests = requests.OrderBy(x => x.Sequence).ToArray();
        var prepared = new Dictionary<int, AsyncExportDownloadedFile>();
        var nextYieldIndex = 0;

        await foreach (var completedFile in PrepareAsCompletedAsync(orderedRequests, cancellationToken))
        {
            prepared[completedFile.Request.Sequence] = completedFile;
            while (nextYieldIndex < orderedRequests.Length &&
                   prepared.Remove(orderedRequests[nextYieldIndex].Sequence, out var file))
            {
                nextYieldIndex++;
                yield return file;
            }
        }

        if (nextYieldIndex < orderedRequests.Length)
        {
            throw new InvalidOperationException(
                "Async export preparation stopped before all requested files were downloaded.");
        }
    }

    private async IAsyncEnumerable<AsyncExportDownloadedFile> PrepareAsCompletedAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        EnsureDownloadDirectory();

        var orderedRequests = requests.OrderBy(x => x.Sequence).ToArray();
        if (orderedRequests.Length == 0)
        {
            yield break;
        }

        var existingStatesByRequestKey = await ResolveExistingActiveStatesAsync(
            orderedRequests,
            cancellationToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new List<Task<AsyncExportDownloadedFile>>();
        var nextRequestIndex = 0;
        var maxParallelRequests = Math.Max(1, _options.MaxParallelRequests);

        void StartPendingRequests()
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            while (nextRequestIndex < orderedRequests.Length &&
                   active.Count < maxParallelRequests)
            {
                var request = orderedRequests[nextRequestIndex];
                existingStatesByRequestKey.TryGetValue(GetRequestKey(request), out var existingState);
                active.Add(PrepareRequestCoreAsync(
                    request,
                    existingState,
                    linkedCancellation.Token));
                nextRequestIndex++;
            }
        }

        try
        {
            StartPendingRequests();
            while (nextRequestIndex < orderedRequests.Length || active.Count > 0)
            {
                if (active.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Async export preparation stopped before all requested files were downloaded.");
                }

                var completedTask = await Task.WhenAny(active);
                active.Remove(completedTask);

                var completedFile = await completedTask;
                StartPendingRequests();
                yield return completedFile;
            }
        }
        finally
        {
            linkedCancellation.Cancel();
            try
            {
                await Task.WhenAll(active);
            }
            catch
            {
                // The primary preparation failure is already propagated by the iterator body.
            }
        }
    }

    public async IAsyncEnumerable<JsonElement> ReadRowsAsync(
        AsyncExportDownloadedFile file,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var state = await LoadOrCreateStateAsync(file.Request, file.StateKey, cancellationToken);
        state = state with
        {
            Status = AsyncExportLocalStatus.Processing,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await SaveStateAsync(state, cancellationToken);

        await foreach (var row in ReadDownloadedRowsAsync(file, cancellationToken))
        {
            yield return row;
        }
    }

    public async Task CompleteAsync(
        AsyncExportDownloadedFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (File.Exists(file.Path))
        {
            File.Delete(file.Path);
        }

        await _stateStore.DeleteAsync(file.StateKey ?? GetRequestKey(file.Request), cancellationToken);

        _stateStatuses.TryRemove(file.StateKey ?? GetRequestKey(file.Request), out _);
        RecordAsyncExportStatus();
    }

    private async Task<AsyncExportDownloadedFile> PrepareRequestCoreAsync(
        AsyncExportRequest request,
        AsyncExportLocalState? existingState,
        CancellationToken cancellationToken)
    {
        var state = existingState;
        while (true)
        {
            state ??= await LoadOrCreateStateAsync(request, stateKey: null, cancellationToken);
            TrackStateStatus(state);
            if (IsDownloaded(state))
            {
                return new AsyncExportDownloadedFile(request, state.FilePath!, state.Key);
            }

            if (state.ExportId is null ||
                state.Status is AsyncExportLocalStatus.Failed or AsyncExportLocalStatus.Expired)
            {
                state = await SubmitAsync(request, state, cancellationToken);
            }

            var pollResult = await PollUntilReadyAsync(request, state, cancellationToken);
            if (pollResult.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                state = state with
                    {
                        Status = AsyncExportLocalStatus.Failed,
                        Error = pollResult.Error,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                await SaveStateAsync(state, cancellationToken);
                continue;
            }

            if (pollResult.Status.Equals("expired", StringComparison.OrdinalIgnoreCase))
            {
                state = state with
                    {
                        Status = AsyncExportLocalStatus.Expired,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                await SaveStateAsync(state, cancellationToken);
                continue;
            }

            if (pollResult.ExpiresAtUtc.HasValue &&
                pollResult.ExpiresAtUtc.Value <= DateTimeOffset.UtcNow)
            {
                state = state with
                    {
                        Status = AsyncExportLocalStatus.Expired,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                await SaveStateAsync(state, cancellationToken);
                continue;
            }

            try
            {
                return await DownloadAsync(request, state, pollResult, cancellationToken);
            }
            catch (AsyncExportExpiredException)
            {
                state = state with
                    {
                        Status = AsyncExportLocalStatus.Expired,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                await SaveStateAsync(state, cancellationToken);
            }
        }
    }

    private async Task<AsyncExportLocalState> SubmitAsync(
        AsyncExportRequest request,
        AsyncExportLocalState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForSubmitSlotAsync(cancellationToken);

            var pathAndQuery = AsyncExportRequestBuilder.BuildCreateRequest(
                request.RequestMetadata,
                request.Range,
                _options.Format);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseBody = response.Content == null
                ? string.Empty
                : await ReadAsStringWithTimeoutAsync(response.Content, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = GetRateLimitDelay(response);
                _logger.LogWarning(
                    "Async export submit for {EntityKey} request {Sequence} was rate limited. Retrying in {Delay}.",
                    request.Job.Metadata.Key,
                    request.Sequence,
                    delay);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                throw new InvalidOperationException(
                    $"Async export request for sync entity '{request.Job.Metadata.Key}' returned HTTP {(int)response.StatusCode}: {responseBody}");
            }

            var createResult = JsonSerializer.Deserialize<AsyncExportCreateResult>(responseBody, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Async export request for sync entity '{request.Job.Metadata.Key}' returned an empty create result.");
            var nextState = state with
            {
                ExportId = createResult.ExportId,
                Status = AsyncExportLocalStatus.Polling,
                Attempts = state.Attempts + 1,
                Error = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveStateAsync(nextState, cancellationToken);

            _logger.LogInformation(
                "Submitted async export {ExportId} for {EntityKey} request {Sequence}.",
                createResult.ExportId,
                request.Job.Metadata.Key,
                request.Sequence);

            return nextState;
        }
    }

    private async Task WaitForSubmitSlotAsync(CancellationToken cancellationToken)
    {
        var maxSubmissions = Math.Max(1, _options.MaxSubmissions);
        var submissionWindow = _options.SubmissionWindow > TimeSpan.Zero
            ? _options.SubmissionWindow
            : TimeSpan.FromHours(1);
        while (true)
        {
            TimeSpan? delay = null;
            await _submitRateLimitLock.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;
                while (_submitTimestamps.Count > 0 &&
                       now - _submitTimestamps.Peek() >= submissionWindow)
                {
                    _submitTimestamps.Dequeue();
                }

                if (_submitTimestamps.Count < maxSubmissions)
                {
                    _submitTimestamps.Enqueue(now);
                    return;
                }

                delay = submissionWindow - (now - _submitTimestamps.Peek());
            }
            finally
            {
                _submitRateLimitLock.Release();
            }

            if (delay.GetValueOrDefault() > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Async export submit rate limit reached ({MaxSubmissions}/{SubmissionWindow}). Waiting {Delay}.",
                    maxSubmissions,
                    submissionWindow,
                    delay.Value);
                await Task.Delay(delay.Value, cancellationToken);
            }
        }
    }

    private TimeSpan GetRateLimitDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        return _options.RateLimitRetryDelay;
    }

    private async Task<AsyncExportPollResult> PollUntilReadyAsync(
        AsyncExportRequest request,
        AsyncExportLocalState state,
        CancellationToken cancellationToken)
    {
        if (state.ExportId is null)
        {
            throw new InvalidOperationException(
                $"Async export state for sync entity '{request.Job.Metadata.Key}' has no export id.");
        }

        while (true)
        {
            var pollResult = await PollAsync(request, state.ExportId.Value, cancellationToken);
            if (pollResult.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
                pollResult.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                pollResult.Status.Equals("expired", StringComparison.OrdinalIgnoreCase))
            {
                return pollResult;
            }

            await SaveStateAsync(
                state with
                {
                    Status = AsyncExportLocalStatus.Polling,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
            await Task.Delay(_options.PollInterval, cancellationToken);
        }
    }

    private async Task<AsyncExportPollResult> PollAsync(
        AsyncExportRequest request,
        Guid exportId,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"api/exports/{exportId}");
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
                $"Polling async export {exportId} for sync entity '{request.Job.Metadata.Key}' returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return JsonSerializer.Deserialize<AsyncExportPollResult>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Polling async export {exportId} for sync entity '{request.Job.Metadata.Key}' returned an empty status result.");
    }

    private async Task<AsyncExportDownloadedFile> DownloadAsync(
        AsyncExportRequest request,
        AsyncExportLocalState state,
        AsyncExportPollResult pollResult,
        CancellationToken cancellationToken)
    {
        if (state.ExportId is null)
        {
            throw new InvalidOperationException(
                $"Async export state for sync entity '{request.Job.Metadata.Key}' has no export id.");
        }

        if (pollResult.DownloadId is null)
        {
            throw new InvalidOperationException(
                $"Async export {state.ExportId} for sync entity '{request.Job.Metadata.Key}' succeeded without a download id.");
        }

        var filePath = GetDownloadPath(state.Key);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/exports/{state.ExportId.Value}/download/{pollResult.DownloadId.Value}");
        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(GetAcceptHeader()));
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new AsyncExportExpiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = response.Content == null
                ? string.Empty
                : await ReadAsStringWithTimeoutAsync(response.Content, cancellationToken);
            throw new InvalidOperationException(
                $"Downloading async export {state.ExportId} for sync entity '{request.Job.Metadata.Key}' returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(filePath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        var downloadedState = state with
        {
            Status = AsyncExportLocalStatus.Downloaded,
            DownloadId = pollResult.DownloadId,
            FilePath = filePath,
            ExpiresAtUtc = pollResult.ExpiresAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await SaveStateAsync(downloadedState, cancellationToken);

        return new AsyncExportDownloadedFile(request, filePath, state.Key);
    }

    private async Task<AsyncExportLocalState> LoadOrCreateStateAsync(
        AsyncExportRequest request,
        string? stateKey,
        CancellationToken cancellationToken)
    {
        var key = stateKey ?? GetRequestKey(request);
        var state = await ReadStateAsync(key, cancellationToken);
        if (state is not null)
        {
            TrackStateStatus(state);
            return state;
        }

        var newState = new AsyncExportLocalState(
            Key: GetRequestKey(request),
            EntityKey: request.Job.Metadata.Key,
            Sequence: request.Sequence,
            RangeStart: request.Range.Start,
            RangeEnd: request.Range.End,
            Status: AsyncExportLocalStatus.Pending,
            ExportId: null,
            DownloadId: null,
            FilePath: null,
            ExpiresAtUtc: null,
            Attempts: 0,
            Error: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        TrackStateStatus(newState);
        return newState;
    }

    private async Task<IReadOnlyDictionary<string, AsyncExportLocalState>> ResolveExistingActiveStatesAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        CancellationToken cancellationToken)
    {
        var requestsByKey = requests.ToDictionary(GetRequestKey, StringComparer.OrdinalIgnoreCase);
        var requestsByEntity = requests
            .GroupBy(x => x.Job.Metadata.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var statesByRequestKey = new Dictionary<string, AsyncExportLocalState>(StringComparer.OrdinalIgnoreCase);

        await foreach (var stateKey in _stateStore.ListKeysAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ReadStateAsync(stateKey, cancellationToken);
            if (state is null)
            {
                continue;
            }

            TrackStateStatus(state);
            if (IsTerminalState(state.Status))
            {
                continue;
            }

            var requestKey = state.Key;
            if (!requestsByKey.ContainsKey(requestKey))
            {
                if (!requestsByEntity.TryGetValue(state.EntityKey, out var candidates))
                {
                    throw PreviousUnfinishedStateException(state);
                }

                var matchingSequence = candidates
                    .Where(x => x.Sequence == state.Sequence)
                    .ToArray();
                var request = matchingSequence.Length == 1
                    ? matchingSequence[0]
                    : candidates.Length == 1
                        ? candidates[0]
                        : null;

                if (request == null)
                {
                    throw PreviousUnfinishedStateException(state);
                }

                requestKey = GetRequestKey(request);
            }

            if (!statesByRequestKey.TryAdd(requestKey, state))
            {
                throw new InvalidOperationException(
                    $"Async export has multiple unfinished request states for sync entity '{state.EntityKey}'. Wait for them to finish or fail before starting new export requests.");
            }
        }

        return statesByRequestKey;
    }

    private async Task SaveStateAsync(
        AsyncExportLocalState state,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        stream.Position = 0;
        await _stateStore.SaveAsync(state.Key, stream, cancellationToken);
        TrackStateStatus(state);
    }

    private void TrackStateStatus(AsyncExportLocalState state)
    {
        _stateStatuses[state.Key] = state.Status;
        RecordAsyncExportStatus();
    }

    private void RecordAsyncExportStatus()
    {
        if (_monitor == null)
        {
            return;
        }

        var statuses = _stateStatuses.Values.ToArray();
        _monitor.RecordAsyncExportStatus(new AsyncExportStatusSummary(
            Pending: CountStatus(statuses, AsyncExportLocalStatus.Pending),
            Polling: CountStatus(statuses, AsyncExportLocalStatus.Polling),
            Downloaded: CountStatus(statuses, AsyncExportLocalStatus.Downloaded),
            Processing: CountStatus(statuses, AsyncExportLocalStatus.Processing),
            Failed: CountStatus(statuses, AsyncExportLocalStatus.Failed),
            Expired: CountStatus(statuses, AsyncExportLocalStatus.Expired)));
    }

    private static int CountStatus(
        IReadOnlyList<string> statuses,
        string expectedStatus)
    {
        return statuses.Count(status => string.Equals(
            status,
            expectedStatus,
            StringComparison.OrdinalIgnoreCase));
    }

    private bool IsDownloaded(AsyncExportLocalState state)
    {
        return state is { Status: AsyncExportLocalStatus.Downloaded or AsyncExportLocalStatus.Processing, FilePath: not null } &&
            File.Exists(state.FilePath);
    }

    private static bool IsTerminalState(string status)
    {
        return string.Equals(status, AsyncExportLocalStatus.Failed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, AsyncExportLocalStatus.Expired, StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException PreviousUnfinishedStateException(AsyncExportLocalState state)
    {
        return new InvalidOperationException(
            $"Async export has previous unfinished request state '{state.Key}' for sync entity '{state.EntityKey}' with status '{state.Status}'. Wait for it to finish or fail before starting new export requests.");
    }

    private async Task<AsyncExportLocalState?> ReadStateAsync(
        string stateKey,
        CancellationToken cancellationToken)
    {
        await using var stream = await _stateStore.OpenReadAsync(stateKey, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        try
        {
            var state = await JsonSerializer.DeserializeAsync<AsyncExportLocalState>(
                stream,
                JsonOptions,
                cancellationToken);
            if (state != null)
            {
                return state;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Async export state '{stateKey}' is not readable.",
                exception);
        }

        throw new InvalidOperationException(
            $"Async export state '{stateKey}' is empty.");
    }

    private void EnsureDownloadDirectory()
    {
        Directory.CreateDirectory(GetDownloadDirectory());
    }

    private string GetDownloadDirectory()
    {
        return Path.Combine(_options.StatePath, "downloads");
    }

    private string GetRequestKey(AsyncExportRequest request)
    {
        var text = string.Join(
            "|",
            request.Job.Metadata.Key,
            request.Sequence.ToString(CultureInfo.InvariantCulture),
            request.RequestMetadata.Operations.FirstOrDefault()?.Path ?? "",
            request.Range.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            request.Range.End.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            request.Range.IncludeEndFilter.ToString(CultureInfo.InvariantCulture),
            request.Job.DataMode.ToString(),
            GetExportFormat(),
            request.Job.IsInitial.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private IAsyncEnumerable<JsonElement> ReadDownloadedRowsAsync(
        AsyncExportDownloadedFile file,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.Path);
        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return AsyncExportCsvRowReader.ReadRowsAsync(
                file.Path,
                file.Request.RequestMetadata,
                cancellationToken);
        }

        if (string.Equals(extension, ".parquet", StringComparison.OrdinalIgnoreCase))
        {
            return AsyncExportParquetRowReader.ReadRowsAsync(
                file.Path,
                file.Request.RequestMetadata,
                cancellationToken);
        }

        throw new InvalidOperationException(
            $"Async export format '{GetExportFormat()}' cannot be read as rows. Supported row formats are Parquet and Csv.");
    }

    private string GetDownloadPath(string stateKey)
    {
        return Path.Combine(GetDownloadDirectory(), $"{stateKey}.{GetFileExtension()}");
    }

    private string GetFileExtension()
    {
        return ExportFormatNames.ToFileExtension(GetExportFormat());
    }

    private string GetAcceptHeader()
    {
        return GetExportFormat() switch
        {
            ExportFormatNames.Csv => "text/csv",
            ExportFormatNames.Xml => "application/xml",
            ExportFormatNames.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/vnd.apache.parquet"
        };
    }

    private string GetExportFormat()
    {
        return ExportFormatNames.NormalizePortalFormat(
            _options.Format,
            "AsyncExport:format",
            ExportFormatNames.Parquet);
    }

    private sealed record AsyncExportCreateResult(Guid ExportId, DateTimeOffset CreatedAtUtc);

    private sealed record AsyncExportPollResult(
        Guid ExportId,
        string Status,
        string? Error,
        string? DownloadUrl,
        Guid? DownloadId,
        DateTimeOffset? ExpiresAtUtc);

    private sealed record AsyncExportLocalState(
        string Key,
        string EntityKey,
        int Sequence,
        DateTimeOffset RangeStart,
        DateTimeOffset RangeEnd,
        string Status,
        Guid? ExportId,
        Guid? DownloadId,
        string? FilePath,
        DateTimeOffset? ExpiresAtUtc,
        int Attempts,
        string? Error,
        DateTimeOffset UpdatedAtUtc);

    private static class AsyncExportLocalStatus
    {
        public const string Pending = "pending";
        public const string Polling = "polling";
        public const string Downloaded = "downloaded";
        public const string Processing = "processing";
        public const string Failed = "failed";
        public const string Expired = "expired";
    }

    private sealed class AsyncExportExpiredException : Exception
    {
    }
}

internal static class AsyncExportRequestBuilder
{
    public static string BuildCreateRequest(
        SwaggerSyncEntityMetadata metadata,
        SyncDateRange range,
        string exportFormat)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportFormat);

        var operation = metadata.Operations.FirstOrDefault(x =>
            string.Equals(x.Method, "get", StringComparison.OrdinalIgnoreCase));
        if (operation == null)
        {
            throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' has no GET operation metadata for async export.");
        }

        if (operation.Path.Contains('{', StringComparison.Ordinal) ||
            operation.Path.Contains('}', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' async export operation '{operation.OperationId}' requires route values and cannot be called from SyncPlan alone.");
        }

        var queryParameters = new List<(string Name, string Value)>
        {
            ("$export", exportFormat),
            ("$exportAsync", "true"),
            ("$top", "-1")
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

        return AppendQuery(operation.Path, queryParameters);
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
            queryParameters.Select(x => $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value)}"));

        return $"{path}{separator}{query}";
    }
}

internal static class AsyncExportParquetRowReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async IAsyncEnumerable<JsonElement> ReadRowsAsync(
        string path,
        SwaggerSyncEntityMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var reader = await ParquetReader.CreateAsync(
            path,
            cancellationToken: cancellationToken);
        var fields = reader.Schema.GetDataFields().ToArray();
        var pathMap = BuildPathMap(metadata);

        for (var groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using var rowGroup = reader.OpenRowGroupReader(groupIndex);
            var rowCount = checked((int)rowGroup.RowCount);
            if (rowCount == 0)
            {
                continue;
            }

            var columns = new List<ParquetColumnValues>(fields.Length);
            foreach (var field in fields)
            {
                columns.Add(new ParquetColumnValues(
                    field.Name,
                    await ReadColumnAsync(rowGroup, field, rowCount, cancellationToken)));
            }

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in columns)
                {
                    var value = column.Values[rowIndex];
                    var pathSegments = ResolvePath(column.Name, pathMap);
                    SetValue(root, pathSegments, NormalizeValue(value));
                }

                using var document = JsonDocument.Parse(JsonSerializer.Serialize(root, JsonOptions));
                yield return document.RootElement.Clone();
            }
        }
    }

    private static async Task<object?[]> ReadColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        if (field.ClrType == typeof(string))
        {
            var values = new string?[rowCount];
            await rowGroup.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken);
            return values.Cast<object?>().ToArray();
        }

        if (field.ClrType == typeof(byte[]))
        {
            var values = new byte[]?[rowCount];
            await rowGroup.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken);
            return values
                .Select(value => value == null ? null : Convert.ToBase64String(value))
                .Cast<object?>()
                .ToArray();
        }

        if (!field.ClrType.IsValueType)
        {
            throw new InvalidOperationException(
                $"Async export Parquet column '{field.Name}' uses unsupported CLR type '{field.ClrType}'.");
        }

        return await ReadNullableValueColumnAsync(
            rowGroup,
            field,
            rowCount,
            cancellationToken);
    }

    private static Task<object?[]> ReadNullableValueColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var method = typeof(AsyncExportParquetRowReader)
            .GetMethod(
                nameof(ReadNullableValueColumnCoreAsync),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(field.ClrType);
        return (Task<object?[]>)method.Invoke(
            null,
            [rowGroup, field, rowCount, cancellationToken])!;
    }

    private static async Task<object?[]> ReadNullableValueColumnCoreAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
        where T : struct
    {
        var values = new T?[rowCount];
        await rowGroup.ReadAsync<T>(field, values.AsMemory(), cancellationToken: cancellationToken);
        return values.Cast<object?>().ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> BuildPathMap(SwaggerSyncEntityMetadata metadata)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in metadata.PrimaryKey
                     .Concat(metadata.References.Select(x => x.Source))
                     .Concat(metadata.Watermark == null ? [] : [metadata.Watermark.Field])
                     .Concat(metadata.ScalarFields.Select(x => x.Source)))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            map[path.Replace(".", "_", StringComparison.Ordinal)] =
                path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return map;
    }

    private static string[] ResolvePath(
        string header,
        IReadOnlyDictionary<string, string[]> pathMap)
    {
        if (pathMap.TryGetValue(header, out var mapped))
        {
            return mapped;
        }

        return header.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void SetValue(
        IDictionary<string, object?> root,
        IReadOnlyList<string> path,
        object? value)
    {
        if (path.Count == 0)
        {
            return;
        }

        var current = root;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var segment = path[index];
            if (!current.TryGetValue(segment, out var next) ||
                next is not IDictionary<string, object?> nextObject)
            {
                nextObject = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[segment] = nextObject;
            }

            current = nextObject;
        }

        current[path[^1]] = value;
    }

    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                : dateTime.ToUniversalTime(),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            _ => value
        };
    }

    private sealed record ParquetColumnValues(string Name, object?[] Values);
}

internal static class AsyncExportCsvRowReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async IAsyncEnumerable<JsonElement> ReadRowsAsync(
        string path,
        SwaggerSyncEntityMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

        var headers = await ReadRecordAsync(reader, cancellationToken);
        if (headers == null || headers.Count == 0)
        {
            yield break;
        }

        var pathMap = BuildPathMap(metadata);
        while (await ReadRecordAsync(reader, cancellationToken) is { } row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Count && index < row.Count; index++)
            {
                var header = headers[index];
                if (string.IsNullOrWhiteSpace(header))
                {
                    continue;
                }

                var value = ConvertValue(row[index]);
                var pathSegments = ResolvePath(header, pathMap);
                SetValue(root, pathSegments, value);
            }

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(root, JsonOptions));
            yield return document.RootElement.Clone();
        }
    }

    private static IReadOnlyDictionary<string, string[]> BuildPathMap(SwaggerSyncEntityMetadata metadata)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in metadata.PrimaryKey
                     .Concat(metadata.References.Select(x => x.Source))
                     .Concat(metadata.Watermark == null ? [] : [metadata.Watermark.Field])
                     .Concat(metadata.ScalarFields.Select(x => x.Source)))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            map[path.Replace(".", "_", StringComparison.Ordinal)] =
                path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return map;
    }

    private static string[] ResolvePath(
        string header,
        IReadOnlyDictionary<string, string[]> pathMap)
    {
        if (pathMap.TryGetValue(header, out var mapped))
        {
            return mapped;
        }

        return header.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void SetValue(
        IDictionary<string, object?> root,
        IReadOnlyList<string> path,
        object? value)
    {
        if (path.Count == 0)
        {
            return;
        }

        var current = root;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var segment = path[index];
            if (!current.TryGetValue(segment, out var next) ||
                next is not IDictionary<string, object?> nextObject)
            {
                nextObject = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[segment] = nextObject;
            }

            current = nextObject;
        }

        current[path[^1]] = value;
    }

    private static object? ConvertValue(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue;
        }

        if (Guid.TryParse(value, out _))
        {
            return value;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return longValue;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue;
        }

        return value;
    }

    private static async Task<IReadOnlyList<string>?> ReadRecordAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var buffer = new char[1];
        var inQuotes = false;
        var sawAny = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (count == 0)
            {
                if (inQuotes)
                {
                    throw new InvalidOperationException("CSV export ended inside a quoted field.");
                }

                if (!sawAny)
                {
                    return null;
                }

                fields.Add(field.ToString());
                return fields;
            }

            sawAny = true;
            var ch = buffer[0];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    var next = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                    if (next == 0)
                    {
                        fields.Add(field.ToString());
                        return fields;
                    }

                    if (buffer[0] == '"')
                    {
                        field.Append('"');
                        continue;
                    }

                    inQuotes = false;
                    ch = buffer[0];
                }
                else
                {
                    field.Append(ch);
                    continue;
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
                continue;
            }

            if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (ch == '\r')
            {
                var next = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                if (next != 0 && buffer[0] != '\n')
                {
                    field.Append(buffer[0]);
                    continue;
                }

                fields.Add(field.ToString());
                return fields;
            }

            if (ch == '\n')
            {
                fields.Add(field.ToString());
                return fields;
            }

            field.Append(ch);
        }
    }
}
