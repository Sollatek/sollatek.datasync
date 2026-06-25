#nullable enable

using Sollatek.DataSync.Config;
using Sollatek.DataSync.Export;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobFileExportObjectSink : IFileExportObjectSink
{
    private readonly IBlobExportContainer _container;
    private readonly FileExportOptions _options;

    public AzureBlobFileExportObjectSink(
        IBlobExportContainer container,
        FileExportOptions options)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string?> WriteAsync(
        string entityKey,
        DateOnly day,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int partNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return null;
        }

        var blobName = AzureBlobExportPath.BuildBlobName(_options, entityKey, day, partNumber);
        if (!_options.ReplaceExisting && await _container.ExistsAsync(blobName, cancellationToken))
        {
            throw new InvalidOperationException($"Azure Blob export object already exists: {blobName}");
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "sollatek-datasync-blob", Guid.NewGuid().ToString("N"));
        try
        {
            var temporaryOptions = _options with
            {
                RootPath = temporaryRoot
            };
            var temporarySink = new ParquetFileExportSink(temporaryOptions);
            var temporaryPath = await temporarySink.WriteAsync(
                entityKey,
                day,
                rows,
                partNumber,
                cancellationToken);
            if (temporaryPath is null)
            {
                return null;
            }

            await using var stream = File.OpenRead(temporaryPath);
            await _container.UploadAsync(
                blobName,
                stream,
                GetContentType(),
                _options.ReplaceExisting,
                cancellationToken);
            return blobName;
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    public async Task<string> CopyAsync(
        string entityKey,
        DateOnly day,
        string sourcePath,
        int partNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var blobName = AzureBlobExportPath.BuildBlobName(_options, entityKey, day, partNumber);
        if (!_options.ReplaceExisting && await _container.ExistsAsync(blobName, cancellationToken))
        {
            throw new InvalidOperationException($"Azure Blob export object already exists: {blobName}");
        }

        await using var stream = File.OpenRead(sourcePath);
        await _container.UploadAsync(
            blobName,
            stream,
            GetContentType(),
            _options.ReplaceExisting,
            cancellationToken);
        return blobName;
    }

    public Task<bool> ExistsAsync(
        string entityKey,
        DateOnly day,
        int partNumber,
        CancellationToken cancellationToken)
    {
        var blobName = AzureBlobExportPath.BuildBlobName(_options, entityKey, day, partNumber);
        return _container.ExistsAsync(blobName, cancellationToken);
    }

    private string? GetContentType()
    {
        return _options.Format.ToLowerInvariant() switch
        {
            "csv" => "text/csv",
            "xml" => "application/xml",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "parquet" => "application/vnd.apache.parquet",
            _ => null
        };
    }
}
