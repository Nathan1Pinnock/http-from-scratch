using System.Text;

namespace HttpFromScratch.Tests;

public class RouterTests
{
    private static HttpRequest Request(string method, string path) => new()
    {
        Method = method,
        Target = path,
        Path = path,
        Query = "",
        Version = new Version(1, 1),
        Headers = new HttpHeaders(),
    };

    private static string Text(HttpResponse response) => Encoding.UTF8.GetString(response.Body.Span);

    [Fact]
    public async Task MatchesOnMethodAndPath()
    {
        var router = new Router()
            .MapGet("/a", _ => HttpResponse.Text("got"))
            .MapPost("/a", _ => HttpResponse.Text("posted"));

        Assert.Equal("got", Text(await router.HandleAsync(Request("GET", "/a"))));
        Assert.Equal("posted", Text(await router.HandleAsync(Request("POST", "/a"))));
    }

    [Fact]
    public async Task HeadUsesTheGetHandler()
    {
        var router = new Router().MapGet("/a", _ => HttpResponse.Text("got"));

        Assert.Equal(200, (await router.HandleAsync(Request("HEAD", "/a"))).StatusCode);
    }

    [Fact]
    public async Task WrongMethodIs405WithAllow()
    {
        var router = new Router().MapGet("/a", _ => HttpResponse.Text("got"));
        var response = await router.HandleAsync(Request("DELETE", "/a"));

        Assert.Equal(405, response.StatusCode);
        Assert.Equal("GET, HEAD", response.Headers["Allow"]);
    }

    [Fact]
    public async Task UnknownPathGoesToTheFallback_Or404()
    {
        var bare = new Router().MapGet("/a", _ => HttpResponse.Text("got"));
        Assert.Equal(404, (await bare.HandleAsync(Request("GET", "/b"))).StatusCode);

        var withFallback = new Router().Fallback(_ => ValueTask.FromResult(HttpResponse.Text("fallback")));
        Assert.Equal("fallback", Text(await withFallback.HandleAsync(Request("GET", "/anything"))));
    }

    [Fact]
    public async Task PathsAreExactAndCaseSensitive()
    {
        var router = new Router().MapGet("/a", _ => HttpResponse.Text("got"));

        Assert.Equal(404, (await router.HandleAsync(Request("GET", "/A"))).StatusCode);
        Assert.Equal(404, (await router.HandleAsync(Request("GET", "/a/"))).StatusCode);
    }
}
