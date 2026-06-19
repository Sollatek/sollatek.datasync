using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Tests;

public sealed class SyncPlanConfigurationTests
{
    [Fact]
    public void GetSyncPlan_ReadsArrayConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0"] = "assets",
                ["SyncPlan:1"] = "customers"
            })
            .Build();

        Assert.Equal(["assets", "customers"], configuration.GetSyncPlan());
    }

    [Fact]
    public void GetSyncPlan_UsesEntitiesStringAsFullOverride()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0"] = "customers",
                ["SyncPlan:1"] = "devices",
                ["SyncPlan:entities"] = "assets; rawDataLocationdata"
            })
            .Build();

        Assert.Equal(["assets", "rawDataLocationdata"], configuration.GetSyncPlan());
    }

    [Fact]
    public void GetSyncPlan_ReadsMixedStringAndPolicyEntries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0"] = "customers",
                ["SyncPlan:1:rawdata/dooropeningdata:dataMode"] = "differential",
                ["SyncPlan:1:rawdata/dooropeningdata:partitionDate"] = "exportRunDay",
                ["SyncPlan:2"] = "assets"
            })
            .Build();

        Assert.Equal(["customers", "rawdata/dooropeningdata", "assets"], configuration.GetSyncPlan());
    }

    [Fact]
    public void GetSyncPlan_RejectsPolicyEntryWithMultipleEntityKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0:assets:dataMode"] = "full",
                ["SyncPlan:0:rawdata/dooropeningdata:dataMode"] = "differential"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.GetSyncPlan());

        Assert.Contains("SyncPlan object entries must contain exactly one entity key", exception.Message);
    }

    [Fact]
    public void DefaultAppsettings_PreservesLegacyEntitiesWithInitialFullLoads()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(FindProjectFile("Sollatek.DataSync", "appsettings.json"))
            .Build();

        var expectedEntities = new[]
        {
            "customers",
            "devices",
            "pointsOfInterest",
            "assets",
            "rawDataLocationdata",
            "rawDataExtrainfodata",
            "rawDataTemperaturedata",
            "rawDataDooropeningdata"
        };

        Assert.Equal(expectedEntities, configuration.GetSyncPlan());

        var options = SyncPlanOptions.FromConfiguration(configuration);
        foreach (var entity in expectedEntities)
        {
            Assert.Equal(SyncInitialDataMode.Full, options.GetEntityOptions(entity).Initial);
        }
    }

    private static string FindProjectFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find project file '{Path.Combine(pathParts)}'.");
    }
}
