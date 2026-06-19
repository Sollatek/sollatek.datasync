#nullable enable

using System.Globalization;
using Parquet;
using Parquet.Schema;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Export;

public sealed class ParquetFileExportSink
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

        var path = DailyExportPath.Build(_options.RootPath, entityKey, day, partNumber);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Parquet export file already exists: {path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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

    private sealed record ParquetColumn(string Name, Type ClrType);
}
