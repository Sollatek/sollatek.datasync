using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Tests;

public sealed class SchemaContractOptionsTests
{
    [Fact]
    public void FromConfiguration_DefaultsVersionToOne()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = SchemaContractOptions.FromConfiguration(configuration);

        Assert.Equal("1", options.Version);
    }

    [Fact]
    public void FromConfiguration_ReadsAndTrimsVersion()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Schema:version"] = " release-2026-07 "
            })
            .Build();

        var options = SchemaContractOptions.FromConfiguration(configuration);

        Assert.Equal("release-2026-07", options.Version);
    }
}
