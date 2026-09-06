using System.Net;

namespace HttpFromScratch;

public sealed class HttpServerOptions
{
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>The port to listen on; 0 lets the OS pick a free one, which the tests rely on.</summary>
    public int Port { get; set; }

    /// <summary>Largest request head (request line plus headers) we will buffer before answering 431.</summary>
    public int MaxHeadBytes { get; set; } = 16 * 1024;

    /// <summary>Largest request body we will accept before answering 413.</summary>
    public long MaxBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>How long an idle connection may sit between requests before we close it.</summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a client has to finish sending a request once it has started.</summary>
    public TimeSpan RequestReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>After this many requests on one connection we close it, so no client can hold a connection forever.</summary>
    public int MaxRequestsPerConnection { get; set; } = 1000;

    public string ServerHeader { get; set; } = "http-from-scratch/1.0";

    /// <summary>Receives one line per request served and one per connection-level error.</summary>
    public Action<string>? Log { get; set; }
}
