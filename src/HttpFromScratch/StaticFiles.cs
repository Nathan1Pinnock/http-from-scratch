using System.Globalization;

namespace HttpFromScratch;

/// <summary>
/// Serves files from one directory and refuses anything that would resolve
/// outside it. Directories get their index.html. Sends Last-Modified and
/// answers If-Modified-Since with 304 so browsers can cache.
/// </summary>
public sealed class StaticFiles
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".xml"] = "application/xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".pdf"] = "application/pdf",
        [".wasm"] = "application/wasm",
    };

    private readonly string _root;

    /// <param name="root">The directory to serve. Resolved once, so a relative path is relative to the current directory at construction.</param>
    public StaticFiles(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    public ValueTask<HttpResponse> HandleAsync(HttpRequest request) => ValueTask.FromResult(Handle(request));

    private HttpResponse Handle(HttpRequest request)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            var notAllowed = HttpResponse.Error(405);
            notAllowed.Headers.Set("Allow", "GET, HEAD");
            return notAllowed;
        }

        string? file = Resolve(request.Path);
        if (file is null)
        {
            return HttpResponse.NotFound();
        }

        var info = new FileInfo(file);
        DateTime lastModified = TruncateToSeconds(info.LastWriteTimeUtc);
        string lastModifiedHeader = lastModified.ToString("r", CultureInfo.InvariantCulture);

        if (request.Headers["If-Modified-Since"] is string since
            && DateTime.TryParseExact(since, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime sinceUtc)
            && lastModified <= sinceUtc)
        {
            var notModified = HttpResponse.Status(304);
            notModified.Headers.Set("Last-Modified", lastModifiedHeader);
            return notModified;
        }

        var response = HttpResponse.Bytes(File.ReadAllBytes(file), ContentTypes.GetValueOrDefault(info.Extension, "application/octet-stream"));
        response.Headers.Set("Last-Modified", lastModifiedHeader);
        return response;
    }

    /// <summary>Maps a URL path onto a file inside the root, or returns null if there is no such file or it escapes the root.</summary>
    private string? Resolve(string urlPath)
    {
        string relative = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_root, relative));
        }
        catch (ArgumentException)
        {
            return null; // characters that cannot be in a path
        }

        // After normalisation ("..", absolute paths, drive letters) it must still be under the root.
        if (!full.StartsWith(_root, StringComparison.Ordinal) && full != Path.TrimEndingDirectorySeparator(_root))
        {
            return null;
        }

        if (Directory.Exists(full))
        {
            full = Path.Combine(full, "index.html");
        }

        return File.Exists(full) ? full : null;
    }

    private static DateTime TruncateToSeconds(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
