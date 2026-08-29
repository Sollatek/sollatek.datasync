using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Platform.ApiClient.Models;

namespace Platform.ApiClient.Base;

public class IdentityServerClient : IIdentityServerClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClientCredentialsSettings _settings;
    private readonly ILogger<IdentityServerClient> _logger;
    private readonly TokenCache _tokenCache;

    public IdentityServerClient(TokenCache tokenCache,
        IHttpClientFactory httpClientFactory,
        ClientCredentialsSettings settings,
        ILogger<IdentityServerClient> logger)
    {
        _tokenCache = tokenCache?? throw new ArgumentNullException(nameof(tokenCache));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> RequestClientCredentialsTokenAsync(CancellationToken cancellationToken)
    {
        using (var httpClient = _httpClientFactory.CreateClient())
        {
            int statusCode = 400;
            try
            {
                using (var request_ = new HttpRequestMessage(HttpMethod.Post,
                           _settings.TokenEndpoint))
                {
                  

                   
                    request_.Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("client_id", _settings.ClientKey),
                        new KeyValuePair<string, string>("client_secret", _settings.ClientSecret),
                        new KeyValuePair<string, string>("grant_type", "client_credentials")
                    });
                    using (var response_ = await httpClient
                               .SendAsync(request_, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                          )
                    {
                        statusCode = (int)response_.StatusCode;
                        if (statusCode == 200)
                        {
                            var jsonString = await response_.Content.ReadAsStringAsync(cancellationToken);
                           
                                var typedBody = JsonConvert.DeserializeObject<BearerTokenResponse>(jsonString);
                                if (typedBody.Error != null || typedBody.TokenType.ToLower() != "bearer")
                                {
                                    throw new AuthenticationException(
                                        "Unable to retrieve an access token. Please verify that your application secret is correct.");
                                }
                                _logger.LogInformation("--- Retrieved valid token");
                                _tokenCache.Update(typedBody.AccessToken, typedBody.ExpiresIn);
                                return typedBody.AccessToken;
                            
                        }
                        else
                        {
                            var jsonString = await response_.Content.ReadAsStringAsync(cancellationToken);
                        }
                    }
                }
            }
            catch (JsonException exception)
            {
                
                var message = "Could not deserialize the response body stream as " +
                              typeof(BearerTokenResponse).FullName + ".";
                throw new ApiException(message, statusCode, string.Empty, null, exception);
            }
            catch (Exception exception)
            {
               
                throw new ApiException(exception.Message, statusCode, string.Empty, null, exception);
            }
        }

        return null;
    }


    public class BearerTokenResponse
    {
        [JsonProperty("refresh_token")] public string RefreshToken { get; set; }
        [JsonProperty("access_token")] public string AccessToken { get; set; }
        [JsonProperty("expires_in")] public int ExpiresIn { get; set; }
        [JsonProperty("token_type")] public string TokenType { get; set; }
        public string Error { get; set; }
    }
}
