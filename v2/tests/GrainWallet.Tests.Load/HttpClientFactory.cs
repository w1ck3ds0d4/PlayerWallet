using System.Net;

namespace GrainWallet.Tests.Load;

internal static class HttpClientFactory
{
    /// <summary>
    /// Single shared <see cref="HttpClient"/> for the NBomber worker pool, tuned for sustained 1000 rps:
    /// <see cref="SocketsHttpHandler.MaxConnectionsPerServer"/>=256, 2 min connection lifetime, HTTP/2 with HTTP/1.1 fallback.
    /// Every scenario must dispose the response and drain its body to <see cref="Stream.Null"/> or the pool stalls.
    /// </summary>
    public static HttpClient Create(string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 256,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
