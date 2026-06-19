namespace Platform.ApiClient.Base;

public class TokenCache
{
   public string AccessToken { get; private set; }
    
    public DateTime ExpiresIn { get; private set; }

    public void Update(string token, int expiresIn)
    {
        AccessToken = token;
        ExpiresIn = DateTime.UtcNow.AddSeconds(expiresIn - 100);
    }

    public bool IsValid()
    {
        return !string.IsNullOrEmpty(AccessToken) && ExpiresIn > DateTime.UtcNow;
    }
}