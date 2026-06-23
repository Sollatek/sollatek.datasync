using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Tests;

public sealed class StateOptionsTests
{
    [Fact]
    public void StateOptions_DefaultsToFilesystemState()
    {
        var options = StateOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(StateProvider.Filesystem, options.Provider);
        Assert.Equal("_state", options.RootPath);
    }

    [Fact]
    public void StateOptions_ReadsAzureBlobStateRoot()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["State:provider"] = "azureBlobStorage",
                ["State:rootPath"] = "jobs/datasync-state"
            })
            .Build();

        var options = StateOptions.FromConfiguration(configuration);

        Assert.Equal(StateProvider.AzureBlob, options.Provider);
        Assert.Equal("jobs/datasync-state", options.RootPath);
    }

    [Fact]
    public void StateOptions_RejectsUnknownProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["State:provider"] = "tableStorage"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StateOptions.FromConfiguration(configuration));

        Assert.Contains("Unknown state provider 'tableStorage'", exception.Message);
    }
}
