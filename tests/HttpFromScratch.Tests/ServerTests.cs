using System.Text;
using System.Text.RegularExpressions;

namespace HttpFromScratch.Tests;

/// <summary>
/// Talks to a real server on a random loopback port, mostly with the raw client
/// so the exact bytes are checked, and once with HttpClient so a real client
/// has to be satisfied too.
/// </summary>
public sealed partial class ServerTests : IAsyncLifetime
{
    private HttpServer _server = null!;

    public Task InitializeAsync()
    {
        _server = StartServer();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static HttpServer StartServer(Action<HttpServerOptions>? configure = null)
    {
        var options = new HttpServerOptions { Port = 0 };
        configure?.Invoke(options);
        var server = new HttpServer(Handle, options);
        server.Start();
        return server;
    }

    private static ValueTask<HttpResponse> Handle(HttpRequest request) => ValueTask.FromResult(request.Path switch
    {
        "/" => HttpResponse.Text("hello"),
        "/echo" => HttpResponse.Bytes(request.Body, request.Headers["Content-Type"] ?? "application/octet-stream"),
        "/boom" => throw new InvalidOperationException("the handler failed"),
        _ => HttpResponse.NotFound(),
    });

    private RawClient Connect() => new(_server.Port);

    [GeneratedRegex(@"^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT$")]
    private static partial Regex Rfc1123Date();

    [Fact]
    public void Get_ReturnsTheExactStatusLineHeadersAndBody()
    {
        using var client = Connect();
        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        var response = client.ReadResponse();

        Assert.Equal("HTTP/1.1 200 OK", response.StatusLine);
        Assert.Equal("text/plain; charset=utf-8", response["Content-Type"]);
        Assert.Equal("5", response["Content-Length"]);
        Assert.Equal("http-from-scratch/1.0", response["Server"]);
        Assert.Matches(Rfc1123Date(), response["Date"]!);
        Assert.Null(response["Connection"]);
        Assert.Equal("hello", response.Text);
    }

    [Fact]
    public void KeepAlive_ServesSeveralRequestsOnOneConnection()
    {
        using var client = Connect();
        for (int i = 0; i < 3; i++)
        {
            client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
            Assert.Equal("hello", client.ReadResponse().Text);
        }
    }

    [Fact]
    public void ConnectionClose_ClosesAfterTheResponse()
    {
        using var client = Connect();
        client.Send("GET / HTTP/1.1\r\nHost: t\r\nConnection: close\r\n\r\n");
        var response = client.ReadResponse();

        Assert.Equal(200, response.Status);
        Assert.Equal("close", response["Connection"]);
        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void Http10_ClosesByDefault()
    {
        using var client = Connect();
        client.Send("GET / HTTP/1.0\r\n\r\n");
        var response = client.ReadResponse();

        Assert.Equal("HTTP/1.1 200 OK", response.StatusLine);
        Assert.Equal("close", response["Connection"]);
        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void Http10_StaysOpenWhenAsked()
    {
        using var client = Connect();
        client.Send("GET / HTTP/1.0\r\nConnection: keep-alive\r\n\r\n");
        Assert.Equal("keep-alive", client.ReadResponse()["Connection"]);

        client.Send("GET / HTTP/1.0\r\nConnection: keep-alive\r\n\r\n");
        Assert.Equal("hello", client.ReadResponse().Text);
    }

    [Fact]
    public void Pipelined_RequestsAreAnsweredInOrder()
    {
        using var client = Connect();
        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\nGET /missing HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("hello", client.ReadResponse().Text);
        Assert.Equal(404, client.ReadResponse().Status);
    }

    [Fact]
    public void Head_SplitAcrossPacketsIsStillParsed()
    {
        using var client = Connect();
        foreach (string piece in new[] { "GET / HT", "TP/1.1\r\nHo", "st: t\r\n", "\r\n" })
        {
            client.Send(piece);
            Thread.Sleep(30);
        }

        Assert.Equal("hello", client.ReadResponse().Text);
    }

    [Fact]
    public void Head_HasTheHeadersButNoBody()
    {
        using var client = Connect();
        client.Send("HEAD / HTTP/1.1\r\nHost: t\r\n\r\n");
        var response = client.ReadResponse(headRequest: true);

        Assert.Equal(200, response.Status);
        Assert.Equal("5", response["Content-Length"]);

        // Nothing was left on the wire: the next request lines up correctly.
        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("hello", client.ReadResponse().Text);
    }

    [Fact]
    public void UnknownPath_Is404()
    {
        using var client = Connect();
        client.Send("GET /nope HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("HTTP/1.1 404 Not Found", client.ReadResponse().StatusLine);
    }

    [Fact]
    public void Post_WithContentLength_IsEchoed()
    {
        using var client = Connect();
        client.Send("POST /echo HTTP/1.1\r\nHost: t\r\nContent-Type: text/plain\r\nContent-Length: 4\r\n\r\nping");
        var response = client.ReadResponse();

        Assert.Equal(200, response.Status);
        Assert.Equal("text/plain", response["Content-Type"]);
        Assert.Equal("ping", response.Text);
    }

    [Fact]
    public void Post_WithChunkedBody_IsEchoed()
    {
        using var client = Connect();
        client.Send("POST /echo HTTP/1.1\r\nHost: t\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2\r\nde\r\n0\r\n\r\n");

        Assert.Equal("abcde", client.ReadResponse().Text);

        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("hello", client.ReadResponse().Text);
    }

    [Fact]
    public void Post_BodyArrivingSlowly_IsReadInFull()
    {
        using var client = Connect();
        client.Send("POST /echo HTTP/1.1\r\nHost: t\r\nContent-Length: 6\r\n\r\nab");
        Thread.Sleep(30);
        client.Send("cd");
        Thread.Sleep(30);
        client.Send("ef");

        Assert.Equal("abcdef", client.ReadResponse().Text);
    }

    [Fact]
    public void Expect100Continue_GetsAnInterimResponseFirst()
    {
        using var client = Connect();
        client.Send("POST /echo HTTP/1.1\r\nHost: t\r\nExpect: 100-continue\r\nContent-Length: 4\r\n\r\n");
        Assert.Equal("HTTP/1.1 100 Continue", client.ReadResponse().StatusLine);

        client.Send("ping");
        Assert.Equal("ping", client.ReadResponse().Text);
    }

    [Fact]
    public void MalformedRequest_Is400AndCloses()
    {
        using var client = Connect();
        client.Send("GET / HTTP/1.1\r\nHost: t\r\nBad Header\r\n\r\n");
        var response = client.ReadResponse();

        Assert.Equal(400, response.Status);
        Assert.Equal("close", response["Connection"]);
        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void UnsupportedVersion_Is505()
    {
        using var client = Connect();
        client.Send("GET / HTTP/2.0\r\nHost: t\r\n\r\n");

        Assert.Equal(505, client.ReadResponse().Status);
    }

    [Fact]
    public async Task BodyOverTheLimit_Is413()
    {
        await using var server = StartServer(o => o.MaxBodyBytes = 10);
        using var client = new RawClient(server.Port);
        client.Send("POST /echo HTTP/1.1\r\nHost: t\r\nContent-Length: 11\r\n\r\n");

        Assert.Equal(413, client.ReadResponse().Status);
        Assert.True(client.WaitForClose());

        using var chunked = new RawClient(server.Port);
        chunked.Send("POST /echo HTTP/1.1\r\nHost: t\r\nTransfer-Encoding: chunked\r\n\r\nb\r\n");

        Assert.Equal(413, chunked.ReadResponse().Status);
    }

    [Fact]
    public async Task HeadOverTheLimit_Is431()
    {
        await using var server = StartServer(o => o.MaxHeadBytes = 1024);
        using var client = new RawClient(server.Port);
        client.Send($"GET / HTTP/1.1\r\nHost: t\r\nX-Big: {new string('x', 2000)}\r\n\r\n");

        Assert.Equal(431, client.ReadResponse().Status);
        Assert.True(client.WaitForClose());
    }

    [Fact]
    public async Task IdleConnection_IsClosedAfterTheKeepAliveTimeout()
    {
        await using var server = StartServer(o => o.KeepAliveTimeout = TimeSpan.FromMilliseconds(200));
        using var client = new RawClient(server.Port);
        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal(200, client.ReadResponse().Status);

        Assert.True(client.WaitForClose());
    }

    [Fact]
    public async Task MaxRequestsPerConnection_ClosesAfterTheLastOne()
    {
        await using var server = StartServer(o => o.MaxRequestsPerConnection = 2);
        using var client = new RawClient(server.Port);
        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Null(client.ReadResponse()["Connection"]);

        client.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("close", client.ReadResponse()["Connection"]);
        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void HandlerException_Is500()
    {
        using var client = Connect();
        client.Send("GET /boom HTTP/1.1\r\nHost: t\r\n\r\n");
        var response = client.ReadResponse();

        Assert.Equal(500, response.Status);
        Assert.Equal("close", response["Connection"]);
        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void ClientHangingUpMidRequest_IsHandledQuietly()
    {
        using (var client = Connect())
        {
            client.Send("GET / HTTP/1.1\r\nHost: t\r\n"); // never finishes the head
        }

        // The server is still fine afterwards.
        using var next = Connect();
        next.Send("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("hello", next.ReadResponse().Text);
    }

    [Fact]
    public async Task WorksWithARealHttpClient()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };

        Assert.Equal("hello", await http.GetStringAsync("/"));

        using var post = await http.PostAsync("/echo", new StringContent("ping", Encoding.UTF8, "text/plain"));
        Assert.Equal(200, (int)post.StatusCode);
        Assert.Equal("ping", await post.Content.ReadAsStringAsync());
        Assert.Equal("http-from-scratch/1.0", post.Headers.Server.ToString());

        using var missing = await http.GetAsync("/missing");
        Assert.Equal(404, (int)missing.StatusCode);
    }
}
