using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.ApiClient.Base;

namespace Sollatek.DataSync.Tests;

public sealed class IdentityServerClientTests
{
    private const string RunLocalKeycloakFlag = "DATASYNC_RUN_KEYCLOAK_INTEGRATION";

    [Fact]
    public void Settings_DefaultToTheStandardRealmTokenEndpoint()
    {
        var settings = new ClientCredentialsSettings(
            "https://id.sollatek.io/",
            "client-id",
            "client-secret");

        Assert.Equal(
            "https://id.sollatek.io/realms/platform/protocol/openid-connect/token",
            settings.TokenEndpoint.AbsoluteUri);
        Assert.DoesNotContain("/connect/", settings.TokenEndpoint.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://id.sollatek.io/")]
    [InlineData("https://user@id.sollatek.io/")]
    [InlineData("https://id.sollatek.io/?debug=true")]
    [InlineData("https://id.sollatek.io/#fragment")]
    public void Settings_RejectUnsafeOauthBaseUri(string oauthUrl)
    {
        Assert.Throws<ArgumentException>(() => new ClientCredentialsSettings(
            oauthUrl,
            "client-id",
            "client-secret"));
    }

    [Fact]
    public void Settings_AllowLoopbackHttpForLocalValidation()
    {
        var settings = new ClientCredentialsSettings(
            "http://127.0.0.1:18081/",
            "client-id",
            "client-secret");

        Assert.Equal(
            "http://127.0.0.1:18081/realms/platform/protocol/openid-connect/token",
            settings.TokenEndpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("", "client-secret")]
    [InlineData("client-id", "")]
    public void Settings_RejectMissingClientCredentials(string clientId, string clientSecret)
    {
        Assert.Throws<ArgumentException>(() => new ClientCredentialsSettings(
            "https://id.sollatek.io/",
            clientId,
            clientSecret));
    }

    [Fact]
    public async Task RequestClientCredentialsTokenAsync_PostsTheUnchangedGrantToTheRealmEndpoint()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"mock-token","expires_in":300,"token_type":"Bearer"}""",
                Encoding.UTF8,
                "application/json")
        });
        var client = new IdentityServerClient(
            new TokenCache(),
            new FixedHttpClientFactory(new HttpClient(handler)),
            new ClientCredentialsSettings(
                "https://id.sollatek.io/",
                "client-id",
                "client-secret"),
            NullLogger<IdentityServerClient>.Instance);

        var token = await client.RequestClientCredentialsTokenAsync(CancellationToken.None);

        Assert.Equal("mock-token", token);
        Assert.NotNull(handler.RequestUri);
        Assert.Equal(
            "/realms/platform/protocol/openid-connect/token",
            handler.RequestUri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Method);
        var form = ParseForm(handler.Content);
        Assert.Equal("client-id", form["client_id"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.Equal("client_credentials", form["grant_type"]);
    }

    [Fact]
    public async Task LocalKeycloak_StandardEndpointIssuesBearerToken()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunLocalKeycloakFlag),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var standardBaseUrl = RequiredEnvironmentVariable("DATASYNC_KEYCLOAK_STANDARD_BASE_URL");
        var clientId = RequiredEnvironmentVariable("DATASYNC_KEYCLOAK_CLIENT_ID");
        var clientSecret = RequiredEnvironmentVariable("DATASYNC_KEYCLOAK_CLIENT_SECRET");

        await AssertCanAcquireTokenAsync(standardBaseUrl, clientId, clientSecret);
    }

    private static async Task AssertCanAcquireTokenAsync(
        string baseUrl,
        string clientId,
        string clientSecret)
    {
        var client = new IdentityServerClient(
            new TokenCache(),
            new TransientHttpClientFactory(),
            new ClientCredentialsSettings(baseUrl, clientId, clientSecret),
            NullLogger<IdentityServerClient>.Instance);

        var token = await client.RequestClientCredentialsTokenAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Environment variable '{name}' is required.");

    private static IReadOnlyDictionary<string, string> ParseForm(string content) =>
        content.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
                part => Uri.UnescapeDataString(part[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TransientHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string Content { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            Content = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
