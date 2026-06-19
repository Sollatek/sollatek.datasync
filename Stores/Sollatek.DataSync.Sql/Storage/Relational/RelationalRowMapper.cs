#nullable enable

using System.Text.Json;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalRowMapper
{
    public static RelationalRow Map(RelationalTablePlan table, JsonElement source)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Cannot map sync entity '{table.EntityKey}' because the API record is not a JSON object.");
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            values[column.Name] = TryReadColumnValue(source, column, out var value)
                ? ReadScalarValue(table, column, value)
                : null;
        }

        return new RelationalRow(table.EntityKey, table.TableName, values);
    }

    private static bool TryReadColumnValue(
        JsonElement source,
        RelationalColumnPlan column,
        out JsonElement value)
    {
        if (TryReadPath(source, column.Source, out value))
        {
            return true;
        }

        return !string.Equals(column.Source, column.Name, StringComparison.OrdinalIgnoreCase) &&
               TryReadPath(source, column.Name, out value);
    }

    private static bool TryReadPath(JsonElement source, string path, out JsonElement value)
    {
        value = source;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(value, segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static bool TryGetProperty(JsonElement source, string name, out JsonElement value)
    {
        if (source.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? ReadScalarValue(
        RelationalTablePlan table,
        RelationalColumnPlan column,
        JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => ReadNumber(value),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                $"Column '{column.Name}' for sync entity '{table.EntityKey}' maps JSON path '{column.Source}', but that path is not a scalar value.")
        };
    }

    private static object ReadNumber(JsonElement value)
    {
        if (value.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }

        return value.GetDouble();
    }
}
