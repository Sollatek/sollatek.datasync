using System.Net.Http.Headers;

namespace Platform.ApiClient.Base;

public class ProtectedApiBearerTokenHandler : DelegatingHandler
{
    private readonly IIdentityServerClient _identityServerClient;
    private readonly TokenCache _tokenCache;

    public ProtectedApiBearerTokenHandler(TokenCache tokenCache,
        IIdentityServerClient identityServerClient)
    {
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
        _identityServerClient = identityServerClient
                                ?? throw new ArgumentNullException(nameof(identityServerClient));
        
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        
        // request the access token
        var accessToken = _tokenCache.IsValid()
            ? _tokenCache.AccessToken
            : await _identityServerClient.RequestClientCredentialsTokenAsync(cancellationToken);

        // set the bearer token to the outgoing request
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Proceed calling the inner handler, that will actually send the request
        // to our protected api
        return await base.SendAsync(request, cancellationToken);
    }
}