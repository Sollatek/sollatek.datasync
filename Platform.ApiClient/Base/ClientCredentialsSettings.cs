namespace Platform.ApiClient.Base;

public class ClientCredentialsSettings
{
    public const string StandardTokenEndpointPath =
        "/realms/platform/protocol/openid-connect/token";

    public string OauthUrl { get; set; }
    public string ClientKey { get; set; }
    public string ClientSecret { get; set; }
    public Uri TokenEndpoint { get; }

    public ClientCredentialsSettings(
        string oauthUrl,
        string clientKey,
        string clientSecret)
    {
        if (!Uri.TryCreate(oauthUrl, UriKind.Absolute, out var oauthBaseUri) ||
            (oauthBaseUri.Scheme != Uri.UriSchemeHttps &&
             !(oauthBaseUri.Scheme == Uri.UriSchemeHttp && oauthBaseUri.IsLoopback)) ||
            !string.IsNullOrEmpty(oauthBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(oauthBaseUri.Query) ||
            !string.IsNullOrEmpty(oauthBaseUri.Fragment))
        {
            throw new ArgumentException(
                "The OAuth URL must be an HTTPS absolute URI without user info, a query, or a fragment. Loopback HTTP is allowed for local tests.",
                nameof(oauthUrl));
        }

        if (string.IsNullOrWhiteSpace(clientKey))
        {
            throw new ArgumentException("The OAuth client identifier is required.", nameof(clientKey));
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new ArgumentException("The OAuth client secret is required.", nameof(clientSecret));
        }

        OauthUrl = oauthUrl;
        ClientKey = clientKey;
        ClientSecret = clientSecret;
        TokenEndpoint = new Uri(oauthBaseUri, StandardTokenEndpointPath);
    }
}
