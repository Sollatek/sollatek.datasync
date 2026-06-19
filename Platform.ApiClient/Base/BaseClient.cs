using System.Text;

// ReSharper disable once CheckNamespace
namespace Platform.ApiClient;

public class BaseClient
{
    private IClientSettings _settings;
    public BaseClient(IClientSettings settings)
    {
        _settings = settings;

    }

    public string BaseUrl
    {
        get
        {
            return _settings.BaseUrl;
        }
    }

    protected Task PrepareRequestAsync(HttpClient client_,HttpRequestMessage request_,StringBuilder urlBuilder_,CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    protected Task PrepareRequestAsync(HttpClient client_,HttpRequestMessage request_,string url,CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected  Task ProcessResponseAsync(HttpClient client_,HttpResponseMessage response_,CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}