using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync;

namespace Sollatek.DataSync;

public class TimedHostedService : IHostedService, IDisposable
{
    private readonly ILogger<TimedHostedService> _logger;
    private Timer _timer;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SyncOptions _syncOptions;
    private readonly SyncPlanOptions _syncPlanOptions;
    private readonly RetryOptions _retryOptions;
    private readonly ISyncMonitor _monitor;
    private readonly ISyncJobRunner _syncRunner;
    private readonly ISyncStateStore _syncStateStore;
    private readonly ISyncTargetDataStore _syncTargetDataStore;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private SwaggerBackedSyncPlan _syncPlan;
    private int _tryNumber = 1;

    public TimedHostedService(
        ILogger<TimedHostedService> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        SyncOptions syncOptions,
        SyncPlanOptions syncPlanOptions,
        RetryOptions retryOptions,
        ISyncMonitor monitor,
        ISyncJobRunner syncRunner,
        ISyncStateStore syncStateStore,
        ISyncTargetDataStore syncTargetDataStore,
        IHostApplicationLifetime applicationLifetime)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _syncOptions = syncOptions;
        _syncPlanOptions = syncPlanOptions;
        _retryOptions = retryOptions;
        _monitor = monitor;
        _syncRunner = syncRunner;
        _syncStateStore = syncStateStore;
        _syncTargetDataStore = syncTargetDataStore;
        _applicationLifetime = applicationLifetime;
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        var plan = await SwaggerBackedSyncPlanLoader.LoadAsync(
            _httpClientFactory.CreateClient(),
            _configuration,
            stoppingToken);
        _syncPlan = plan;

        _logger.LogInformation(
            "Loaded swagger sync metadata for {EntityCount} selected entities: {Entities}",
            _syncPlan.MetadataEntities.Count,
            string.Join(", ", _syncPlan.MetadataEntities.Select(x => x.Key)));
        _logger.LogInformation("Timed Hosted Service running");

        var firstRunDelay = _syncOptions.RunOnStartup
            ? TimeSpan.Zero
            : _syncOptions.RunInterval;
        _timer = new Timer(_ => DoWork(stoppingToken), null, firstRunDelay, Timeout.InfiniteTimeSpan);
    }

    private async void DoWork(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job Started at {S}",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        _timer?.Change(Timeout.Infinite, 0);
        var runId = Guid.NewGuid().ToString("N");
        _monitor.RecordRunStarted(runId);

        try
        {
            var syncJobs = await BuildJobsAsync(cancellationToken);
            await _syncRunner.RunAsync(runId, syncJobs, cancellationToken);
            await SyncStateCoordinator.SaveSuccessfulEndsAsync(
                _syncStateStore,
                syncJobs,
                cancellationToken);

            _tryNumber = 1;
            _monitor.RecordSuccess(runId, DateTimeOffset.UtcNow);
            _logger.LogInformation("Job Finished at {S}",
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            if (_syncOptions.StopWhenFinished)
            {
                _logger.LogInformation("Sync run finished and Sync:stopWhenFinished is enabled. Stopping application.");
                _applicationLifetime.StopApplication();
                return;
            }

            _timer?.Change(_syncOptions.RunInterval, Timeout.InfiniteTimeSpan);
            _logger.LogInformation(
                "Job rescheduled to run again in {RunInterval}.",
                _syncOptions.RunInterval);
        }
        catch (Exception e)
        {
            _logger.LogError(new EventId(1000), e, "---Error");
            ScheduleAfterFailure(runId, e);
        }
    }

    private async Task<IReadOnlyList<SyncJob>> BuildJobsAsync(CancellationToken cancellationToken)
    {
        if (_syncPlan == null)
        {
            throw new InvalidOperationException("Swagger sync metadata must be loaded before a sync run starts.");
        }

        var rangeEnd = DateTimeOffset.UtcNow;
        var planningStates = await SyncStateCoordinator.LoadPlanningStatesAsync(
            _syncStateStore,
            _syncTargetDataStore,
            _syncPlan.MetadataEntities,
            cancellationToken);
        var jobs = SyncJobPlanner.Plan(_syncPlan, _syncOptions, _syncPlanOptions, planningStates, rangeEnd);

        _logger.LogInformation(
            "Planned sync run for {EntityCount} entities ending at {RangeEnd:O}. {StateCount} entities have persisted sync state.",
            jobs.Count,
            rangeEnd,
            planningStates.Count(x => x.Value.LastSuccessfulEnd.HasValue));

        return jobs;
    }

    private void ScheduleAfterFailure(string runId, Exception exception)
    {
        var failedEntity = _monitor.Current.CurrentEntity;

        if (_tryNumber >= _retryOptions.MaxTries)
        {
            _monitor.RecordFailure(runId, failedEntity, exception.Message, _tryNumber, nextRetryAt: null);
            _tryNumber = 1;

            if (_syncOptions.StopWhenFinished)
            {
                _logger.LogError(
                    "Sync run {RunId} exhausted {MaxTries} tries and Sync:stopWhenFinished is enabled. Stopping application.",
                    runId,
                    _retryOptions.MaxTries);
                _applicationLifetime.StopApplication();
                return;
            }

            _timer?.Change(_syncOptions.RunInterval, Timeout.InfiniteTimeSpan);
            _logger.LogError(
                "Sync run {RunId} exhausted {MaxTries} tries. Job rescheduled to run again in {RunInterval}.",
                runId,
                _retryOptions.MaxTries,
                _syncOptions.RunInterval);
            return;
        }

        var nextTryNumber = _tryNumber + 1;
        var now = DateTimeOffset.UtcNow;
        var nextRetryAt = RetryDelayPlanner.GetNextRetryTime(now, nextTryNumber, _retryOptions);
        var retryDelay = nextRetryAt - now;
        _tryNumber = nextTryNumber;

        _monitor.RecordFailure(runId, failedEntity, exception.Message, nextTryNumber, nextRetryAt);
        _timer?.Change(retryDelay, Timeout.InfiniteTimeSpan);
        _logger.LogInformation(
            "Sync run {RunId} scheduled retry try {TryNumber} at {NextRetryAt:O}.",
            runId,
            nextTryNumber,
            nextRetryAt);
    }

    public Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timed Hosted Service is stopping");

        _timer?.Change(Timeout.Infinite, 0);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
