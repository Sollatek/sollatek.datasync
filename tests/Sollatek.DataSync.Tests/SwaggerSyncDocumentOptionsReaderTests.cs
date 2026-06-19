using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SwaggerSyncDocumentOptionsReaderTests
{
    [Fact]
    public void FromConfiguration_UsesConfiguredSwaggerDocumentsInOrder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "data-v1",
                ["SwaggerDocuments:0:url"] = "https://api.sollatek.io/swagger/data-v1/swagger.json",
                ["SwaggerDocuments:1:name"] = "portal-v1",
                ["SwaggerDocuments:1:url"] = "https://api.sollatek.io/swagger/portal-v1/swagger.json"
            })
            .Build();

        var documents = SwaggerSyncDocumentOptionsReader.FromConfiguration(configuration);

        Assert.Collection(
            documents,
            first =>
            {
                Assert.Equal("data-v1", first.Name);
                Assert.Equal("https://api.sollatek.io/swagger/data-v1/swagger.json", first.Url);
            },
            second =>
            {
                Assert.Equal("portal-v1", second.Name);
                Assert.Equal("https://api.sollatek.io/swagger/portal-v1/swagger.json", second.Url);
            });
    }

    [Fact]
    public void FromConfiguration_DefaultsToDataAndPortalDocumentsFromApiUrl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:apiUrl"] = "https://api.sollatek.io/"
            })
            .Build();

        var documents = SwaggerSyncDocumentOptionsReader.FromConfiguration(configuration);

        Assert.Equal(
            [
                "https://api.sollatek.io/swagger/data-v1/swagger.json",
                "https://api.sollatek.io/swagger/portal-v1/swagger.json"
            ],
            documents.Select(x => x.Url));
        Assert.Equal(["data-v1", "portal-v1"], documents.Select(x => x.Name));
    }

    [Fact]
    public void FromConfiguration_RejectsDocumentWithoutName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:url"] = "https://api.sollatek.io/swagger/data-v1/swagger.json"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncDocumentOptionsReader.FromConfiguration(configuration));

        Assert.Contains("SwaggerDocuments[0].name is required", exception.Message);
    }

    [Fact]
    public void FromConfiguration_RejectsDocumentWithoutUrl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "data-v1"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SwaggerSyncDocumentOptionsReader.FromConfiguration(configuration));

        Assert.Contains("SwaggerDocuments[0].url is required", exception.Message);
    }
}
