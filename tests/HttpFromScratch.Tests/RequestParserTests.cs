using System.Text;

namespace HttpFromScratch.Tests;

public class RequestParserTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static RequestHead Parse(string head) => RequestParser.ParseHead(Ascii(head));

    private static HttpParseException Rejects(string head) =>
        Assert.Throws<HttpParseException>(() => RequestParser.ParseHead(Ascii(head)));

    [Fact]
    public void FindHeadEnd_IsMinusOne_UntilTheBlankLineArrives()
    {
        Assert.Equal(-1, RequestParser.FindHeadEnd(Ascii("GET / HTTP/1.1\r\nHost: a\r\n")));
        Assert.Equal(-1, RequestParser.FindHeadEnd(Ascii("GET / HTTP/1.1\r\nHost: a\r\n\r")));
    }

    [Fact]
    public void FindHeadEnd_CountsTheBlankLine_AndIgnoresWhatFollows()
    {
        Assert.Equal(27, RequestParser.FindHeadEnd(Ascii("GET / HTTP/1.1\r\nHost: a\r\n\r\nXYZ")));
    }

    [Fact]
    public void ParsesTheRequestLineAndHeaders()
    {
        var head = Parse("GET /index.html HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n");

        Assert.Equal("GET", head.Method);
        Assert.Equal("/index.html", head.Target);
        Assert.Equal("/index.html", head.Path);
        Assert.Equal("", head.Query);
        Assert.Equal(new Version(1, 1), head.Version);
        Assert.Equal("example.com", head.Headers["Host"]);
        Assert.Equal("*/*", head.Headers["Accept"]);
        Assert.Equal(BodyFraming.None, head.Framing);
    }

    [Fact]
    public void HeaderLookupIsCaseInsensitive()
    {
        var head = Parse("GET / HTTP/1.1\r\nhOsT: a\r\nCONTENT-type: text/plain\r\n\r\n");

        Assert.Equal("a", head.Headers["Host"]);
        Assert.Equal("text/plain", head.Headers["content-TYPE"]);
        Assert.True(head.Headers.Contains("Content-Type"));
    }

    [Fact]
    public void HeaderValuesAreTrimmedOfSurroundingWhitespace()
    {
        var head = Parse("GET / HTTP/1.1\r\nHost: a\r\nX-Test:   spaced value \t\r\n\r\n");

        Assert.Equal("spaced value", head.Headers["X-Test"]);
    }

    [Fact]
    public void RepeatedHeadersKeepEveryValueInOrder()
    {
        var head = Parse("GET / HTTP/1.1\r\nHost: a\r\nAccept: text/html\r\nAccept: */*\r\n\r\n");

        Assert.Equal(new[] { "text/html", "*/*" }, head.Headers.GetValues("Accept"));
        Assert.Equal("text/html", head.Headers["Accept"]);
    }

    [Fact]
    public void HasToken_ReadsCommaSeparatedLists()
    {
        var head = Parse("GET / HTTP/1.1\r\nHost: a\r\nConnection: keep-alive, Upgrade\r\n\r\n");

        Assert.True(head.Headers.HasToken("Connection", "upgrade"));
        Assert.False(head.Headers.HasToken("Connection", "close"));
    }

    [Fact]
    public void SplitsPathAndQuery_AndLeavesTheQueryUndecoded()
    {
        var head = Parse("GET /search?q=a%20b&x=2 HTTP/1.1\r\nHost: a\r\n\r\n");

        Assert.Equal("/search", head.Path);
        Assert.Equal("q=a%20b&x=2", head.Query);
        Assert.Equal("/search?q=a%20b&x=2", head.Target);
    }

    [Fact]
    public void PercentDecodesThePath()
    {
        var head = Parse("GET /a%20b/c%2Fd/%C3%A9 HTTP/1.1\r\nHost: a\r\n\r\n");

        Assert.Equal("/a b/c/d/é", head.Path);
    }

    [Theory]
    [InlineData("/a%2")]
    [InlineData("/a%zz")]
    [InlineData("/a%00")]
    public void RejectsBadPercentEncoding(string target)
    {
        Assert.Equal(400, Rejects($"GET {target} HTTP/1.1\r\nHost: a\r\n\r\n").StatusCode);
    }

    [Fact]
    public void AbsoluteFormTargetYieldsThePath()
    {
        var head = Parse("GET http://example.com/x?y=1 HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.Equal("http://example.com/x?y=1", head.Target);
        Assert.Equal("/x", head.Path);
        Assert.Equal("y=1", head.Query);

        Assert.Equal("/", Parse("GET http://example.com HTTP/1.1\r\nHost: example.com\r\n\r\n").Path);
    }

    [Fact]
    public void AsteriskFormIsAllowed()
    {
        Assert.Equal("*", Parse("OPTIONS * HTTP/1.1\r\nHost: a\r\n\r\n").Path);
    }

    [Fact]
    public void Http11RequiresExactlyOneHost()
    {
        Assert.Equal(400, Rejects("GET / HTTP/1.1\r\n\r\n").StatusCode);
        Assert.Equal(400, Rejects("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n").StatusCode);
    }

    [Fact]
    public void Http10DoesNotNeedHost()
    {
        var head = Parse("GET / HTTP/1.0\r\n\r\n");

        Assert.Equal(new Version(1, 0), head.Version);
    }

    [Theory]
    [InlineData("GET /\r\n\r\n")]
    [InlineData("GET  / HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1 extra\r\n\r\n")]
    [InlineData(" GET / HTTP/1.1\r\n\r\n")]
    [InlineData("\r\n\r\n")]
    public void RejectsMalformedRequestLines(string head)
    {
        Assert.Equal(400, Rejects(head).StatusCode);
    }

    [Theory]
    [InlineData("HTTP/2.0", 505)]
    [InlineData("HTTP/1.5", 505)]
    [InlineData("HTTP/1", 400)]
    [InlineData("http/1.1", 400)]
    [InlineData("HTTP/1.1.1", 400)]
    public void OnlyHttp10And11AreAccepted(string version, int expected)
    {
        Assert.Equal(expected, Rejects($"GET / {version}\r\nHost: a\r\n\r\n").StatusCode);
    }

    [Fact]
    public void MethodMustBeAToken()
    {
        Assert.Equal(400, Rejects("G(T / HTTP/1.1\r\nHost: a\r\n\r\n").StatusCode);
        Assert.Equal("m-y.method", Parse("m-y.method / HTTP/1.1\r\nHost: a\r\n\r\n").Method);
    }

    [Theory]
    [InlineData("Host example.com")]
    [InlineData("Host : example.com")]
    [InlineData(": example.com")]
    [InlineData("X-Test: ab")]
    public void RejectsMalformedHeaderLines(string line)
    {
        Assert.Equal(400, Rejects($"GET / HTTP/1.1\r\n{line}\r\nHost: a\r\n\r\n").StatusCode);
    }

    [Fact]
    public void RejectsObsoleteLineFolding()
    {
        Assert.Equal(400, Rejects("GET / HTTP/1.1\r\nHost: a\r\nX-Long: one\r\n two\r\n\r\n").StatusCode);
    }

    [Fact]
    public void RejectsControlAndNonAsciiBytesInTheRequestLine()
    {
        Assert.Equal(400, Rejects("GET /a\tb HTTP/1.1\r\nHost: a\r\n\r\n").StatusCode);

        var utf8 = Encoding.UTF8.GetBytes("GET /é HTTP/1.1\r\nHost: a\r\n\r\n");
        var ex = Assert.Throws<HttpParseException>(() => RequestParser.ParseHead(utf8));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void HeaderValuesMayContainObsText()
    {
        var latin1 = Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\nHost: a\r\nX-Name: café\r\n\r\n");

        Assert.Equal("café", RequestParser.ParseHead(latin1).Headers["X-Name"]);
    }

    [Fact]
    public void TooManyHeadersIs431()
    {
        string hundred = string.Concat(Enumerable.Range(0, 99).Select(i => $"X-{i}: v\r\n"));
        Parse($"GET / HTTP/1.1\r\nHost: a\r\n{hundred}\r\n");

        Assert.Equal(431, Rejects($"GET / HTTP/1.1\r\nHost: a\r\n{hundred}X-Extra: v\r\n\r\n").StatusCode);
    }

    [Fact]
    public void TargetTooLongIs414()
    {
        string target = "/" + new string('a', 9000);

        Assert.Equal(414, Rejects($"GET {target} HTTP/1.1\r\nHost: a\r\n\r\n").StatusCode);
    }

    [Fact]
    public void ContentLengthSetsTheFraming()
    {
        var head = Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\n\r\n");

        Assert.Equal(BodyFraming.ContentLength, head.Framing);
        Assert.Equal(5, head.ContentLength);
    }

    [Fact]
    public void ZeroContentLengthMeansNoBody()
    {
        Assert.Equal(BodyFraming.None, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 0\r\n\r\n").Framing);
    }

    [Fact]
    public void IdenticalRepeatedContentLengthsAreTolerated()
    {
        Assert.Equal(5, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\n").ContentLength);
        Assert.Equal(5, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5, 5\r\n\r\n").ContentLength);
    }

    [Theory]
    [InlineData("Content-Length: abc")]
    [InlineData("Content-Length: -1")]
    [InlineData("Content-Length: ")]
    [InlineData("Content-Length: 1234567890123456789")]
    [InlineData("Content-Length: 5\r\nContent-Length: 6")]
    [InlineData("Content-Length: 5, 6")]
    public void RejectsBadContentLengths(string header)
    {
        Assert.Equal(400, Rejects($"POST / HTTP/1.1\r\nHost: a\r\n{header}\r\n\r\n").StatusCode);
    }

    [Fact]
    public void ChunkedTransferEncodingSetsTheFraming()
    {
        Assert.Equal(BodyFraming.Chunked, Parse("POST / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n").Framing);
        Assert.Equal(BodyFraming.Chunked, Parse("POST / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: Chunked\r\n\r\n").Framing);
    }

    [Fact]
    public void OtherTransferCodingsAreNotImplemented()
    {
        Assert.Equal(501, Rejects("POST / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: gzip, chunked\r\n\r\n").StatusCode);
    }

    [Theory]
    [InlineData("HTTP/1.1", "Transfer-Encoding: gzip")]
    [InlineData("HTTP/1.1", "Transfer-Encoding: chunked, gzip")]
    [InlineData("HTTP/1.1", "Transfer-Encoding: chunked\r\nContent-Length: 5")]
    [InlineData("HTTP/1.0", "Transfer-Encoding: chunked")]
    public void RejectsAmbiguousBodyFraming(string version, string headers)
    {
        Assert.Equal(400, Rejects($"POST / {version}\r\nHost: a\r\n{headers}\r\n\r\n").StatusCode);
    }
}
