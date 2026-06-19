namespace Platform.ApiClient.Base;

public class ClientSettings:IClientSettings
{
    public ClientSettings(string baseUrl)
    {
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; init; }
    
}