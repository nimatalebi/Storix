namespace NT.Storix.Core.Monitoring;

/// <summary>Process-wide HttpClient for notifications and health checks.</summary>
public static class SharedHttp
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", $"Storix/{StorixInfo.Version}" } },
    };
}
