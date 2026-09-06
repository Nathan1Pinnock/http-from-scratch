# http-from-scratch

An HTTP/1.1 server written in C# on top of raw TCP sockets. No Kestrel, no HttpListener, no framework: the listener, the request parser, keep-alive, chunked bodies, the router and the static file handler are all in this repo, and real clients (curl, browsers, `HttpClient`) talk to it without knowing the difference.

I use ASP.NET Core every day at TaskPool and wanted to know what Kestrel is actually doing underneath: how bytes off a socket become a request object, what keep-alive really means on the wire, why `Content-Length` and `Transfer-Encoding` can't both be trusted, and where the sharp edges are that a framework hides. Building one is the fastest way to find out.

## Status

Work in progress, built up in small commits. The commit log is the changelog.

## Run it

    dotnet run --project src/HttpFromScratch.Host -- --port 8080 --root www
    curl -v http://localhost:8080/

## Test it

    dotnet test

The tests open real sockets against the server on a random port and check the exact bytes that come back. They also drive it with `HttpClient`, so a real client has to be happy with it too.
