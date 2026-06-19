#nullable enable

using System.Globalization;

namespace Sollatek.DataSync.Export;

public static class DailyExportPath
{
    public static string Build(
        string rootPath,
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        if (!IsSafeFolderName(entityKey))
        {
            throw new InvalidOperationException(
                $"Entity key '{entityKey}' is not a safe export folder name.");
        }

        if (partNumber < 0)
        {
            throw new InvalidOperationException("partNumber must be greater than or equal to 0.");
        }

        return Path.Combine(
            rootPath,
            entityKey,
            $"year={day.Year.ToString("0000", CultureInfo.InvariantCulture)}",
            $"month={day.Month.ToString("00", CultureInfo.InvariantCulture)}",
            $"day={day.Day.ToString("00", CultureInfo.InvariantCulture)}",
            $"part-{partNumber.ToString("000000", CultureInfo.InvariantCulture)}.parquet");
    }

    private static bool IsSafeFolderName(string value)
    {
        return value.All(character =>
            char.IsLetterOrDigit(character) ||
            character == '_' ||
            character == '-');
    }
}
