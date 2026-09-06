# http-from-scratch

An HTTP/1.1 server in C# on raw TCP sockets. No Kestrel, no HttpListener: the listener, the parser, keep-alive, chunked bodies, the router and the static file handler are all in this repo, and curl, browsers and HttpClient talk to it without knowing the difference.

I use ASP.NET Core every day at TaskPool and wanted to know what Kestrel does underneath: how bytes off a socket become a request object, what keep-alive means on the wire, why Content-Length and Transfer-Encoding can't both be trusted, and where the sharp edges are that a framework hides. I wrote it in September 2026.

## Run it

    dotnet run --project src/HttpFromScratch.Host
    curl -v http://localhost:8080/

That serves the sample site in src/HttpFromScratch.Host/www, plus GET /hello and POST /echo from handlers in code, and prints one access log line per request. --port, --root and --bind change the defaults.

## Test it

    dotnet test

101 tests. The parser and the chunked decoder are tested as pure functions over byte arrays. The server tests start a real server on a random loopback port and talk to it with a deliberately dumb socket client, so the exact bytes on the wire are asserted: keep-alive, pipelining, a head split across packets, HEAD, Expect: 100-continue, the size limits, the idle timeout, and what happens when the client hangs up or the handler throws. One test drives it with HttpClient so a real client has to be satisfied too. CI runs the same suite on every push to main and every pull request.

## How it fits together

HttpServer owns the listening Socket and accepts clients. Each one becomes a Connection, which is the file to read: it receives into a buffer, asks RequestParser whether a complete head has arrived yet, parses it, reads the body by Content-Length or through ChunkedDecoder (sending 100 Continue first if the client asked for it), builds an HttpRequest, calls the handler, and writes the HttpResponse back with the headers the server owns (Date, Server, Content-Length, Connection). Then it decides whether to loop: 1.1 stays open unless the client sent Connection: close, 1.0 closes unless it sent keep-alive, and whatever is left in the buffer after one request is the start of the next, which is all pipelining is. RequestParser applies the RFC 9112 framing rules, so Content-Length alongside Transfer-Encoding is rejected, repeated Content-Length values must agree, chunked must be the final coding, and there are caps on the target length (414) and the number of headers (431). HttpServerOptions holds the rest of the limits: 16 KB of head before a 431, 10 MB of body before a 413, 15 seconds idle, 30 seconds to finish sending a request, 1,000 requests per connection. Router and StaticFiles are ordinary handlers the server doesn't know exist. Router answers 405 with Allow and serves HEAD from the GET handler, and StaticFiles can't be walked out of its directory and does Last-Modified and 304. Each connection is one async task, not a thread, so idle keep-alive connections are cheap.

## What I learnt

- A request is not a unit the network gives you. TCP hands over an arbitrary slice of bytes, so the head can arrive in three pieces or two requests can arrive in one, and everything has to be written as "consume what you can, wait for more".
- Keep-alive is just not closing the socket. The hard part is agreeing where each message ends, which is why the framing rules matter so much.
- If two servers in a chain disagree about where a body ends, an attacker can hide a second request inside the first. That is what the Content-Length and Transfer-Encoding rules are for.
- Without an idle timeout one client can hold a connection forever, and without a request timeout it can trickle one byte a minute.
- HEAD means "the headers you would have sent for GET", so Content-Length is the body's length even though no body follows.
- Testing at the byte level catches things a client library would paper over, like a missing CRLF or a response written before the body was fully read.

## Rough edges

No TLS, no HTTP/2, no compression, and no streaming responses: bodies are built in memory. Request targets get percent-decoding and nothing else, and the static file handler does not follow symlinks safely. All of those are left out on purpose.
