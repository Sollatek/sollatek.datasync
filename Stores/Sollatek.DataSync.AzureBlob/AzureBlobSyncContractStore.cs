#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Contract;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobSyncContractStore : ISyncContractStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IBlobStateContainer _container;
    private readonly StateOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AzureBlobSyncContractStore(
        IBlobStateContainer container,
        StateOptions options)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<SyncContractSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = await _container.OpenReadAsync(
                GetContractBlobName(),
                cancellationToken);
            if (stream is null)
            {
                return null;
            }

            var snapshot = await JsonSerializer.DeserializeAsync<SyncContractSnapshot>(
                stream,
                JsonOptions,
                cancellationToken);
            if (snapshot is null)
            {
                throw new InvalidOperationException(
                    $"Azure Blob sync contract '{GetContractBlobName()}' is invalid.");
            }

            snapshot.Validate();
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(
        SyncContractSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new MemoryStream();
            await JsonSerializer.SerializeAsync(
                stream,
                snapshot,
                JsonOptions,
                cancellationToken);
            stream.Position = 0;
            await _container.UploadAsync(
                GetContractBlobName(),
                stream,
                "application/json",
                cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GetContractBlobName()
    {
        return AzureBlobStatePath.Combine(_options.RootPath, "sync-contract.json");
    }
}
