using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.ApiClient.Models;

namespace Sollatek.DataSync.Tests;

public sealed class SyncPagingOptionsTests
{
    [Fact]
    public void GetMaxPageSize_UsesConfiguredValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:maxPageSize"] = "250"
            })
            .Build();

        Assert.Equal(250, configuration.GetMaxPageSize());
    }

    [Fact]
    public void GetMaxPageSize_DefaultsTo500()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(500, configuration.GetMaxPageSize());
    }

    [Fact]
    public void GetMaxPageSize_RejectsNonPositiveValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:maxPageSize"] = "0"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.GetMaxPageSize());

        Assert.Contains("Sync:maxPageSize must be greater than 0", exception.Message);
    }

    [Fact]
    public async Task DoForAll_UsesConfiguredMaxPageSize()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:maxPageSize"] = "125"
            })
            .Build();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .BuildServiceProvider();
        var requestedPages = new List<(int? Top, int? Skip)>();

        await Helper.DoForAll<int>(
            serviceProvider,
            (top, skip) =>
            {
                requestedPages.Add((top, skip));
                return Task.FromResult(CreateResponse([1, 2, 3]));
            },
            _ => Task.CompletedTask);

        Assert.Equal([(125, 0)], requestedPages);
    }

    private static ApiResponse<ICollection<int>> CreateResponse(ICollection<int> items)
    {
        return new ApiResponse<ICollection<int>>(
            200,
            new Dictionary<string, IEnumerable<string>>(),
            items);
    }
}
