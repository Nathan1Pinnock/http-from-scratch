using System.Buffers;
using System.Globalization;
using System.Text;

namespace HttpFromScratch;

/// <summary>A response to send back: status, headers and an in-memory body.</summary>
public sealed class HttpResponse
{
    public int StatusCode { get; set; } = 200;

    /// <summary>Overrides the standard reason phrase for <see cref="StatusCode"/> when set.</summary>
    public string? ReasonPhrase { get; set; }

    public HttpHeaders Headers { get; } = new();

    public ReadOnlyMemory<byte> Body { get; set; }

    /// <summary>Set by a handler that wants the connection closed once this response is sent.</summary>
    public bool CloseConnection { get; set; }

    public static HttpResponse Status(int code) => new() { StatusCode = code };

    public static HttpResponse Text(string text, int status = 200) =>
        Bytes(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", status);

    public static HttpResponse Html(string html, int status = 200) =>
        Bytes(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", status);

    public static HttpResponse Bytes(ReadOnlyMemory<byte> body, string contentType, int status = 200)
    {
        var response = new HttpResponse { StatusCode = status, Body = body };
        response.Headers.Set("Content-Type", contentType);
        return response;
    }

    public static HttpResponse NotFound(string message = "Not Found") => Text(message + "\n", 404);

    public static HttpResponse Error(int status, string? message = null) =>
        Text((message ?? HttpStatus.ReasonPhrase(status)) + "\n", status);

    /// <summary>
    /// Serialises the response: status line, the handler's headers, then Date,
    /// Server, Content-Length and (if given) Connection, a blank line, and the
    /// body. For a HEAD request <paramref name="includeBody"/> is false: the
    /// headers still describe the body, but it is not sent.
    /// </summary>
    internal void WriteTo(IBufferWriter<byte> output, bool includeBody, string? connectionHeader, string serverHeader)
    {
        bool bodyAllowed = StatusCode >= 200 && StatusCode is not (204 or 304);

        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ")
            .Append(StatusCode.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(ReasonPhrase ?? HttpStatus.ReasonPhrase(StatusCode))
            .Append("\r\n");

        foreach (var (name, value) in Headers)
        {
            if (IsReserved(name))
            {
                continue; // these are the server's to set, below
            }

            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Server: ").Append(serverHeader).Append("\r\n");
        if (bodyAllowed)
        {
            head.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        if (connectionHeader is not null)
        {
            head.Append("Connection: ").Append(connectionHeader).Append("\r\n");
        }

        head.Append("\r\n");

        // Header values are Latin-1 on the wire; anything wider gets replaced.
        Encoding.Latin1.GetBytes(head.ToString(), output);

        if (includeBody && bodyAllowed && Body.Length > 0)
        {
            output.Write(Body.Span);
        }
    }

    private static bool IsReserved(string name) =>
        name.Equals("Date", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Server", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
}
