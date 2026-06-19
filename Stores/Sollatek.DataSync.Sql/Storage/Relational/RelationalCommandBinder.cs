#nullable enable

using System.Data.Common;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalCommandBinder
{
    public static DbCommand CreateCommand(
        StorageProvider provider,
        DbConnection connection,
        DbTransaction? transaction,
        RelationalCommand command)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(command);

        var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command.Sql;
        dbCommand.Transaction = transaction;

        foreach (var parameter in command.Parameters)
        {
            var dbParameter = dbCommand.CreateParameter();
            dbParameter.ParameterName = GetParameterName(provider, parameter);
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            dbCommand.Parameters.Add(dbParameter);
        }

        return dbCommand;
    }

    private static string GetParameterName(
        StorageProvider provider,
        RelationalCommandParameter parameter)
    {
        return provider switch
        {
            StorageProvider.SqlServer => parameter.Placeholder,
            StorageProvider.Postgres when parameter.Placeholder.StartsWith('$') => string.Empty,
            StorageProvider.Postgres => parameter.Name,
            StorageProvider.MySql => parameter.Name,
            _ => throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.")
        };
    }
}
