using System.Globalization;
using System.Net;
using HttpFromScratch;

int port = 8080;
string root = "www";
IPAddress bind = IPAddress.Loopback;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        case "--bind" when i + 1 < args.Length:
            bind = IPAddress.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine("usage: HttpFromScratch.Host [--port 8080] [--root www] [--bind 127.0.0.1]");
            return 1;
    }
}

var files = new StaticFiles(root);
var router = new Router()
    .MapGet("/hello", _ => HttpResponse.Text("hello from http-from-scratch\n"))
    .MapPost("/echo", request => HttpResponse.Bytes(request.Body, request.Headers["Content-Type"] ?? "application/octet-stream"))
    .Fallback(files.HandleAsync);

var options = new HttpServerOptions
{
    Address = bind,
    Port = port,
    Log = Console.WriteLine,
};

await using var server = new HttpServer(router.HandleAsync, options);
server.Start();
Console.WriteLine($"Listening on http://{server.EndPoint}/ and serving {files.Root}");
Console.WriteLine("GET /hello and POST /echo are handled in code; everything else comes from the directory. Ctrl+C stops.");

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.TrySetResult();
};
await stopped.Task;

Console.WriteLine("Stopping.");
return 0;
