#nullable enable

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring.StatusEndpoint;

public sealed class StatusHttpHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<StatusHttpHostedService> _logger;
    private readonly MonitoringOptions _options;
    private readonly ISyncMonitor _monitor;
    private WebApplication? _app;

    public StatusHttpHostedService(
        ILogger<StatusHttpHostedService> logger,
        MonitoringOptions options,
        ISyncMonitor monitor)
    {
        _logger = logger;
        _options = options;
        _monitor = monitor;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.StatusEndpoint.Enabled)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(_options.StatusEndpoint.Url);

        var app = builder.Build();
        app.MapGet(_options.StatusEndpoint.StatusPath, () => Results.Json(ToResponse(_monitor.Current)));
        app.MapGet(_options.StatusEndpoint.HealthPath, () => Results.Json(new
        {
            status = "ok"
        }));

        await app.StartAsync(cancellationToken);
        _app = app;

        _logger.LogInformation(
            "DataSync status endpoint listening on {Url}. Status path: {StatusPath}. Health path: {HealthPath}.",
            _options.StatusEndpoint.Url,
            _options.StatusEndpoint.StatusPath,
            _options.StatusEndpoint.HealthPath);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static object ToResponse(SyncRunStatus status)
    {
        return new
        {
            state = status.State.ToString(),
            status.RunId,
            status.CurrentEntity,
            status.StartedAt,
            status.LastSuccessAt,
            status.LastFailureAt,
            status.LastError,
            status.TryNumber,
            status.NextRetryAt,
            status.RecordsProcessed,
            status.PagesProcessed,
            status.FilesProcessed
        };
    }
}
