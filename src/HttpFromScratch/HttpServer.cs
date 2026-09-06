using System.Net;
using System.Net.Sockets;

namespace HttpFromScratch;

/// <summary>Produces the response for one request. Runs on the connection's task, so it may await freely.</summary>
public delegate ValueTask<HttpResponse> RequestHandler(HttpRequest request);

/// <summary>
/// Listens on a TCP socket and runs one <see cref="Connection"/> per accepted
/// client. Every connection is an async task rather than a thread, so many
/// idle keep-alive connections cost almost nothing.
/// </summary>
public sealed class HttpServer : IAsyncDisposable
{
    private readonly HttpServerOptions _options;
    private readonly RequestHandler _handler;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _acceptLoop;

    public HttpServer(RequestHandler handler, HttpServerOptions? options = null)
    {
        _handler = handler;
        _options = options ?? new HttpServerOptions();
        _listener = new Socket(_options.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (!OperatingSystem.IsWindows())
        {
            // Lets us rebind straight away after a restart while old connections sit in TIME_WAIT.
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
    }

    /// <summary>The address actually bound, including the real port when 0 was requested.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndPoint!;

    public int Port => EndPoint.Port;

    /// <summary>Binds, listens and starts accepting in the background.</summary>
    public void Start()
    {
        _listener.Bind(new IPEndPoint(_options.Address, _options.Port));
        _listener.Listen(backlog: 512);
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _options.Log?.Invoke($"accept failed: {ex.SocketErrorCode}");
                continue;
            }

            client.NoDelay = true; // small responses should not wait for Nagle's algorithm
            var connection = new Connection(client, _options, _handler, _stopping.Token);
            Task run = connection.RunAsync();
            lock (_connections)
            {
                _connections.Add(run);
            }

            _ = run.ContinueWith(
                t =>
                {
                    lock (_connections)
                    {
                        _connections.Remove(t);
                    }
                },
                TaskScheduler.Default);
        }
    }

    /// <summary>Stops accepting, tells every connection to finish, and waits briefly for them.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        Task[] pending;
        lock (_connections)
        {
            pending = [.. _connections];
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _options.Log?.Invoke($"{pending.Length} connection(s) did not finish in time");
        }

        _stopping.Dispose();
    }
}
