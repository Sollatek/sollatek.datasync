#nullable enable

using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalUpsertCommandBuilder
{
    private const int DefaultMaxRowsPerCommand = 500;
    private const int SqlServerMaxParametersPerCommand = 2000;

    public static RelationalCommand BuildUpsert(
        StorageProvider provider,
        RelationalTablePlan table,
        RelationalRow row)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(row);

        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }

        var columns = table.Columns.ToArray();
        var parameters = columns
            .Select((column, index) => BuildParameter(provider, row, column, index))
            .ToArray();

        var sql = provider switch
        {
            StorageProvider.Postgres => BuildPostgres(table, columns, parameters),
            StorageProvider.MySql => BuildMySql(table, columns, parameters),
            StorageProvider.SqlServer => BuildSqlServer(table, columns, parameters),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, parameters);
    }

    public static RelationalCommand BuildUpsert(
        StorageProvider provider,
        RelationalTablePlan table,
        IReadOnlyList<RelationalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);

        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot build an upsert command for sync entity '{table.EntityKey}' without rows.");
        }

        var columns = table.Columns.ToArray();
        if (columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot build an upsert command for sync entity '{table.EntityKey}' without planned columns.");
        }

        if (table.PrimaryKeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot build an upsert command for sync entity '{table.EntityKey}' without primary key columns.");
        }

        var parameterRows = new List<IReadOnlyList<RelationalCommandParameter>>(rows.Count);
        var parameterIndex = 0;

        foreach (var row in rows)
        {
            parameterRows.Add(columns
                .Select(column => BuildParameter(provider, row, column, parameterIndex++))
                .ToArray());
        }

        var parameters = parameterRows
            .SelectMany(row => row)
            .ToArray();
        var sql = provider switch
        {
            StorageProvider.Postgres => BuildPostgresBatch(table, columns, parameterRows),
            StorageProvider.MySql => BuildMySqlBatch(table, columns, parameterRows),
            StorageProvider.SqlServer => BuildSqlServerBatch(table, columns, parameterRows),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, parameters);
    }

    internal static int GetMaxRowsPerCommand(
        StorageProvider provider,
        RelationalTablePlan table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var columnCount = Math.Max(1, table.Columns.Count);
        return provider == StorageProvider.SqlServer
            ? Math.Max(1, SqlServerMaxParametersPerCommand / columnCount)
            : DefaultMaxRowsPerCommand;
    }

    private static RelationalCommandParameter BuildParameter(
        StorageProvider provider,
        RelationalRow row,
        RelationalColumnPlan column,
        int index)
    {
        if (!row.Values.TryGetValue(column.Name, out var value))
        {
            throw new InvalidOperationException(
                $"Relational row for sync entity '{row.EntityKey}' is missing planned column '{column.Name}'.");
        }

        return provider == StorageProvider.Postgres
            ? new RelationalCommandParameter($"p{index + 1}", $"${index + 1}", NormalizeValue(value))
            : new RelationalCommandParameter($"p{index}", $"@p{index}", NormalizeValue(value));
    }

    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            null => null,
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string BuildPostgres(
        RelationalTablePlan table,
        IReadOnlyList<RelationalColumnPlan> columns,
        IReadOnlyList<RelationalCommandParameter> parameters)
    {
        var insertColumns = JoinColumns(StorageProvider.Postgres, columns.Select(x => x.Name));
        var placeholders = string.Join(", ", parameters.Select(x => x.Placeholder));
        var conflictColumns = JoinColumns(StorageProvider.Postgres, table.PrimaryKeyColumns);
        var updateColumns = GetUpdateColumns(table, columns);
        var conflictAction = updateColumns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(
                ", ",
                updateColumns.Select(column =>
                    $"{RelationalSqlDialect.Quote(StorageProvider.Postgres, column.Name)} = EXCLUDED.{RelationalSqlDialect.Quote(StorageProvider.Postgres, column.Name)}"));

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(StorageProvider.Postgres, table.TableName)} ({insertColumns})",
            $"VALUES ({placeholders})",
            $"ON CONFLICT ({conflictColumns}) {conflictAction};");
    }

    private static string BuildPostgresBatch(
        RelationalTablePlan table,
        IReadOnlyList<RelationalColumnPlan> columns,
        IReadOnlyList<IReadOnlyList<RelationalCommandParameter>> parameterRows)
    {
        var insertColumns = JoinColumns(StorageProvider.Postgres, columns.Select(x => x.Name));
        var values = JoinParameterRows(parameterRows);
        var conflictColumns = JoinColumns(StorageProvider.Postgres, table.PrimaryKeyColumns);
        var updateColumns = GetUpdateColumns(table, columns);
        var conflictAction = updateColumns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(
                ", ",
                updateColumns.Select(column =>
                    $"{RelationalSqlDialect.Quote(StorageProvider.Postgres, column.Name)} = EXCLUDED.{RelationalSqlDialect.Quote(StorageProvider.Postgres, column.Name)}"));

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(StorageProvider.Postgres, table.TableName)} ({insertColumns})",
            $"VALUES {values}",
            $"ON CONFLICT ({conflictColumns}) {conflictAction};");
    }

    private static string BuildMySql(
        RelationalTablePlan table,
        IReadOnlyList<RelationalColumnPlan> columns,
        IReadOnlyList<RelationalCommandParameter> parameters)
    {
        var insertColumns = JoinColumns(StorageProvider.MySql, columns.Select(x => x.Name));
        var placeholders = string.Join(", ", parameters.Select(x => x.Placeholder));
        var updateColumns = GetUpdateColumns(table, columns);
        if (updateColumns.Count == 0)
        {
            updateColumns = columns.Take(1).ToArray();
        }

        var updateAssignments = string.Join(
            ", ",
            updateColumns.Select(column =>
                $"{RelationalSqlDialect.Quote(StorageProvider.MySql, column.Name)} = VALUES({RelationalSqlDialect.Quote(StorageProvider.MySql, column.Name)})"));

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(StorageProvider.MySql, table.TableName)} ({insertColumns})",
            $"VALUES ({placeholders})",
            $"ON DUPLICATE KEY UPDATE {updateAssignments};");
    }

    private static string BuildMySqlBatch(
        RelationalTablePlan table,
        IReadOnlyList<RelationalColumnPlan> columns,
        IReadOnlyList<IReadOnlyList<RelationalCommandParameter>> parameterRows)
    {
        var insertColumns = JoinColumns(StorageProvider.MySql, columns.Select(x => x.Name));
        var values = JoinParameterRows(parameterRows);
        var updateColumns = GetUpdateColumns(table, columns);
        if (updateColumns.Count == 0)
        {
            updateColumns = columns.Take(1).ToArray();
        }

        var updateAssignments = string.Join(
            ", ",
            updateColumns.Select(column =>
                $"{RelationalSqlDialect.Quote(StorageProvider.MySql, column.Name)} = VALUES({RelationalSqlDialect.Quote(StorageProvider.MySql, column.Name)})"));

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(StorageProvider.MySql, table.TableName)} ({insertColumns})",
            $"VALUES {values}",
            $"ON DUPLICATE KEY UPDATE {updateAssignments};");
    }

    private static string BuildSqlServer(
        RelationalTablePlan table,
        IReadOnlyList<RelationalColumnPlan> columns,
        IReadOnlyList<RelationalCommandParameter> parameters)
    {
        var sourceColumns = string.Join(
            ", ",
            columns.Select((column, index) =>
                $"{parameters[index].Placeholder} AS {RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)}"));
        var match = string.Join(
            " AND ",
            table.PrimaryKeyColumns.Select(column =>
                $"target.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column)} = source.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column)}"));
        var insertColumns = JoinColumns(StorageProvider.SqlServer, columns.Select(x => x.Name));
        var insertValues = string.Join(
            ", ",
            columns.Select(column => $"source.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)}"));
        var updateColumns = GetUpdateColumns(table, columns);

        var lines = new List<string>
        {
            $"MERGE INTO {RelationalSqlDialect.Quote(StorageProvider.SqlServer, table.TableName)} AS target",
            $"USING (SELECT {sourceColumns}) AS source",
            $"ON {match}"
        };

        if (updateColumns.Count > 0)
        {
            lines.Add(
                "WHEN MATCHED THEN UPDATE SET " + string.Join(
                    ", ",
                    updateColumns.Select(column =>
                        $"target.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)} = source.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)}")));
        }

        lines.Add(
            $"WHEN NOT MATCHED THEN INSERT ({insertColumns}) VALUES ({insertValues});");

        return string.Join(RelationalSqlDialect.NewLine, lines);
    }

    private static string BuildSqlServerBatch(
        RelationalTablePlan table,
        IReadOnlyList<RelationalColumnPlan> columns,
        IReadOnlyList<IReadOnlyList<RelationalCommandParameter>> parameterRows)
    {
        var sourceValues = JoinParameterRows(parameterRows);
        var sourceColumns = JoinColumns(StorageProvider.SqlServer, columns.Select(x => x.Name));
        var match = string.Join(
            " AND ",
            table.PrimaryKeyColumns.Select(column =>
                $"target.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column)} = source.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column)}"));
        var insertColumns = JoinColumns(StorageProvider.SqlServer, columns.Select(x => x.Name));
        var insertValues = string.Join(
            ", ",
            columns.Select(column => $"source.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)}"));
        var updateColumns = GetUpdateColumns(table, columns);

        var lines = new List<string>
        {
            $"MERGE INTO {RelationalSqlDialect.Quote(StorageProvider.SqlServer, table.TableName)} AS target",
            $"USING (VALUES {sourceValues}) AS source ({sourceColumns})",
            $"ON {match}"
        };

        if (updateColumns.Count > 0)
        {
            lines.Add(
                "WHEN MATCHED THEN UPDATE SET " + string.Join(
                    ", ",
                    updateColumns.Select(column =>
                        $"target.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)} = source.{RelationalSqlDialect.Quote(StorageProvider.SqlServer, column.Name)}")));
        }

        lines.Add(
            $"WHEN NOT MATCHED THEN INSERT ({insertColumns}) VALUES ({insertValues});");

        return string.Join(RelationalSqlDialect.NewLine, lines);
    }

    private static IReadOnlyList<RelationalColumnPlan> GetUpdateColumns(
        RelationalTablePlan table,
        IEnumerable<RelationalColumnPlan> columns)
    {
        var primaryKeys = table.PrimaryKeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return columns
            .Where(column => !primaryKeys.Contains(column.Name))
            .ToArray();
    }

    private static string JoinParameterRows(
        IEnumerable<IReadOnlyList<RelationalCommandParameter>> parameterRows)
    {
        return string.Join(
            ", ",
            parameterRows.Select(row =>
                "(" + string.Join(", ", row.Select(parameter => parameter.Placeholder)) + ")"));
    }

    private static string JoinColumns(StorageProvider provider, IEnumerable<string> columns)
    {
        return string.Join(", ", columns.Select(column => RelationalSqlDialect.Quote(provider, column)));
    }
}
