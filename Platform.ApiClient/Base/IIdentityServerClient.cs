namespace Platform.ApiClient.Base;

public interface IIdentityServerClient
{
    Task<string> RequestClientCredentialsTokenAsync(CancellationToken cancellationToken);
}