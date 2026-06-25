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
    public async Task WriteAsync_UsesConfiguredMonthlyFolderAndDailyEntityFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var options = new FileExportOptions
            {
                RootPath = directory,
                FolderFormat = "yyyyMM",
                FileNameFormat = "{entity}_{date:yyyyMMdd}.parquet",
                Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["assets"] = new()
                    {
                        OutputName = "Assets"
                    }
                }
            };
            var sink = new ParquetFileExportSink(options);

            var path = await sink.WriteAsync(
                "assets",
                new DateOnly(2026, 2, 2),
                [
                    new Dictionary<string, object?>
                    {
                        ["id"] = 1,
                        ["serial"] = "A-001"
                    }
                ],
                partNumber: 0,
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.EndsWith("202602/Assets_20260202.parquet", path!.Replace('\\', '/'));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_CreatesCsvFileWhenConfigured()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sink = new ParquetFileExportSink(new FileExportOptions
            {
                RootPath = directory,
                Format = "csv"
            });

            var path = await sink.WriteAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                [
                    new Dictionary<string, object?>
                    {
                        ["id"] = 1,
                        ["serial"] = "A,001"
                    }
                ],
                partNumber: 0,
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.EndsWith("assets/year=2026/month=06/day=18/part-000000.csv", path!.Replace('\\', '/'));
            Assert.Equal(
                ["id,serial", "1,\"A,001\""],
                await File.ReadAllLinesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CopyAsync_CopiesDownloadedPortalFileToConfiguredPath()
    {
        var directory = CreateTempDirectory();
        var sourceDirectory = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(sourceDirectory, "download.xlsx");
            await File.WriteAllTextAsync(sourcePath, "xlsx bytes");
            var sink = new ParquetFileExportSink(new FileExportOptions
            {
                RootPath = directory,
                Format = "xlsx",
                FolderFormat = "yyyyMM",
                FileNameFormat = "{entity}_{date:yyyyMMdd}.{format}",
                Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["assets"] = new()
                    {
                        OutputName = "Assets"
                    }
                }
            });

            var path = await sink.CopyAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                sourcePath,
                partNumber: 0,
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.EndsWith("202606/Assets_20260618.xlsx", path.Replace('\\', '/'));
            Assert.Equal("xlsx bytes", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(sourceDirectory, recursive: true);
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
    public async Task WriteAsync_ReplacesExistingPartFileByDefault()
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

            var path = await sink.WriteAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                [
                    new Dictionary<string, object?>
                    {
                        ["id"] = 2
                    },
                    new Dictionary<string, object?>
                    {
                        ["id"] = 3
                    }
                ],
                partNumber: 0,
                CancellationToken.None);

            await using var reader = await ParquetReader.CreateAsync(path!);
            using var rowGroup = reader.OpenRowGroupReader(0);
            Assert.Equal(2, rowGroup.RowCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_RejectsExistingPartFileWhenReplaceExistingIsDisabled()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sink = new ParquetFileExportSink(new FileExportOptions
            {
                RootPath = directory,
                ReplaceExisting = false
            });
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

    [Fact]
    public async Task CopyAsync_ReplacesExistingPortalFileByDefault()
    {
        var directory = CreateTempDirectory();
        var sourceDirectory = CreateTempDirectory();
        try
        {
            var firstSourcePath = Path.Combine(sourceDirectory, "first.xlsx");
            var secondSourcePath = Path.Combine(sourceDirectory, "second.xlsx");
            await File.WriteAllTextAsync(firstSourcePath, "first");
            await File.WriteAllTextAsync(secondSourcePath, "second");
            var sink = new ParquetFileExportSink(new FileExportOptions
            {
                RootPath = directory,
                Format = "xlsx"
            });

            var path = await sink.CopyAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                firstSourcePath,
                partNumber: 0,
                CancellationToken.None);
            await sink.CopyAsync(
                "assets",
                new DateOnly(2026, 6, 18),
                secondSourcePath,
                partNumber: 0,
                CancellationToken.None);

            Assert.Equal("second", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(sourceDirectory, recursive: true);
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
