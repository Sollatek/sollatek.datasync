#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public interface IRelationalSyncSink
{
    Task PrepareAsync(
        SchemaManifest manifest,
        IReadOnlyList<RelationalTablePlan> tables,
        CancellationToken cancellationToken);

    Task WriteAsync(
        RelationalTablePlan table,
        IAsyncEnumerable<RelationalRow> rows,
        CancellationToken cancellationToken);
}
