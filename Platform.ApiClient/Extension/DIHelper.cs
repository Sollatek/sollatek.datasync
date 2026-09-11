using Microsoft.Extensions.DependencyInjection;
using Platform.ApiClient.Base;

namespace Platform.ApiClient.Extension;

public static class DiHelper
{
    public static IServiceCollection AddApiClients(
        this IServiceCollection services,
        string apiUrl,
        string oauthUrl,
        string clientKey,
        string clientSecret)
    {
        services.AddSingleton(new ClientCredentialsSettings(
            oauthUrl,
            clientKey,
            clientSecret));
        services.AddSingleton<TokenCache>();
        services.AddTransient<ProtectedApiBearerTokenHandler>();
        services.AddSingleton<IClientSettings>(new ClientSettings(apiUrl));
        services.AddTransient<IIdentityServerClient, IdentityServerClient>();

        AddProtectedClient<DevicesClient>(services, apiUrl);
        AddProtectedClient<CustomersClient>(services, apiUrl);
        AddProtectedClient<CoolersClient>(services, apiUrl);
        AddProtectedClient<PointsOfInterestClient>(services, apiUrl);
        AddProtectedClient<RawDataClient>(services, apiUrl);
        return services;
    }

    private static void AddProtectedClient<TClient>(IServiceCollection services, string apiUrl)
        where TClient : class
    {
        services.AddHttpClient<TClient>(client =>
        {
            client.BaseAddress = new Uri(apiUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddHttpMessageHandler<ProtectedApiBearerTokenHandler>();
    }
}
