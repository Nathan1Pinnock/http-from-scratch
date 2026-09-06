using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace HttpFromScratch;

/// <summary>
/// Drives one TCP connection: receives bytes into a buffer, carves requests out
/// of it, calls the handler and writes responses back, looping for as long as
/// the client keeps the connection alive. Pipelined requests need no special
/// code, because whatever is left in the buffer after one request is simply the
/// start of the next.
/// </summary>
internal sealed class Connection(Socket socket, HttpServerOptions options, RequestHandler handler, CancellationToken serverStopping)
{
    private static readonly byte[] Continue100 = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
    private int _start; // first byte not yet consumed
    private int _end;   // one past the last byte received

    private int Available => _end - _start;

    private ReadOnlySpan<byte> Unread => _buffer.AsSpan(_start, _end - _start);

    public async Task RunAsync()
    {
        try
        {
            for (int served = 0; served < options.MaxRequestsPerConnection; served++)
            {
                bool last = served + 1 == options.MaxRequestsPerConnection;
                if (!await ServeOneAsync(mustClose: last).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (ConnectionClosedException)
        {
            // The client went away or stopped talking; nothing to do but clean up.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            options.Log?.Invoke($"{socket.RemoteEndPoint} connection error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // already gone
            }

            socket.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }

    /// <summary>Reads, handles and answers one request. Returns false when the connection should close.</summary>
    private async Task<bool> ServeOneAsync(bool mustClose)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        var ct = timeout.Token;

        // An idle connection gets the keep-alive timeout; once a request has started
        // arriving, the client has the (longer) request timeout to finish it.
        bool waitingForFirstByte = Available == 0;
        timeout.CancelAfter(waitingForFirstByte ? options.KeepAliveTimeout : options.RequestReadTimeout);

        int headLength;
        while ((headLength = RequestParser.FindHeadEnd(Unread)) < 0)
        {
            if (Available >= options.MaxHeadBytes)
            {
                await WriteResponseAsync(HttpResponse.Error(431), includeBody: true, "close", ct).ConfigureAwait(false);
                return false;
            }

            await FillAsync(ct).ConfigureAwait(false);
            if (waitingForFirstByte)
            {
                waitingForFirstByte = false;
                timeout.CancelAfter(options.RequestReadTimeout);
            }
        }

        if (headLength > options.MaxHeadBytes)
        {
            // A whole oversized head can arrive in a single receive, so check again.
            await WriteResponseAsync(HttpResponse.Error(431), includeBody: true, "close", ct).ConfigureAwait(false);
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        RequestHead head;
        try
        {
            head = RequestParser.ParseHead(Unread[..headLength]);
        }
        catch (HttpParseException ex)
        {
            await WriteResponseAsync(HttpResponse.Error(ex.StatusCode, ex.Message), includeBody: true, "close", ct).ConfigureAwait(false);
            return false;
        }

        _start += headLength;

        ReadOnlyMemory<byte> body = default;
        try
        {
            body = await ReadBodyAsync(head, ct).ConfigureAwait(false);
        }
        catch (HttpParseException ex)
        {
            await WriteResponseAsync(HttpResponse.Error(ex.StatusCode, ex.Message), includeBody: true, "close", ct).ConfigureAwait(false);
            return false;
        }

        // The request is fully in; the handler and the write are not bound by the read timeout.
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);

        var request = new HttpRequest
        {
            Method = head.Method,
            Target = head.Target,
            Path = head.Path,
            Query = head.Query,
            Version = head.Version,
            Headers = head.Headers,
            Body = body,
        };

        HttpResponse response;
        try
        {
            response = await handler(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            options.Log?.Invoke($"{socket.RemoteEndPoint} handler threw on {head.Method} {head.Target}: {ex}");
            response = HttpResponse.Error(500);
            response.CloseConnection = true;
        }

        // HTTP/1.1 is persistent unless someone says close; HTTP/1.0 is the reverse.
        bool keepAlive = head.Version.Minor >= 1
            ? !head.Headers.HasToken("Connection", "close")
            : head.Headers.HasToken("Connection", "keep-alive");
        if (mustClose || response.CloseConnection || response.Headers.HasToken("Connection", "close"))
        {
            keepAlive = false;
        }

        string? connectionHeader = !keepAlive ? "close" : head.Version.Minor == 0 ? "keep-alive" : null;
        bool includeBody = head.Method != "HEAD";
        await WriteResponseAsync(response, includeBody, connectionHeader, ct).ConfigureAwait(false);

        options.Log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"{socket.RemoteEndPoint} \"{head.Method} {head.Target}\" {response.StatusCode} {(includeBody ? response.Body.Length : 0)}B {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms"));

        return keepAlive;
    }

    private async Task<ReadOnlyMemory<byte>> ReadBodyAsync(RequestHead head, CancellationToken ct)
    {
        if (head.Framing == BodyFraming.None)
        {
            return default;
        }

        if (head.Framing == BodyFraming.ContentLength && head.ContentLength > options.MaxBodyBytes)
        {
            throw new HttpParseException(413, "The body is larger than the server allows.");
        }

        if (head.Version.Minor >= 1 && head.Headers.HasToken("Expect", "100-continue"))
        {
            // The client is holding the body back until we say we want it.
            await SendAllAsync(Continue100, ct).ConfigureAwait(false);
        }

        return head.Framing == BodyFraming.ContentLength
            ? await ReadContentLengthBodyAsync(head.ContentLength, ct).ConfigureAwait(false)
            : await ReadChunkedBodyAsync(ct).ConfigureAwait(false);
    }

    private async Task<ReadOnlyMemory<byte>> ReadContentLengthBodyAsync(long length, CancellationToken ct)
    {
        var body = new byte[length];
        int filled = 0;
        while (filled < body.Length)
        {
            if (Available == 0)
            {
                await FillAsync(ct).ConfigureAwait(false);
            }

            int take = Math.Min(body.Length - filled, Available);
            Unread[..take].CopyTo(body.AsSpan(filled));
            filled += take;
            _start += take;
        }

        return body;
    }

    private async Task<ReadOnlyMemory<byte>> ReadChunkedBodyAsync(CancellationToken ct)
    {
        var decoder = new ChunkedDecoder(options.MaxBodyBytes);
        while (!decoder.IsComplete)
        {
            if (Available > 0)
            {
                int consumed = decoder.Feed(Unread);
                _start += consumed;
                if (consumed > 0)
                {
                    continue;
                }
            }

            await FillAsync(ct).ConfigureAwait(false);
        }

        return decoder.Body;
    }

    /// <summary>Receives at least one more byte into the buffer, compacting or growing it first if needed.</summary>
    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, Available);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        int received;
        try
        {
            received = await socket.ReceiveAsync(_buffer.AsMemory(_end), SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new ConnectionClosedException();
        }
        catch (SocketException)
        {
            throw new ConnectionClosedException();
        }

        if (received == 0)
        {
            throw new ConnectionClosedException(); // orderly shutdown by the client
        }

        _end += received;
    }

    private async Task WriteResponseAsync(HttpResponse response, bool includeBody, string? connectionHeader, CancellationToken ct)
    {
        var output = new ArrayBufferWriter<byte>(512 + (includeBody ? response.Body.Length : 0));
        response.WriteTo(output, includeBody, connectionHeader, options.ServerHeader);
        await SendAllAsync(output.WrittenMemory, ct).ConfigureAwait(false);
    }

    private async Task SendAllAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            int sent;
            try
            {
                sent = await socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new ConnectionClosedException();
            }
            catch (SocketException)
            {
                throw new ConnectionClosedException();
            }

            data = data[sent..];
        }
    }

    /// <summary>The peer closed, reset, or timed out; the connection is finished either way.</summary>
    private sealed class ConnectionClosedException : Exception;
}
