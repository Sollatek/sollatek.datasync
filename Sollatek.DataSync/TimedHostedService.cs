using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Notifications;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync;
using Sollatek.DataSync.Sync.Contract;

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
    private readonly ISyncContractStore _syncContractStore;
    private readonly ISyncTargetDataStore _syncTargetDataStore;
    private readonly IAsyncExportRowSource _asyncExportRowSource;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IFailureNotificationSender _failureNotificationSender;
    private SwaggerBackedSyncPlan _syncPlan;
    private SyncContractSnapshot _pendingSyncContract;
    private DateTimeOffset? _nextRangeEnd;
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
        ISyncContractStore syncContractStore,
        ISyncTargetDataStore syncTargetDataStore,
        IHostApplicationLifetime applicationLifetime,
        IAsyncExportRowSource asyncExportRowSource,
        IFailureNotificationSender failureNotificationSender)
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
        _syncContractStore = syncContractStore;
        _syncTargetDataStore = syncTargetDataStore;
        _asyncExportRowSource = asyncExportRowSource;
        _applicationLifetime = applicationLifetime;
        _failureNotificationSender = failureNotificationSender;
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        var loadResult = await SchemaAwareSyncPlanLoader.LoadAsync(
            _httpClientFactory.CreateClient(),
            _configuration,
            SchemaContractOptions.FromConfiguration(_configuration),
            _syncContractStore,
            _logger,
            stoppingToken);
        _syncPlan = loadResult.Plan;
        _pendingSyncContract = loadResult.PendingAcceptance;

        _logger.LogInformation(
            "Loaded swagger sync metadata for {EntityCount} selected entities: {Entities}",
            _syncPlan.MetadataEntities.Count,
            string.Join(", ", _syncPlan.MetadataEntities.Select(x => x.Key)));
        _logger.LogInformation("Timed Hosted Service running");

        _timer = new Timer(_ => DoWork(stoppingToken), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SchedulePlan(SyncSchedulePlanner.GetStartupPlan(_syncOptions, DateTimeOffset.UtcNow));
    }

    private async void DoWork(CancellationToken cancellationToken)
    {
        var rangeEnd = _nextRangeEnd ?? DateTimeOffset.UtcNow;
        _nextRangeEnd = null;
        _logger.LogInformation("Job Started at {S}",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        _timer?.Change(Timeout.Infinite, 0);
        var runId = Guid.NewGuid().ToString("N");
        _monitor.RecordRunStarted(runId);

        try
        {
            var syncJobs = await BuildJobsAsync(rangeEnd, cancellationToken);
            var expectedCompletedRangeEnd = SyncSchedulePlanner.GetCurrentCompletedPeriodEnd(
                _syncOptions,
                DateTimeOffset.UtcNow);
            syncJobs = AnnotateLag(syncJobs, expectedCompletedRangeEnd);
            _monitor.RecordRunPlanned(
                runId,
                GetScheduleModeName(),
                rangeEnd,
                expectedCompletedRangeEnd,
                syncJobs.Count);

            await _syncRunner.RunAsync(runId, syncJobs, cancellationToken);
            if (_pendingSyncContract is not null)
            {
                var acceptedContract = _pendingSyncContract with
                {
                    AcceptedAtUtc = DateTimeOffset.UtcNow
                };
                await _syncContractStore.SaveAsync(
                    acceptedContract,
                    cancellationToken);
                _pendingSyncContract = null;
                _logger.LogInformation(
                    "Accepted DataSync contract {ContractHash} at Schema:version {SchemaVersion}.",
                    acceptedContract.ContractHash,
                    acceptedContract.SchemaVersion);
            }

            await SyncStateCoordinator.SaveSuccessfulEndsAsync(
                _syncStateStore,
                syncJobs,
                cancellationToken);

            _tryNumber = 1;
            _monitor.RecordRunCompletedRange(
                rangeEnd,
                expectedCompletedRangeEnd,
                SyncSchedulePlanner.GetLagPeriods(_syncOptions, rangeEnd, expectedCompletedRangeEnd));
            _monitor.RecordSuccess(runId, DateTimeOffset.UtcNow);
            _logger.LogInformation("Job Finished at {S}",
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            if (_syncOptions.StopWhenFinished)
            {
                _logger.LogInformation("Sync run finished and Sync:stopWhenFinished is enabled. Stopping application.");
                _applicationLifetime.StopApplication();
                return;
            }

            var nextPlan = SyncSchedulePlanner.GetNextPlan(_syncOptions, DateTimeOffset.UtcNow);
            SchedulePlan(nextPlan);
            _logger.LogInformation(
                "Job rescheduled to run again in {RunInterval} with planned range end {RangeEnd:O}.",
                nextPlan.Delay,
                nextPlan.RangeEnd);
        }
        catch (Exception e)
        {
            _logger.LogError(new EventId(1000), e, "---Error");
            await ScheduleAfterFailureAsync(runId, e, rangeEnd, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<SyncJob>> BuildJobsAsync(
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken)
    {
        if (_syncPlan == null)
        {
            throw new InvalidOperationException("Swagger sync metadata must be loaded before a sync run starts.");
        }

        var planningStates = await SyncStateCoordinator.LoadPlanningStatesAsync(
            _syncStateStore,
            _syncTargetDataStore,
            _syncPlan.MetadataEntities,
            cancellationToken);
        var jobs = SyncJobPlanner.Plan(_syncPlan, _syncOptions, _syncPlanOptions, planningStates, rangeEnd);
        if (_syncOptions.TransferMode == SyncTransferMode.AsyncExport)
        {
            jobs = await ApplyActiveAsyncExportRequestsAsync(jobs, cancellationToken);
        }

        _logger.LogInformation(
            "Planned sync run for {EntityCount} entities ending at {RangeEnd:O}. {StateCount} entities have persisted sync state.",
            jobs.Count,
            rangeEnd,
            planningStates.Count(x => x.Value.LastSuccessfulEnd.HasValue));

        return jobs;
    }

    private async Task<IReadOnlyList<SyncJob>> ApplyActiveAsyncExportRequestsAsync(
        IReadOnlyList<SyncJob> jobs,
        CancellationToken cancellationToken)
    {
        var activeRequests = await _asyncExportRowSource.GetActiveRequestsAsync(cancellationToken);
        if (activeRequests.Count == 0)
        {
            return jobs;
        }

        var jobsByEntity = jobs.ToDictionary(x => x.Metadata.Key, StringComparer.OrdinalIgnoreCase);
        var activeRequestsByEntity = new Dictionary<string, List<AsyncExportActiveRequestState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var activeRequest in activeRequests)
        {
            if (!jobsByEntity.ContainsKey(activeRequest.EntityKey))
            {
                throw new InvalidOperationException(
                    $"Async export has previous unfinished request state for sync entity '{activeRequest.EntityKey}', but that entity is not in the current sync plan.");
            }

            if (!activeRequestsByEntity.TryGetValue(activeRequest.EntityKey, out var entityRequests))
            {
                entityRequests = [];
                activeRequestsByEntity.Add(activeRequest.EntityKey, entityRequests);
            }

            entityRequests.Add(activeRequest);
        }

        return jobs
            .Where(job => activeRequestsByEntity.ContainsKey(job.Metadata.Key))
            .Select(job =>
            {
                var entityRequests = activeRequestsByEntity[job.Metadata.Key];
                if (entityRequests.Count == 1)
                {
                    var activeRequest = entityRequests[0];
                    return job with
                    {
                        Range = new SyncDateRange(activeRequest.RangeStart, activeRequest.RangeEnd)
                    };
                }

                return job;
            })
            .ToArray();
    }

    private async Task ScheduleAfterFailureAsync(
        string runId,
        Exception exception,
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken)
    {
        var failedEntity = _monitor.Current.CurrentEntity;

        if (_tryNumber >= _retryOptions.MaxTries)
        {
            var tryNumber = _tryNumber;
            var failedAt = DateTimeOffset.UtcNow;
            _monitor.RecordFailure(runId, failedEntity, exception.Message, _tryNumber, nextRetryAt: null);
            await SendFailureNotificationAsync(
                runId,
                failedEntity,
                exception,
                tryNumber,
                rangeEnd,
                failedAt,
                cancellationToken);
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

            var nextPlan = SyncSchedulePlanner.GetNextPlan(_syncOptions, DateTimeOffset.UtcNow);
            SchedulePlan(nextPlan);
            _logger.LogError(
                "Sync run {RunId} exhausted {MaxTries} tries. Job rescheduled to run again in {RunInterval} with planned range end {RangeEnd:O}.",
                runId,
                _retryOptions.MaxTries,
                nextPlan.Delay,
                nextPlan.RangeEnd);
            return;
        }

        var nextTryNumber = _tryNumber + 1;
        var now = DateTimeOffset.UtcNow;
        var nextRetryAt = RetryDelayPlanner.GetNextRetryTime(now, nextTryNumber, _retryOptions);
        var retryDelay = nextRetryAt - now;
        _tryNumber = nextTryNumber;

        _monitor.RecordFailure(runId, failedEntity, exception.Message, nextTryNumber, nextRetryAt);
        _nextRangeEnd = rangeEnd;
        _timer?.Change(retryDelay, Timeout.InfiniteTimeSpan);
        _logger.LogInformation(
            "Sync run {RunId} scheduled retry try {TryNumber} at {NextRetryAt:O}.",
            runId,
            nextTryNumber,
            nextRetryAt);
    }

    private async Task SendFailureNotificationAsync(
        string runId,
        string failedEntity,
        Exception exception,
        int tryNumber,
        DateTimeOffset rangeEnd,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _failureNotificationSender.SendAsync(
                new FailureNotificationMessage(
                    runId,
                    failedEntity,
                    exception.Message,
                    tryNumber,
                    _retryOptions.MaxTries,
                    rangeEnd.ToUniversalTime(),
                    failedAt.ToUniversalTime()),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception notificationException)
        {
            _logger.LogError(
                notificationException,
                "Failed to send sync failure notification email for run {RunId}.",
                runId);
        }
    }

    private void SchedulePlan(SyncScheduleRunPlan plan)
    {
        _nextRangeEnd = plan.RangeEnd;
        _timer?.Change(plan.Delay, Timeout.InfiniteTimeSpan);
        var now = DateTimeOffset.UtcNow;
        _monitor.RecordRunScheduled(
            GetScheduleModeName(),
            now + plan.Delay,
            SyncSchedulePlanner.GetCurrentCompletedPeriodEnd(_syncOptions, now));

        if (!plan.ShouldRun)
        {
            _logger.LogInformation(
                "Initial sync run is disabled. First scheduled run is in {Delay} with planned range end {RangeEnd:O}.",
                plan.Delay,
                plan.RangeEnd);
        }
    }

    private IReadOnlyList<SyncJob> AnnotateLag(
        IReadOnlyList<SyncJob> jobs,
        DateTimeOffset expectedCompletedRangeEnd)
    {
        return jobs
            .Select(job => job with
            {
                ExpectedCompletedRangeEndUtc = expectedCompletedRangeEnd,
                LagPeriods = SyncSchedulePlanner.GetLagPeriods(
                    _syncOptions,
                    job.Range.Start,
                    expectedCompletedRangeEnd)
            })
            .ToArray();
    }

    private string GetScheduleModeName()
    {
        return _syncOptions.Schedule.Mode.ToString().ToLowerInvariant();
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
