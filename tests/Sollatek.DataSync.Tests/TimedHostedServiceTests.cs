using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Notifications;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class TimedHostedServiceTests
{
    [Fact]
    public async Task StopWhenFinished_StopsApplicationAfterSuccessfulRun()
    {
        using var httpClient = new HttpClient(new SwaggerResponseHandler());
        var lifetime = new RecordingApplicationLifetime();
        var runner = new RecordingSyncJobRunner();
        var stateStore = new RecordingSyncStateStore();
        using var service = CreateService(
            httpClient,
            runner,
            stateStore,
            lifetime,
            new SyncOptions
            {
                StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                RunOnStartup = true,
                StopWhenFinished = true,
                RunInterval = TimeSpan.FromHours(6)
            });

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Single(runner.Runs);
        Assert.Contains(stateStore.Saves, save => save.EntityKey == "assets");
    }

    [Fact]
    public async Task StopWhenFinished_StopsApplicationAfterRetryExhaustion()
    {
        using var httpClient = new HttpClient(new SwaggerResponseHandler());
        var lifetime = new RecordingApplicationLifetime();
        var runner = new RecordingSyncJobRunner
        {
            Failure = new InvalidOperationException("sync failed")
        };
        using var service = CreateService(
            httpClient,
            runner,
            new RecordingSyncStateStore(),
            lifetime,
            new SyncOptions
            {
                StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                RunOnStartup = true,
                StopWhenFinished = true,
                RunInterval = TimeSpan.FromHours(6)
            },
            new RetryOptions { MaxTries = 1 });

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task RetryExhaustion_SendsFailureEmailNotification()
    {
        using var httpClient = new HttpClient(new SwaggerResponseHandler());
        var lifetime = new RecordingApplicationLifetime();
        var runner = new RecordingSyncJobRunner
        {
            Failure = new InvalidOperationException("sync failed")
        };
        var notifier = new RecordingFailureNotificationSender();
        using var service = CreateService(
            httpClient,
            runner,
            new RecordingSyncStateStore(),
            lifetime,
            new SyncOptions
            {
                StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                RunOnStartup = true,
                StopWhenFinished = true,
                RunInterval = TimeSpan.FromHours(6)
            },
            new RetryOptions { MaxTries = 1 },
            failureNotificationSender: notifier);

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var message = Assert.Single(notifier.Messages);
        Assert.Equal("sync failed", message.Error);
        Assert.Equal(1, message.TryNumber);
        Assert.Equal(1, message.MaxTries);
        Assert.Null(message.EntityKey);
    }

    [Fact]
    public async Task RunAsync_ResumesOnlyActiveAsyncExportState()
    {
        using var httpClient = new HttpClient(new SwaggerResponseHandler());
        var lifetime = new RecordingApplicationLifetime();
        var runner = new RecordingSyncJobRunner();
        var stateStore = new RecordingSyncStateStore();
        var activeStart = new DateTimeOffset(2025, 12, 22, 0, 0, 0, TimeSpan.Zero);
        var activeEnd = new DateTimeOffset(2026, 6, 22, 13, 47, 36, TimeSpan.Zero);
        using var service = CreateService(
            httpClient,
            runner,
            stateStore,
            lifetime,
            new SyncOptions
            {
                StartFrom = activeStart,
                RunOnStartup = true,
                StopWhenFinished = true,
                RunInterval = TimeSpan.FromHours(6),
                TransferMode = SyncTransferMode.AsyncExport
            },
            asyncExportRowSource: new FixedActiveAsyncExportRowSource(
            [
                new AsyncExportActiveRequestState(
                    "rawDataTemperaturedata",
                    activeStart,
                    activeEnd,
                    "polling")
            ]));

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var job = Assert.Single(Assert.Single(runner.Runs));
        Assert.Equal("rawDataTemperaturedata", job.Metadata.Key);
        Assert.Equal(activeStart, job.Range.Start);
        Assert.Equal(activeEnd, job.Range.End);
        var save = Assert.Single(stateStore.Saves);
        Assert.Equal("rawDataTemperaturedata", save.EntityKey);
        Assert.Equal(activeEnd, save.End);
    }

    [Fact]
    public async Task RunAsync_AllowsMultipleActiveAsyncExportStatesForSameEntity()
    {
        using var httpClient = new HttpClient(new SwaggerResponseHandler());
        var lifetime = new RecordingApplicationLifetime();
        var runner = new RecordingSyncJobRunner();
        var stateStore = new RecordingSyncStateStore();
        var rangeStart = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero);
        var syncOptions = new SyncOptions
        {
            StartFrom = rangeStart,
            StartupMode = SyncStartupMode.HistoricalOnly,
            StopWhenFinished = true,
            RunInterval = TimeSpan.FromHours(6),
            TransferMode = SyncTransferMode.AsyncExport,
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Daily,
                Time = new TimeOnly(1, 0)
            }
        };
        var rangeEnd = SyncSchedulePlanner.GetStartupPlan(syncOptions, DateTimeOffset.UtcNow).RangeEnd;
        using var service = CreateService(
            httpClient,
            runner,
            stateStore,
            lifetime,
            syncOptions,
            asyncExportRowSource: new FixedActiveAsyncExportRowSource(
            [
                new AsyncExportActiveRequestState(
                    "assets",
                    rangeStart,
                    rangeStart.AddDays(1),
                    "polling"),
                new AsyncExportActiveRequestState(
                    "assets",
                    rangeStart.AddDays(1),
                    rangeStart.AddDays(2),
                    "polling")
            ]));

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var job = Assert.Single(Assert.Single(runner.Runs));
        Assert.Equal("assets", job.Metadata.Key);
        Assert.Equal(rangeStart, job.Range.Start);
        Assert.Equal(rangeEnd, job.Range.End);
        var save = Assert.Single(stateStore.Saves);
        Assert.Equal("assets", save.EntityKey);
        Assert.Equal(rangeEnd, save.End);
    }

    private static TimedHostedService CreateService(
        HttpClient httpClient,
        ISyncJobRunner runner,
        ISyncStateStore stateStore,
        IHostApplicationLifetime lifetime,
        SyncOptions syncOptions,
        RetryOptions? retryOptions = null,
        IAsyncExportRowSource? asyncExportRowSource = null,
        IFailureNotificationSender? failureNotificationSender = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "data-v1",
                ["SwaggerDocuments:0:url"] = "https://api.sollatek.io/swagger/data-v1/swagger.json",
                ["SyncPlan:0"] = "assets",
                ["SyncPlan:1"] = "rawDataTemperaturedata"
            })
            .Build();

        return new TimedHostedService(
            NullLogger<TimedHostedService>.Instance,
            configuration,
            new FixedHttpClientFactory(httpClient),
            syncOptions,
            SyncPlanOptions.FromConfiguration(configuration),
            retryOptions ?? RetryOptions.Default,
            new RecordingSyncMonitor(),
            runner,
            stateStore,
            new AlwaysHasDataSyncTargetDataStore(),
            lifetime,
            asyncExportRowSource ?? NoopAsyncExportRowSource.Instance,
            failureNotificationSender ?? NoopFailureNotificationSender.Instance);
    }

    private sealed class FixedHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return httpClient;
        }
    }

    private sealed class SwaggerResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Swagger)
            });
        }
    }

    private sealed class RecordingSyncJobRunner : ISyncJobRunner
    {
        public List<IReadOnlyList<SyncJob>> Runs { get; } = [];

        public Exception? Failure { get; init; }

        public Task RunAsync(
            string runId,
            IReadOnlyList<SyncJob> jobs,
            CancellationToken cancellationToken)
        {
            Runs.Add(jobs);
            return Failure == null
                ? Task.CompletedTask
                : Task.FromException(Failure);
        }
    }

    private sealed class FixedActiveAsyncExportRowSource(
        IReadOnlyList<AsyncExportActiveRequestState> activeStates) : IAsyncExportRowSource
    {
        public Task<IReadOnlyList<AsyncExportActiveRequestState>> GetActiveRequestsAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(activeStates);
        }

        public Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
            IReadOnlyList<AsyncExportRequest> requests,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<JsonElement> ReadRowsAsync(
            AsyncExportDownloadedFile file,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
#pragma warning disable CS0162
            await Task.CompletedTask;
            yield break;
#pragma warning restore CS0162
        }

        public Task CompleteAsync(
            AsyncExportDownloadedFile file,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NoopAsyncExportRowSource : IAsyncExportRowSource
    {
        public static NoopAsyncExportRowSource Instance { get; } = new();

        private NoopAsyncExportRowSource()
        {
        }

        public Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
            IReadOnlyList<AsyncExportRequest> requests,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<JsonElement> ReadRowsAsync(
            AsyncExportDownloadedFile file,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
#pragma warning disable CS0162
            await Task.CompletedTask;
            yield break;
#pragma warning restore CS0162
        }

        public Task CompleteAsync(
            AsyncExportDownloadedFile file,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingSyncStateStore : ISyncStateStore
    {
        public List<(string EntityKey, DateTimeOffset End)> Saves { get; } = [];

        public Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
            string entityKey,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }

        public Task SaveSuccessfulEndAsync(
            string entityKey,
            DateTimeOffset end,
            CancellationToken cancellationToken)
        {
            Saves.Add((entityKey, end));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFailureNotificationSender : IFailureNotificationSender
    {
        public List<FailureNotificationMessage> Messages { get; } = [];

        public Task SendAsync(
            FailureNotificationMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysHasDataSyncTargetDataStore : ISyncTargetDataStore
    {
        public Task<bool> HasStoredDataAsync(
            SwaggerSyncEntityMetadata metadata,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
            SwaggerSyncEntityMetadata metadata,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }
    }

    private sealed class RecordingSyncMonitor : ISyncMonitor
    {
        public SyncRunStatus Current { get; private set; } = SyncRunStatus.Idle;

        public void RecordRunStarted(string runId, DateTimeOffset? startedAt = null)
        {
            Current = new SyncRunStatus
            {
                State = SyncRunState.Running,
                RunId = runId,
                StartedAt = startedAt ?? DateTimeOffset.UtcNow
            };
        }

        public void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null)
        {
            Current = Current with
            {
                State = SyncRunState.ProcessingEntity,
                RunId = runId,
                CurrentEntity = entityKey,
                StartedAt = startedAt ?? Current.StartedAt
            };
        }

        public void RecordProgress(
            string runId,
            string entityKey,
            long recordsProcessed = 0,
            long pagesProcessed = 0,
            long filesProcessed = 0)
        {
        }

        public void RecordFailure(
            string runId,
            string? entityKey,
            string error,
            int tryNumber,
            DateTimeOffset? nextRetryAt,
            DateTimeOffset? failedAt = null)
        {
            Current = Current with
            {
                State = nextRetryAt.HasValue ? SyncRunState.WaitingToRetry : SyncRunState.Failed,
                RunId = runId,
                CurrentEntity = entityKey,
                LastError = error,
                TryNumber = tryNumber,
                NextRetryAt = nextRetryAt,
                LastFailureAt = failedAt ?? DateTimeOffset.UtcNow
            };
        }

        public void RecordSuccess(string runId, DateTimeOffset finishedAt)
        {
            Current = Current with
            {
                State = SyncRunState.Succeeded,
                RunId = runId,
                LastSuccessAt = finishedAt
            };
        }
    }

    private sealed class RecordingApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public TaskCompletionSource StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            StopRequested.TrySetResult();
        }
    }

    private const string Swagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
          "version": 1,
          "entities": {
          "assets": { "operationId": "Assets_Get", "primaryKey": ["id"] },
          "rawDataTemperaturedata": {
            "operationId": "RawData_GetTemperatureData",
            "primaryKey": ["id"],
            "watermark": { "field": "recordedAt" }
          }
        }
      }
    }
    """;
}
