using Sollatek.DataSync.AzureBlob;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.AzureBlob.Tests;

public sealed class AzureBlobContainerClientFactoryTests
{
    [Fact]
    public void Create_BuildsDefaultCredentialClientFromAccountNameAndContainer()
    {
        var client = AzureBlobContainerClientFactory.Create(new StorageOptions
        {
            Provider = StorageProvider.AzureBlob,
            BlobAuthentication = AzureBlobAuthenticationMode.DefaultAzureCredential,
            AccountName = "storageacct",
            ContainerName = "exports"
        });

        Assert.Equal(new Uri("https://storageacct.blob.core.windows.net/exports"), client.Uri);
    }

    [Fact]
    public void Create_BuildsContainerUriClient()
    {
        var client = AzureBlobContainerClientFactory.Create(new StorageOptions
        {
            Provider = StorageProvider.AzureBlob,
            BlobAuthentication = AzureBlobAuthenticationMode.ContainerUri,
            ContainerUri = "https://storageacct.blob.core.windows.net/exports"
        });

        Assert.Equal(new Uri("https://storageacct.blob.core.windows.net/exports"), client.Uri);
    }
}
