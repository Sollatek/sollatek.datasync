#nullable enable

namespace Sollatek.DataSync.Export;

public interface IExportDateProvider
{
    DateOnly UtcToday { get; }
}

public sealed class SystemExportDateProvider : IExportDateProvider
{
    public DateOnly UtcToday => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
}
