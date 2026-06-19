#nullable enable

using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Config;

public sealed record FileExportOptions
{
    public string RootPath { get; init; } = ".artifacts/exports";

    public string Format { get; init; } = "parquet";

    public IReadOnlyDictionary<string, FileExportEntityOptions> Entities { get; init; } =
        new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase);

    public FileExportEntityOptions GetEntityOptions(string entityKey)
    {
        return SyncEntityOptionMatcher.GetEntityOptions(
            Entities,
            entityKey,
            FileExportEntityOptions.Default,
            "File export options");
    }

    public FileExportEntityOptions GetEntityOptions(SwaggerSyncEntityMetadata metadata)
    {
        return SyncEntityOptionMatcher.GetEntityOptions(
            Entities,
            metadata,
            FileExportEntityOptions.Default,
            "File export options");
    }

    public static FileExportOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var format = configuration.GetValue<string>("FileExport:format");
        if (!string.IsNullOrWhiteSpace(format) &&
            !string.Equals(format, "parquet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FileExport:format must be parquet.");
        }

        var rootPath = configuration.GetValue<string>("FileExport:rootPath");

        return new FileExportOptions
        {
            RootPath = string.IsNullOrWhiteSpace(rootPath) ? ".artifacts/exports" : rootPath,
            Format = "parquet",
            Entities = ReadEntityOptions(configuration)
        };
    }

    private static IReadOnlyDictionary<string, FileExportEntityOptions> ReadEntityOptions(
        IConfiguration configuration)
    {
        var entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var entitySection in configuration.GetSection("FileExport:entities").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(entitySection.Key))
            {
                continue;
            }

            entities[entitySection.Key] = new FileExportEntityOptions
            {
                DataMode = ReadDataMode(entitySection),
                PartitionDate = ReadPartitionDate(entitySection)
            };
        }

        foreach (var entry in SyncPlanConfigurationReader.Read(configuration))
        {
            if (entry.OptionsSection == null || !HasFileExportPolicy(entry.OptionsSection))
            {
                continue;
            }

            SyncEntityOptionMatcher.RemoveNormalizedMatch(entities, entry.EntityKey);
            entities[entry.EntityKey] = new FileExportEntityOptions
            {
                DataMode = ReadDataMode(entry.OptionsSection),
                PartitionDate = ReadPartitionDate(entry.OptionsSection)
            };
        }

        return entities;
    }

    private static bool HasFileExportPolicy(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.GetValue<string>("dataMode")) ||
            !string.IsNullOrWhiteSpace(configuration.GetValue<string>("partitionDate"));
    }

    private static FileExportDataMode ReadDataMode(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("dataMode");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return FileExportDataMode.Differential;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "differential" => FileExportDataMode.Differential,
            "full" => FileExportDataMode.Full,
            _ => throw new InvalidOperationException(
                "Filesystem export dataMode must be one of: differential, full.")
        };
    }

    private static FileExportPartitionDateMode ReadPartitionDate(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("partitionDate");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return FileExportPartitionDateMode.WatermarkDay;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "watermarkday" => FileExportPartitionDateMode.WatermarkDay,
            "watermark-day" => FileExportPartitionDateMode.WatermarkDay,
            "exportrunday" => FileExportPartitionDateMode.ExportRunDay,
            "export-run-day" => FileExportPartitionDateMode.ExportRunDay,
            _ => throw new InvalidOperationException(
                "Filesystem export partitionDate must be one of: watermarkDay, exportRunDay.")
        };
    }
}

public sealed record FileExportEntityOptions
{
    public static FileExportEntityOptions Default { get; } = new();

    public FileExportDataMode DataMode { get; init; } = FileExportDataMode.Differential;

    public FileExportPartitionDateMode PartitionDate { get; init; } =
        FileExportPartitionDateMode.WatermarkDay;
}

public enum FileExportDataMode
{
    Differential,
    Full
}

public enum FileExportPartitionDateMode
{
    WatermarkDay,
    ExportRunDay
}
