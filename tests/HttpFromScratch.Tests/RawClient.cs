using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace HttpFromScratch.Tests;

/// <summary>
/// A deliberately dumb HTTP client for the tests: it writes exactly the bytes
/// it is given and reads back exactly what the server sends, so the tests can
/// pin down the wire format rather than trust a real client's interpretation.
/// </summary>
internal sealed class RawClient : IDisposable
{
    private readonly Socket _socket;
    private readonly List<byte> _pending = [];

    public RawClient(int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = 5000,
            SendTimeout = 5000,
        };
        _socket.Connect(IPAddress.Loopback, port);
    }

    public void Send(string text) => Send(Encoding.ASCII.GetBytes(text));

    public void Send(byte[] bytes)
    {
        int sent = 0;
        while (sent < bytes.Length)
        {
            sent += _socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
        }
    }

    /// <summary>
    /// Reads one response. The body is as long as Content-Length says, or empty
    /// when there is no Content-Length (interim 100 responses) or when the
    /// request was a HEAD.
    /// </summary>
    public RawResponse ReadResponse(bool headRequest = false)
    {
        int headEnd;
        while ((headEnd = CollectionsMarshal.AsSpan(_pending).IndexOf("\r\n\r\n"u8)) < 0)
        {
            ReceiveMore();
        }

        string head = Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(_pending)[..headEnd]);
        _pending.RemoveRange(0, headEnd + 4);

        string[] lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        int length = !headRequest && headers.TryGetValue("Content-Length", out string? value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : 0;
        while (_pending.Count < length)
        {
            ReceiveMore();
        }

        byte[] body = CollectionsMarshal.AsSpan(_pending)[..length].ToArray();
        _pending.RemoveRange(0, length);
        return new RawResponse(lines[0], headers, body);
    }

    /// <summary>True if the server closes the connection without sending anything else first.</summary>
    public bool WaitForClose()
    {
        try
        {
            return _socket.Receive(new byte[64]) == 0;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.ConnectionReset;
        }
    }

    public void Dispose() => _socket.Dispose();

    private void ReceiveMore()
    {
        byte[] buffer = new byte[8192];
        int received = _socket.Receive(buffer);
        if (received == 0)
        {
            throw new IOException("The server closed the connection before the response was complete.");
        }

        _pending.AddRange(buffer.AsSpan(0, received));
    }
}

internal sealed record RawResponse(string StatusLine, Dictionary<string, string> Headers, byte[] Body)
{
    public int Status => int.Parse(StatusLine.Split(' ')[1], CultureInfo.InvariantCulture);

    public string Text => Encoding.UTF8.GetString(Body);

    public string? this[string header] => Headers.TryGetValue(header, out string? value) ? value : null;
}
