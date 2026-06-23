#nullable enable

namespace Sollatek.DataSync.Config;

public static class ExportFormatNames
{
    public const string Csv = "Csv";
    public const string Xml = "Xml";
    public const string Xlsx = "Xlsx";
    public const string Parquet = "Parquet";

    public static string NormalizePortalFormat(string? configuredValue, string key, string defaultValue)
    {
        var value = string.IsNullOrWhiteSpace(configuredValue)
            ? defaultValue
            : configuredValue.Trim();

        return value.ToLowerInvariant() switch
        {
            "csv" => Csv,
            "xml" => Xml,
            "xlsx" => Xlsx,
            "parquet" => Parquet,
            _ => throw new InvalidOperationException(
                $"{key} must be one of: Csv, Xml, Xlsx, Parquet.")
        };
    }

    public static string ToFileExtension(string portalFormat)
    {
        return NormalizePortalFormat(portalFormat, "export format", Parquet).ToLowerInvariant();
    }

    public static bool SupportsRowReading(string portalFormat)
    {
        var normalized = NormalizePortalFormat(portalFormat, "export format", Parquet);
        return string.Equals(normalized, Csv, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Parquet, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsPagedFilesystemWriting(string fileFormat)
    {
        var normalized = NormalizePortalFormat(fileFormat, "FileExport:format", Parquet);
        return string.Equals(normalized, Csv, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Parquet, StringComparison.OrdinalIgnoreCase);
    }
}
