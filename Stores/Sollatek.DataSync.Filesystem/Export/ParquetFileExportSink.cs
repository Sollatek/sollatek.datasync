#nullable enable

using System.Globalization;
using System.Text;
using Parquet;
using Parquet.Schema;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Export;

public sealed class ParquetFileExportSink : IFileExportObjectSink
{
    private readonly FileExportOptions _options;

    public ParquetFileExportSink(FileExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string?> WriteAsync(
        string entityKey,
        DateOnly day,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int partNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return null;
        }

        var path = GetOutputPath(entityKey, day, partNumber);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Filesystem export file already exists: {path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var portalFormat = ExportFormatNames.NormalizePortalFormat(
            _options.Format,
            "FileExport:format",
            ExportFormatNames.Parquet);
        if (string.Equals(portalFormat, ExportFormatNames.Csv, StringComparison.OrdinalIgnoreCase))
        {
            await WriteCsvAsync(path, rows, cancellationToken);
            return path;
        }

        if (!string.Equals(portalFormat, ExportFormatNames.Parquet, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Paged filesystem exports can write FileExport:format values parquet and csv. Use asyncExport for portal-native xml and xlsx files.");
        }

        var columns = BuildColumns(rows);
        var schema = new ParquetSchema(columns.Select(x =>
            new DataField(x.Name, x.ClrType, isNullable: true, isArray: false, propertyName: null)));

        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: cancellationToken);
        using var rowGroup = writer.CreateRowGroup();
        var fields = schema.GetDataFields();

        for (var index = 0; index < columns.Count; index++)
        {
            await WriteColumnAsync(rowGroup, fields[index], columns[index], rows, cancellationToken);
        }

        return path;
    }

    public async Task<string> CopyAsync(
        string entityKey,
        DateOnly day,
        string sourcePath,
        int partNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var path = GetOutputPath(entityKey, day, partNumber);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Filesystem export file already exists: {path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination, cancellationToken);
        return path;
    }

    public bool Exists(
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        return File.Exists(GetOutputPath(entityKey, day, partNumber));
    }

    public Task<bool> ExistsAsync(
        string entityKey,
        DateOnly day,
        int partNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Exists(entityKey, day, partNumber));
    }

    private string GetOutputPath(
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        return DailyExportPath.Build(_options, entityKey, day, partNumber);
    }

    private static IReadOnlyList<ParquetColumn> BuildColumns(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                {
                    columns.Add(key);
                }
            }
        }

        return columns
            .Select(column => new ParquetColumn(column, InferColumnType(column, rows)))
            .ToArray();
    }

    private static Type InferColumnType(
        string column,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        Type? type = null;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(column, out var value) || value is null)
            {
                continue;
            }

            var current = GetSupportedColumnType(value);
            if (type is null)
            {
                type = current;
                continue;
            }

            if (type != current)
            {
                return typeof(string);
            }
        }

        return type ?? typeof(string);
    }

    private static Type GetSupportedColumnType(object value)
    {
        return value switch
        {
            bool => typeof(bool),
            int => typeof(int),
            long => typeof(long),
            float => typeof(float),
            double => typeof(double),
            decimal => typeof(decimal),
            DateTime => typeof(DateTime),
            _ => typeof(string)
        };
    }

    private static Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        ParquetColumn column,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (column.ClrType == typeof(bool))
        {
            return WriteNullableValueColumnAsync<bool>(rowGroup, field, column.Name, rows, Convert.ToBoolean, cancellationToken);
        }

        if (column.ClrType == typeof(int))
        {
            return WriteNullableValueColumnAsync<int>(rowGroup, field, column.Name, rows, Convert.ToInt32, cancellationToken);
        }

        if (column.ClrType == typeof(long))
        {
            return WriteNullableValueColumnAsync<long>(rowGroup, field, column.Name, rows, Convert.ToInt64, cancellationToken);
        }

        if (column.ClrType == typeof(float))
        {
            return WriteNullableValueColumnAsync<float>(rowGroup, field, column.Name, rows, Convert.ToSingle, cancellationToken);
        }

        if (column.ClrType == typeof(double))
        {
            return WriteNullableValueColumnAsync<double>(rowGroup, field, column.Name, rows, Convert.ToDouble, cancellationToken);
        }

        if (column.ClrType == typeof(decimal))
        {
            return WriteNullableValueColumnAsync<decimal>(rowGroup, field, column.Name, rows, Convert.ToDecimal, cancellationToken);
        }

        if (column.ClrType == typeof(DateTime))
        {
            return WriteNullableValueColumnAsync<DateTime>(rowGroup, field, column.Name, rows, value => (DateTime)value, cancellationToken);
        }

        return WriteStringColumnAsync(rowGroup, field, column.Name, rows, cancellationToken);
    }

    private static Task WriteNullableValueColumnAsync<T>(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        string column,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Func<object, T> convert,
        CancellationToken cancellationToken)
        where T : struct
    {
        var values = rows
            .Select(row => row.TryGetValue(column, out var value) && value is not null
                ? convert(value)
                : (T?)null)
            .ToArray();

        return rowGroup.WriteAsync<T>(
            field,
            values.AsMemory(),
            cancellationToken: cancellationToken);
    }

    private static Task WriteStringColumnAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        string column,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        var values = rows
            .Select(row => row.TryGetValue(column, out var value) ? ConvertToString(value) : null)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        return rowGroup.WriteAsync(field, values);
    }

    private static string? ConvertToString(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        var columns = BuildColumns(rows).Select(x => x.Name).ToArray();
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await writer.WriteLineAsync(string.Join(",", columns.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = columns.Select(column =>
                row.TryGetValue(column, out var value) ? EscapeCsv(ConvertToString(value)) : string.Empty);
            await writer.WriteLineAsync(string.Join(",", values));
        }
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var mustQuote = value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal);
        if (!mustQuote)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed record ParquetColumn(string Name, Type ClrType);
}
