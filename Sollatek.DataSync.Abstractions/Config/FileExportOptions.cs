#nullable enable

using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Config;

public sealed record FileExportOptions
{
    public const string DefaultFolderFormat = "{entityKey}/year={date:yyyy}/month={date:MM}/day={date:dd}";

    public const string DefaultFileNameFormat = "part-{part:000000}.{format}";

    public static string DefaultStatePath => Path.Combine(AppContext.BaseDirectory, "_state", "sync-state.json");

    public string RootPath { get; init; } = ".artifacts/exports";

    public string StatePath { get; init; } = DefaultStatePath;

    public string Format { get; init; } = "parquet";

    public string FolderFormat { get; init; } = DefaultFolderFormat;

    public string FileNameFormat { get; init; } = DefaultFileNameFormat;

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

        var format = ExportFormatNames.ToFileExtension(ExportFormatNames.NormalizePortalFormat(
            configuration.GetValue<string>("FileExport:format"),
            "FileExport:format",
            ExportFormatNames.Parquet));

        var rootPath = configuration.GetValue<string>("FileExport:rootPath");
        var statePath = configuration.GetValue<string>("FileExport:statePath");
        var folderFormat = configuration.GetValue<string>("FileExport:folderFormat");
        var fileNameFormat = configuration.GetValue<string>("FileExport:fileNameFormat");

        return new FileExportOptions
        {
            RootPath = string.IsNullOrWhiteSpace(rootPath) ? ".artifacts/exports" : rootPath,
            StatePath = ResolveStatePath(statePath),
            Format = format,
            FolderFormat = string.IsNullOrWhiteSpace(folderFormat) ? DefaultFolderFormat : folderFormat,
            FileNameFormat = string.IsNullOrWhiteSpace(fileNameFormat) ? DefaultFileNameFormat : fileNameFormat,
            Entities = ReadEntityOptions(configuration)
        };
    }

    private static IReadOnlyDictionary<string, FileExportEntityOptions> ReadEntityOptions(
        IConfiguration configuration)
    {
        if (configuration.GetSection("FileExport:entities").GetChildren().Any())
        {
            throw new InvalidOperationException(
                "FileExport:entities is no longer supported. Move filesystem export policies to SyncPlan entries.");
        }

        var entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase);
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
                PartitionDate = ReadPartitionDate(entry.OptionsSection),
                OutputName = ReadOutputName(entry.OptionsSection)
            };
        }

        return entities;
    }

    private static bool HasFileExportPolicy(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.GetValue<string>("dataMode")) ||
            !string.IsNullOrWhiteSpace(configuration.GetValue<string>("partitionDate")) ||
            !string.IsNullOrWhiteSpace(configuration.GetValue<string>("outputName"));
    }

    private static string ResolveStatePath(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return DefaultStatePath;
        }

        var trimmed = configuredValue.Trim();
        return Path.IsPathFullyQualified(trimmed)
            ? trimmed
            : Path.GetFullPath(trimmed, AppContext.BaseDirectory);
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

    private static string? ReadOutputName(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("outputName");
        return string.IsNullOrWhiteSpace(configuredValue) ? null : configuredValue.Trim();
    }
}

public sealed record FileExportEntityOptions
{
    public static FileExportEntityOptions Default { get; } = new();

    public FileExportDataMode DataMode { get; init; } = FileExportDataMode.Differential;

    public FileExportPartitionDateMode PartitionDate { get; init; } =
        FileExportPartitionDateMode.WatermarkDay;

    public string? OutputName { get; init; }
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
