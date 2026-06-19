#nullable enable

using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalForeignKeyCommandBuilder
{
    public static RelationalCommand BuildExists(
        StorageProvider provider,
        RelationalTablePlan table,
        RelationalForeignKeyPlan foreignKey)
    {
        EnsureRelationalProvider(provider);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(foreignKey);

        var constraintName = GetConstraintName(table, foreignKey);
        var sql = provider switch
        {
            StorageProvider.Postgres => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM pg_constraint",
                $"WHERE conname = {Placeholder(provider, 0)}",
                "  AND contype = 'f';"),
            StorageProvider.MySql => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM information_schema.TABLE_CONSTRAINTS",
                "WHERE CONSTRAINT_SCHEMA = DATABASE()",
                $"  AND TABLE_NAME = {Placeholder(provider, 0)}",
                $"  AND CONSTRAINT_NAME = {Placeholder(provider, 1)}",
                "  AND CONSTRAINT_TYPE = 'FOREIGN KEY';"),
            StorageProvider.SqlServer => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM sys.foreign_keys",
                $"WHERE name = {Placeholder(provider, 0)};"),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        var values = provider == StorageProvider.MySql
            ? new object?[] { table.TableName, constraintName }
            : [constraintName];

        return new RelationalCommand(sql, BuildParameters(provider, values));
    }

    public static RelationalCommand BuildAdd(
        StorageProvider provider,
        RelationalTablePlan table,
        RelationalForeignKeyPlan foreignKey)
    {
        EnsureRelationalProvider(provider);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(foreignKey);

        if (!foreignKey.IsNullable)
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.ColumnName}' on '{table.TableName}' must be nullable for DataSync flexible schema application.");
        }

        var onDelete = foreignKey.OnDelete switch
        {
            RelationalForeignKeyDeleteBehavior.NoAction => "NO ACTION",
            RelationalForeignKeyDeleteBehavior.SetNull => "SET NULL",
            _ => throw new InvalidOperationException(
                $"Unsupported foreign key delete behavior '{foreignKey.OnDelete}'.")
        };
        var constraintName = GetConstraintName(table, foreignKey);
        var alterTablePrefix = provider == StorageProvider.SqlServer
            ? $"ALTER TABLE {RelationalSqlDialect.Quote(provider, table.TableName)} WITH NOCHECK"
            : $"ALTER TABLE {RelationalSqlDialect.Quote(provider, table.TableName)}";

        var sql = string.Join(
            RelationalSqlDialect.NewLine,
            alterTablePrefix,
            $"ADD CONSTRAINT {RelationalSqlDialect.Quote(provider, constraintName)}",
            $"FOREIGN KEY ({RelationalSqlDialect.Quote(provider, foreignKey.ColumnName)})",
            $"REFERENCES {RelationalSqlDialect.Quote(provider, foreignKey.TargetTable)} ({RelationalSqlDialect.Quote(provider, foreignKey.TargetColumn)})",
            $"ON DELETE {onDelete};");

        return new RelationalCommand(sql, []);
    }

    public static string GetConstraintName(
        RelationalTablePlan table,
        RelationalForeignKeyPlan foreignKey)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(foreignKey);

        return $"fk_{table.TableName}_{foreignKey.ColumnName}";
    }

    private static IReadOnlyList<RelationalCommandParameter> BuildParameters(
        StorageProvider provider,
        IReadOnlyList<object?> values)
    {
        return values
            .Select((value, index) => new RelationalCommandParameter(
                $"p{index.ToString(CultureInfo.InvariantCulture)}",
                Placeholder(provider, index),
                value))
            .ToArray();
    }

    private static string Placeholder(StorageProvider provider, int index)
    {
        return provider switch
        {
            StorageProvider.Postgres => $"${index + 1}",
            StorageProvider.MySql or StorageProvider.SqlServer => $"@p{index.ToString(CultureInfo.InvariantCulture)}",
            _ => throw new InvalidOperationException($"Storage provider '{provider}' is not a relational provider.")
        };
    }

    private static void EnsureRelationalProvider(StorageProvider provider)
    {
        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }
    }
}
