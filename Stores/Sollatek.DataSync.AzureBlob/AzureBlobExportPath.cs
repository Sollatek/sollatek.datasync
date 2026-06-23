#nullable enable

using Sollatek.DataSync.Config;
using Sollatek.DataSync.Export;

namespace Sollatek.DataSync.AzureBlob;

internal static class AzureBlobExportPath
{
    public static string BuildBlobName(
        FileExportOptions options,
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        var relativePath = DailyExportPath.BuildRelative(options, entityKey, day, partNumber);
        var prefix = GetRootPrefix(options);
        return string.IsNullOrEmpty(prefix)
            ? relativePath
            : $"{prefix}/{relativePath}";
    }

    public static string GetRootPrefix(FileExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return NormalizeBlobName(options.RootPath).Trim('/');
    }

    public static string? ToRelativePath(
        FileExportOptions options,
        string blobName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var normalizedName = NormalizeBlobName(blobName);
        var prefix = GetRootPrefix(options);
        if (string.IsNullOrEmpty(prefix))
        {
            return normalizedName;
        }

        if (string.Equals(normalizedName, prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var prefixWithSeparator = $"{prefix}/";
        return normalizedName.StartsWith(prefixWithSeparator, StringComparison.Ordinal)
            ? normalizedName[prefixWithSeparator.Length..]
            : null;
    }

    private static string NormalizeBlobName(string value)
    {
        return value
            .Replace("\\", "/", StringComparison.Ordinal)
            .Trim('/');
    }
}
