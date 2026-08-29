namespace Platform.ApiClient.Base;

public class ClientCredentialsSettings
{
    public const string StandardTokenEndpointPath =
        "/realms/platform/protocol/openid-connect/token";
    public const string LegacyTokenEndpointPath = "/connect/token";

    public string OauthUrl { get; set; }
    public string ClientKey { get; set; }
    public string ClientSecret { get; set; }
    public Uri TokenEndpoint { get; }

    public ClientCredentialsSettings(
        string oauthUrl,
        string clientKey,
        string clientSecret,
        string tokenEndpointPath = StandardTokenEndpointPath)
    {
        var effectivePath = string.IsNullOrWhiteSpace(tokenEndpointPath)
            ? StandardTokenEndpointPath
            : tokenEndpointPath.Trim();
        if (effectivePath != StandardTokenEndpointPath &&
            effectivePath != LegacyTokenEndpointPath)
        {
            throw new ArgumentException(
                $"The token endpoint path must be '{StandardTokenEndpointPath}' or the explicit rollback path '{LegacyTokenEndpointPath}'.",
                nameof(tokenEndpointPath));
        }

        if (!Uri.TryCreate(oauthUrl?.TrimEnd('/') + effectivePath, UriKind.Absolute, out var tokenEndpoint) ||
            (!string.Equals(tokenEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(tokenEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(tokenEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(tokenEndpoint.Query) ||
            !string.IsNullOrEmpty(tokenEndpoint.Fragment))
        {
            throw new ArgumentException("The OAuth base URL must produce an absolute HTTP(S) token endpoint.",
                nameof(oauthUrl));
        }

        OauthUrl = oauthUrl;
        ClientKey = clientKey;
        ClientSecret = clientSecret;
        TokenEndpoint = tokenEndpoint;
    }
}
