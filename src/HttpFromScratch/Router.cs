namespace HttpFromScratch;

/// <summary>
/// Exact-path routing: a handler per (method, path), 405 with an Allow header
/// when the path exists but the method does not, HEAD served by the GET handler,
/// and a fallback for everything else (typically <see cref="StaticFiles"/>).
/// </summary>
public sealed class Router
{
    private readonly Dictionary<string, Dictionary<string, RequestHandler>> _routes = new(StringComparer.Ordinal);
    private RequestHandler? _fallback;

    public Router Map(string method, string path, RequestHandler handler)
    {
        if (!_routes.TryGetValue(path, out var byMethod))
        {
            byMethod = new Dictionary<string, RequestHandler>(StringComparer.Ordinal);
            _routes[path] = byMethod;
        }

        byMethod[method] = handler;
        return this;
    }

    public Router Map(string method, string path, Func<HttpRequest, HttpResponse> handler) =>
        Map(method, path, request => ValueTask.FromResult(handler(request)));

    public Router MapGet(string path, Func<HttpRequest, HttpResponse> handler) => Map("GET", path, handler);

    public Router MapPost(string path, Func<HttpRequest, HttpResponse> handler) => Map("POST", path, handler);

    /// <summary>Handles any request whose path has no route of its own.</summary>
    public Router Fallback(RequestHandler handler)
    {
        _fallback = handler;
        return this;
    }

    public ValueTask<HttpResponse> HandleAsync(HttpRequest request)
    {
        if (_routes.TryGetValue(request.Path, out var byMethod))
        {
            if (byMethod.TryGetValue(request.Method, out var handler))
            {
                return handler(request);
            }

            if (request.Method == "HEAD" && byMethod.TryGetValue("GET", out handler))
            {
                return handler(request); // the connection drops the body for HEAD
            }

            var allowed = byMethod.Keys.ToList();
            if (allowed.Contains("GET") && !allowed.Contains("HEAD"))
            {
                allowed.Add("HEAD");
            }

            var notAllowed = HttpResponse.Error(405);
            notAllowed.Headers.Set("Allow", string.Join(", ", allowed));
            return ValueTask.FromResult(notAllowed);
        }

        return _fallback is not null ? _fallback(request) : ValueTask.FromResult(HttpResponse.NotFound());
    }
}
