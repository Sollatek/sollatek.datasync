namespace Platform.ApiClient.Base;

public class ClientCredentialsSettings
{
    public string OauthUrl { get; set; }
    public string ClientKey { get; set; }
    public string ClientSecret { get; set; }

    public ClientCredentialsSettings(string oauthUrl, string clientKey, string clientSecret)
    {
        OauthUrl = oauthUrl;
        ClientKey = clientKey;
        ClientSecret = clientSecret;
    }
}