using System.Text;

namespace HttpFromScratch;

/// <summary>One complete request: the parsed head plus a fully buffered body.</summary>
public sealed class HttpRequest
{
    public required string Method { get; init; }

    /// <summary>The request-target exactly as the client sent it, e.g. "/search?q=x".</summary>
    public required string Target { get; init; }

    /// <summary>The percent-decoded path part of the target, e.g. "/search".</summary>
    public required string Path { get; init; }

    /// <summary>The raw query string without the leading '?', or "" if there was none.</summary>
    public required string Query { get; init; }

    public required Version Version { get; init; }

    public required HttpHeaders Headers { get; init; }

    public ReadOnlyMemory<byte> Body { get; init; }

    public bool IsHttp11 => Version.Major == 1 && Version.Minor >= 1;

    public string BodyAsString() => Encoding.UTF8.GetString(Body.Span);
}
