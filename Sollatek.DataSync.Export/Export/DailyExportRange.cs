#nullable enable

using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Export;

public sealed record DailyExportRange(DateOnly Day, SyncDateRange Range);
