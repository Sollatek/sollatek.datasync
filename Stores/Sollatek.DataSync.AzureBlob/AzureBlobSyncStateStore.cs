#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobSyncStateStore : ISyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IBlobStateContainer _container;
    private readonly StateOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AzureBlobSyncStateStore(
        IBlobStateContainer container,
        StateOptions options)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
        string entityKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            return state.Entities.TryGetValue(entityKey, out var entityState)
                ? entityState.LastSuccessfulEndUtc
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSuccessfulEndAsync(
        string entityKey,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            state.Entities[entityKey] = new AzureBlobSyncEntityState(
                end.ToUniversalTime(),
                DateTimeOffset.UtcNow);
            await WriteStateAsync(state, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AzureBlobSyncStateDocument> ReadStateAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _container.OpenReadAsync(GetStateBlobName(), cancellationToken);
        if (stream is null)
        {
            return new AzureBlobSyncStateDocument(
                new Dictionary<string, AzureBlobSyncEntityState>(StringComparer.OrdinalIgnoreCase));
        }

        var state = await JsonSerializer.DeserializeAsync<AzureBlobSyncStateDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        if (state?.Entities is null)
        {
            throw new InvalidOperationException($"Azure Blob sync state blob '{GetStateBlobName()}' is invalid.");
        }

        return state with
        {
            Entities = new Dictionary<string, AzureBlobSyncEntityState>(
                state.Entities,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task WriteStateAsync(
        AzureBlobSyncStateDocument state,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        stream.Position = 0;
        await _container.UploadAsync(
            GetStateBlobName(),
            stream,
            "application/json",
            cancellationToken);
    }

    private string GetStateBlobName()
    {
        return AzureBlobStatePath.Combine(_options.RootPath, "sync-state.json");
    }

    private sealed record AzureBlobSyncStateDocument(
        Dictionary<string, AzureBlobSyncEntityState> Entities);

    private sealed record AzureBlobSyncEntityState(
        DateTimeOffset LastSuccessfulEndUtc,
        DateTimeOffset UpdatedAtUtc);
}
