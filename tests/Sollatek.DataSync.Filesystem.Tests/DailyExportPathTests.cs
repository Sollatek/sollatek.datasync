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
