using Parquet;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Export;

namespace Sollatek.DataSync.Tests;

public sealed class ParquetFileExportSinkTests
{
    [Fact]
    public async Task WriteAsync_CreatesReadableDailyParquetFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sink = new ParquetFileExportSink(new FileExportOptions { RootPath = directory });

            var path = await sink.WriteAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                [
                    new Dictionary<string, object?>
                    {
                        ["id"] = 1,
                        ["serial"] = "A-001"
                    },
                    new Dictionary<string, object?>
                    {
                        ["id"] = 2,
                        ["serial"] = "A-002"
                    }
                ],
                partNumber: 0,
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.EndsWith("assets/year=2026/month=06/day=18/part-000000.parquet", path.Replace('\\', '/'));

            await using var reader = await ParquetReader.CreateAsync(path);
            Assert.Equal(1, reader.RowGroupCount);
            Assert.Equal(["id", "serial"], reader.Schema.GetDataFields().Select(x => x.Name));

            using var rowGroup = reader.OpenRowGroupReader(0);
            Assert.Equal(2, rowGroup.RowCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_DoesNotCreateFileForEmptyRows()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sink = new ParquetFileExportSink(new FileExportOptions { RootPath = directory });

            var path = await sink.WriteAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                [],
                partNumber: 0,
                CancellationToken.None);

            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_RejectsExistingPartFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sink = new ParquetFileExportSink(new FileExportOptions { RootPath = directory });
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            [
                new Dictionary<string, object?>
                {
                    ["id"] = 1
                }
            ];

            await sink.WriteAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                rows,
                partNumber: 0,
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sink.WriteAsync(
                    "assets",
                    new DateOnly(2026, 6, 18),
                    rows,
                    partNumber: 0,
                    CancellationToken.None));

            Assert.Contains("already exists", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sollatek-datasync-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
