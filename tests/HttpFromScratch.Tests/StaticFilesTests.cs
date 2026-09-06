using System.Globalization;
using System.Text;

namespace HttpFromScratch.Tests;

public sealed class StaticFilesTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "http-from-scratch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly StaticFiles _files;

    public StaticFilesTests()
    {
        _root = Path.Combine(_base, "www");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<h1>hi</h1>");
        File.WriteAllText(Path.Combine(_root, "style.css"), "body{}");
        File.WriteAllText(Path.Combine(_root, "sub", "note.txt"), "note");
        File.WriteAllText(Path.Combine(_base, "secret.txt"), "secret"); // outside the root
        _files = new StaticFiles(_root);
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private async Task<HttpResponse> Get(string path, string method = "GET", string? ifModifiedSince = null)
    {
        var headers = new HttpHeaders();
        if (ifModifiedSince is not null)
        {
            headers.Add("If-Modified-Since", ifModifiedSince);
        }

        return await _files.HandleAsync(new HttpRequest
        {
            Method = method,
            Target = path,
            Path = path,
            Query = "",
            Version = new Version(1, 1),
            Headers = headers,
        });
    }

    private static string Text(HttpResponse response) => Encoding.UTF8.GetString(response.Body.Span);

    [Fact]
    public async Task TheRootServesIndexHtml()
    {
        var response = await Get("/");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Headers["Content-Type"]);
        Assert.Equal("<h1>hi</h1>", Text(response));
        Assert.NotNull(response.Headers["Last-Modified"]);
    }

    [Fact]
    public async Task ContentTypeFollowsTheExtension()
    {
        Assert.Equal("text/css; charset=utf-8", (await Get("/style.css")).Headers["Content-Type"]);
        Assert.Equal("text/plain; charset=utf-8", (await Get("/sub/note.txt")).Headers["Content-Type"]);
        Assert.Equal("note", Text(await Get("/sub/note.txt")));
    }

    [Theory]
    [InlineData("/missing.html")]
    [InlineData("/sub")]
    [InlineData("/sub/")]
    public async Task MissingFilesAndIndexlessDirectoriesAre404(string path)
    {
        Assert.Equal(404, (await Get(path)).StatusCode);
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/sub/../../secret.txt")]
    [InlineData("/./../secret.txt")]
    public async Task PathsThatEscapeTheRootAre404(string path)
    {
        Assert.Equal(404, (await Get(path)).StatusCode);
    }

    [Fact]
    public async Task AnAbsolutePathInTheUrlCannotEscapeTheRoot()
    {
        string absolute = "/" + Path.Combine(_base, "secret.txt").Replace('\\', '/').TrimStart('/');

        Assert.Equal(404, (await Get(absolute)).StatusCode);
    }

    [Fact]
    public async Task IfModifiedSinceGets304WhenNothingChanged()
    {
        string lastModified = (await Get("/")).Headers["Last-Modified"]!;

        var notModified = await Get("/", ifModifiedSince: lastModified);
        Assert.Equal(304, notModified.StatusCode);
        Assert.Equal(lastModified, notModified.Headers["Last-Modified"]);
        Assert.Equal(0, notModified.Body.Length);

        DateTime earlier = DateTime.ParseExact(lastModified, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddDays(-1);
        Assert.Equal(200, (await Get("/", ifModifiedSince: earlier.ToString("r", CultureInfo.InvariantCulture))).StatusCode);
    }

    [Fact]
    public async Task OnlyGetAndHeadAreAllowed()
    {
        var response = await Get("/", method: "POST");

        Assert.Equal(405, response.StatusCode);
        Assert.Equal("GET, HEAD", response.Headers["Allow"]);
    }
}
