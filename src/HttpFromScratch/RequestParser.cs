using System.Text;

namespace HttpFromScratch;

/// <summary>How the request said its body is delimited.</summary>
public enum BodyFraming
{
    /// <summary>No body follows the head.</summary>
    None,

    /// <summary>Exactly <see cref="RequestHead.ContentLength"/> bytes follow the head.</summary>
    ContentLength,

    /// <summary>A chunked body follows the head (Transfer-Encoding: chunked).</summary>
    Chunked,
}

/// <summary>What the request line and header fields told us, before any body is read.</summary>
public sealed class RequestHead
{
    public required string Method { get; init; }

    public required string Target { get; init; }

    public required string Path { get; init; }

    public required string Query { get; init; }

    public required Version Version { get; init; }

    public required HttpHeaders Headers { get; init; }

    public required BodyFraming Framing { get; init; }

    /// <summary>The declared body length when <see cref="Framing"/> is ContentLength.</summary>
    public long ContentLength { get; init; }
}

/// <summary>Thrown when a request cannot be parsed. Carries the status the client should get back.</summary>
public sealed class HttpParseException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Turns the bytes of a request head (request line, header fields, blank line)
/// into a <see cref="RequestHead"/>. These are pure functions over spans so they
/// can be tested without a socket. They follow RFC 9112 where it matters:
/// CRLF line endings, no whitespace before the colon, no obsolete line folding,
/// and the Content-Length / Transfer-Encoding rules that stop request smuggling.
/// </summary>
public static class RequestParser
{
    public const int MaxHeaderCount = 100;
    public const int MaxTargetLength = 8 * 1024;

    private static readonly Version Http10 = new(1, 0);
    private static readonly Version Http11 = new(1, 1);

    /// <summary>
    /// The length of the head including its terminating blank line, or -1 if the
    /// buffer does not yet hold a complete head. Anything after that length is
    /// the body, or the next pipelined request.
    /// </summary>
    public static int FindHeadEnd(ReadOnlySpan<byte> buffer)
    {
        int i = buffer.IndexOf("\r\n\r\n"u8);
        return i < 0 ? -1 : i + 4;
    }

    /// <summary>Parses a complete head, i.e. everything up to and including the blank line.</summary>
    public static RequestHead ParseHead(ReadOnlySpan<byte> head)
    {
        if (!head.EndsWith("\r\n\r\n"u8))
        {
            throw Bad("The head must end with a blank line.");
        }

        head = head[..^2]; // leave one CRLF on the last line so every line ends the same way

        int eol = head.IndexOf("\r\n"u8);
        var (method, target, version) = ParseRequestLine(head[..eol]);
        head = head[(eol + 2)..];

        var headers = new HttpHeaders();
        while (!head.IsEmpty)
        {
            eol = head.IndexOf("\r\n"u8);
            ParseHeaderLine(head[..eol], headers);
            if (headers.Count > MaxHeaderCount)
            {
                throw new HttpParseException(431, "Too many header fields.");
            }

            head = head[(eol + 2)..];
        }

        if (version == Http11 && headers.GetValues("Host").Count() != 1)
        {
            throw Bad("HTTP/1.1 requests must carry exactly one Host header.");
        }

        var (path, query) = SplitTarget(target);
        var (framing, contentLength) = DetermineFraming(headers, version);

        return new RequestHead
        {
            Method = method,
            Target = target,
            Path = path,
            Query = query,
            Version = version,
            Headers = headers,
            Framing = framing,
            ContentLength = contentLength,
        };
    }

    private static (string Method, string Target, Version Version) ParseRequestLine(ReadOnlySpan<byte> line)
    {
        foreach (byte b in line)
        {
            if (b <= 0x20 && b != ' ' || b >= 0x7F)
            {
                throw Bad("The request line contains a control or non-ASCII character.");
            }
        }

        int sp1 = line.IndexOf((byte)' ');
        int sp2 = sp1 < 0 ? -1 : line[(sp1 + 1)..].IndexOf((byte)' ');
        if (sp1 <= 0 || sp2 <= 0)
        {
            throw Bad("The request line must be 'method SP target SP version'.");
        }

        sp2 += sp1 + 1;
        var methodBytes = line[..sp1];
        var targetBytes = line[(sp1 + 1)..sp2];
        var versionBytes = line[(sp2 + 1)..];

        if (versionBytes.IsEmpty || versionBytes.Contains((byte)' '))
        {
            throw Bad("The request line must be 'method SP target SP version'.");
        }

        if (!IsToken(methodBytes))
        {
            throw Bad("The method is not a valid token.");
        }

        if (targetBytes.Length > MaxTargetLength)
        {
            throw new HttpParseException(414, "The request target is too long.");
        }

        Version version = ParseVersion(versionBytes);
        return (Encoding.ASCII.GetString(methodBytes), Encoding.ASCII.GetString(targetBytes), version);
    }

    private static Version ParseVersion(ReadOnlySpan<byte> v)
    {
        // HTTP-version = "HTTP/" DIGIT "." DIGIT, case-sensitive.
        if (v.Length != 8 || !v.StartsWith("HTTP/"u8) || !IsDigit(v[5]) || v[6] != '.' || !IsDigit(v[7]))
        {
            throw Bad("Malformed HTTP version.");
        }

        if (v[5] != '1' || v[7] > '1')
        {
            throw new HttpParseException(505, "Only HTTP/1.0 and HTTP/1.1 are supported.");
        }

        return v[7] == '0' ? Http10 : Http11;
    }

    private static void ParseHeaderLine(ReadOnlySpan<byte> line, HttpHeaders headers)
    {
        if (line.IsEmpty)
        {
            throw Bad("Empty header line.");
        }

        if (line[0] == ' ' || line[0] == '\t')
        {
            throw Bad("Obsolete line folding is not allowed.");
        }

        int colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            throw Bad("A header line needs a name, a colon and a value.");
        }

        var name = line[..colon];
        if (!IsToken(name))
        {
            throw Bad("The header name is not a valid token (no whitespace before the colon).");
        }

        var value = Trim(line[(colon + 1)..]);
        foreach (byte b in value)
        {
            if (b < 0x20 && b != '\t' || b == 0x7F)
            {
                throw Bad("A header value contains a control character.");
            }
        }

        headers.Add(Encoding.ASCII.GetString(name), Encoding.Latin1.GetString(value));
    }

    private static (string Path, string Query) SplitTarget(string target)
    {
        string pathPart;
        if (target.StartsWith('/'))
        {
            pathPart = target; // origin-form, the normal case
        }
        else if (target == "*")
        {
            return ("*", ""); // asterisk-form, e.g. OPTIONS *
        }
        else if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // absolute-form, as sent to proxies: drop the scheme and authority
            int slash = target.IndexOf('/', target.IndexOf("//", StringComparison.Ordinal) + 2);
            pathPart = slash < 0 ? "/" : target[slash..];
        }
        else
        {
            throw Bad("Unsupported request-target form.");
        }

        int q = pathPart.IndexOf('?');
        string query = q < 0 ? "" : pathPart[(q + 1)..];
        string rawPath = q < 0 ? pathPart : pathPart[..q];
        return (PercentDecode(rawPath), query);
    }

    private static string PercentDecode(string raw)
    {
        if (!raw.Contains('%'))
        {
            return raw;
        }

        var bytes = new byte[raw.Length];
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '%')
            {
                if (i + 2 >= raw.Length || !TryHex(raw[i + 1], out int hi) || !TryHex(raw[i + 2], out int lo))
                {
                    throw Bad("Malformed percent-encoding in the request target.");
                }

                bytes[n++] = (byte)(hi << 4 | lo);
                i += 2;
                if (bytes[n - 1] == 0)
                {
                    throw Bad("NUL in the request target.");
                }
            }
            else
            {
                bytes[n++] = (byte)c; // the request line was already checked to be ASCII
            }
        }

        return Encoding.UTF8.GetString(bytes, 0, n);
    }

    private static (BodyFraming Framing, long ContentLength) DetermineFraming(HttpHeaders headers, Version version)
    {
        var transferEncodings = headers.GetValues("Transfer-Encoding").ToList();
        var contentLengths = headers.GetValues("Content-Length").ToList();

        if (transferEncodings.Count > 0 && contentLengths.Count > 0)
        {
            // RFC 9112 6.1: the two disagree about where the body ends, which is
            // exactly what request smuggling exploits. Refuse the request.
            throw Bad("Transfer-Encoding and Content-Length must not both be present.");
        }

        if (transferEncodings.Count > 0)
        {
            if (version == Http10)
            {
                throw Bad("Transfer-Encoding requires HTTP/1.1.");
            }

            var codings = transferEncodings
                .SelectMany(v => v.Split(','))
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToList();

            if (codings.Count == 0 || !codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                throw Bad("Transfer-Encoding must end with chunked.");
            }

            if (codings.Count > 1)
            {
                throw new HttpParseException(501, "Only the chunked transfer coding is supported.");
            }

            return (BodyFraming.Chunked, 0);
        }

        if (contentLengths.Count > 0)
        {
            // A repeated Content-Length is tolerated only if every copy says the same thing.
            var values = contentLengths.SelectMany(v => v.Split(',')).Select(v => v.Trim()).ToList();
            string first = values[0];
            if (values.Any(v => v != first))
            {
                throw Bad("Conflicting Content-Length values.");
            }

            if (first.Length is 0 or > 18 || !first.All(char.IsAsciiDigit))
            {
                throw Bad("Content-Length is not a valid number.");
            }

            long length = long.Parse(first, System.Globalization.CultureInfo.InvariantCulture);
            return length == 0 ? (BodyFraming.None, 0) : (BodyFraming.ContentLength, length);
        }

        return (BodyFraming.None, 0);
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && (s[start] == ' ' || s[start] == '\t'))
        {
            start++;
        }

        while (end > start && (s[end - 1] == ' ' || s[end - 1] == '\t'))
        {
            end--;
        }

        return s[start..end];
    }

    private static bool IsToken(ReadOnlySpan<byte> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (byte b in s)
        {
            bool ok = b is >= (byte)'0' and <= (byte)'9'
                || b is >= (byte)'A' and <= (byte)'Z'
                || b is >= (byte)'a' and <= (byte)'z'
                || "!#$%&'*+-.^_`|~"u8.Contains(b);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool TryHex(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }

    private static HttpParseException Bad(string message) => new(400, message);
}
