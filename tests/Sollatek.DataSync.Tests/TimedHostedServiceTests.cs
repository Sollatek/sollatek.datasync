using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Monitoring;
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

    private static TimedHostedService CreateService(
        HttpClient httpClient,
        ISyncJobRunner runner,
        ISyncStateStore stateStore,
        IHostApplicationLifetime lifetime,
        SyncOptions syncOptions,
        RetryOptions? retryOptions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "data-v1",
                ["SwaggerDocuments:0:url"] = "https://api.sollatek.io/swagger/data-v1/swagger.json",
                ["SyncPlan:0"] = "assets"
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
            lifetime);
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
          "assets": { "operationId": "Assets_Get", "primaryKey": ["id"] }
        }
      }
    }
    """;
}
