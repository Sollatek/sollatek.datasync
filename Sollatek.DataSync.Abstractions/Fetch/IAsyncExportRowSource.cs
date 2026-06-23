#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Json;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Fetch;

public interface IAsyncExportRowSource
{
    Task<IReadOnlyList<AsyncExportActiveRequestState>> GetActiveRequestsAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<AsyncExportActiveRequestState>>([]);
    }

    Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        CancellationToken cancellationToken);

    async IAsyncEnumerable<AsyncExportDownloadedFile> PrepareUnorderedAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var files = await PrepareAsync(requests, cancellationToken);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    async IAsyncEnumerable<AsyncExportDownloadedFile> PrepareOrderedAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var files = await PrepareAsync(requests, cancellationToken);
        foreach (var file in files.OrderBy(x => x.Request.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    IAsyncEnumerable<JsonElement> ReadRowsAsync(
        AsyncExportDownloadedFile file,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        AsyncExportDownloadedFile file,
        CancellationToken cancellationToken);
}

public sealed record AsyncExportRequest(
    int Sequence,
    SyncJob Job,
    SwaggerSyncEntityMetadata RequestMetadata,
    SyncDateRange Range);

public sealed record AsyncExportDownloadedFile(
    AsyncExportRequest Request,
    string Path,
    string? StateKey = null);

public sealed record AsyncExportActiveRequestState(
    string EntityKey,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    string Status);

public sealed class UnconfiguredAsyncExportRowSource : IAsyncExportRowSource
{
    public static UnconfiguredAsyncExportRowSource Instance { get; } = new();

    private UnconfiguredAsyncExportRowSource()
    {
    }

    public Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
        IReadOnlyList<AsyncExportRequest> requests,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "Async export transfer mode requires an IAsyncExportRowSource service.");
    }

    public async IAsyncEnumerable<JsonElement> ReadRowsAsync(
        AsyncExportDownloadedFile file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "Async export transfer mode requires an IAsyncExportRowSource service.");
#pragma warning disable CS0162
        await Task.CompletedTask;
        yield break;
#pragma warning restore CS0162
    }

    public Task CompleteAsync(
        AsyncExportDownloadedFile file,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "Async export transfer mode requires an IAsyncExportRowSource service.");
    }
}
