using Sollatek.DataSync.Config;
using Sollatek.DataSync.Export;

namespace Sollatek.DataSync.Tests;

public sealed class DailyExportPathTests
{
    [Fact]
    public void Build_OrganizesByEntityAndDay()
    {
        var path = DailyExportPath.Build(
            "D:/exports",
            "rawDataLocationdata",
            new DateOnly(2026, 6, 18),
            partNumber: 3);

        Assert.EndsWith(
            "rawDataLocationdata/year=2026/month=06/day=18/part-000003.parquet",
            path.Replace('\\', '/'));
    }

    [Fact]
    public void Build_UsesConfiguredMonthlyFolderAndDailyEntityFile()
    {
        var options = new FileExportOptions
        {
            RootPath = "D:/exports",
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

        var path = DailyExportPath.Build(
            options,
            "assets",
            new DateOnly(2026, 2, 2),
            partNumber: 0);

        Assert.EndsWith("202602/Assets_20260202.parquet", path.Replace('\\', '/'));
    }

    [Fact]
    public void Build_UsesConfiguredPartTokenWhenPresent()
    {
        var options = new FileExportOptions
        {
            RootPath = "D:/exports",
            FolderFormat = "yyyyMM",
            FileNameFormat = "{entity}_{date:yyyyMMdd}_{part:000000}.parquet",
            Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["rawDataTemperaturedata"] = new()
                {
                    OutputName = "Temperature"
                }
            }
        };

        var path = DailyExportPath.Build(
            options,
            "rawDataTemperaturedata",
            new DateOnly(2026, 2, 3),
            partNumber: 7);

        Assert.EndsWith("202602/Temperature_20260203_000007.parquet", path.Replace('\\', '/'));
    }

    [Fact]
    public void Build_UsesConfiguredFormatToken()
    {
        var options = new FileExportOptions
        {
            RootPath = "D:/exports",
            Format = "parquet",
            FolderFormat = "yyyyMM",
            FileNameFormat = "{entity}_{date:yyyyMMdd}.{format}",
            Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["assets"] = new()
                {
                    OutputName = "Assets"
                }
            }
        };

        var path = DailyExportPath.Build(
            options,
            "assets",
            new DateOnly(2026, 3, 1),
            partNumber: 0);

        Assert.EndsWith("202603/Assets_20260301.parquet", path.Replace('\\', '/'));
    }

    [Fact]
    public void BuildRelative_RendersSlashSeparatedObjectPathWithoutRoot()
    {
        var options = new FileExportOptions
        {
            RootPath = "exports",
            Format = "parquet",
            FolderFormat = "yyyyMM",
            FileNameFormat = "{entity}_{date:yyyyMMdd}.{format}",
            Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["assets"] = new()
                {
                    OutputName = "Assets"
                }
            }
        };

        var path = DailyExportPath.BuildRelative(
            options,
            "assets",
            new DateOnly(2026, 3, 1),
            partNumber: 0);

        Assert.Equal("202603/Assets_20260301.parquet", path);
    }

    [Theory]
    [InlineData("../assets")]
    [InlineData("assets/test")]
    [InlineData("assets\\test")]
    public void Build_RejectsUnsafeEntityKeys(string entityKey)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DailyExportPath.Build(
                "D:/exports",
                entityKey,
                new DateOnly(2026, 6, 18),
                partNumber: 0));

        Assert.Contains("safe export folder name", exception.Message);
    }

    [Fact]
    public void Build_RejectsNegativePartNumber()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DailyExportPath.Build(
                "D:/exports",
                "assets",
                new DateOnly(2026, 6, 18),
                partNumber: -1));

        Assert.Contains("partNumber must be greater than or equal to 0", exception.Message);
    }
}
