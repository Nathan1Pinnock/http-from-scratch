# http-from-scratch

An HTTP/1.1 server written in C# on top of raw TCP sockets. No Kestrel, no HttpListener, no framework: the listener, the request parser, keep-alive, chunked bodies, the router and the static file handler are all in this repo, and real clients (curl, browsers, `HttpClient`) talk to it without knowing the difference.

I use ASP.NET Core every day at TaskPool and wanted to know what Kestrel is actually doing underneath: how bytes off a socket become a request object, what keep-alive really means on the wire, why `Content-Length` and `Transfer-Encoding` can't both be trusted, and where the sharp edges are that a framework hides. Building one is the fastest way to find out.

## Run it

    dotnet run --project src/HttpFromScratch.Host
    curl -v http://localhost:8080/

That serves the sample site in `src/HttpFromScratch.Host/www`, plus `/hello` and `POST /echo` from handlers in code, and prints an access log. `--port`, `--root` and `--bind` change the defaults.

## Test it

    dotnet test

101 tests. The parser and the chunked decoder are tested as pure functions over byte arrays. The server tests start a real server on a random loopback port and talk to it with a deliberately dumb socket client, so the exact bytes on the wire are asserted: keep-alive, pipelining, a head split across packets, HEAD, `Expect: 100-continue`, the size limits, the idle timeout, and what happens when the client hangs up or the handler throws. One test drives it with `HttpClient` so a real client has to be satisfied too. CI runs the same suite on every push.

## What it does

- HTTP/1.0 and 1.1 with the right persistence defaults: 1.1 stays open unless `Connection: close`, 1.0 closes unless `Connection: keep-alive`.
- Pipelining, without any special code: whatever is left in the buffer after one request is the start of the next.
- Request bodies by `Content-Length` or `Transfer-Encoding: chunked`, and `100 Continue` when the client asks for it.
- The RFC 9112 rules that stop request smuggling: `Content-Length` and `Transfer-Encoding` together are rejected, repeated `Content-Length` values must agree, chunked must be the final coding.
- Limits everywhere a client could hurt you: head size (431), body size (413), target length (414), header count, idle and request timeouts, requests per connection.
- A router with 405 and `Allow`, HEAD served by GET, and a static file handler that cannot be walked out of its directory, with `Last-Modified` and 304.
- One async task per connection, not a thread, so idle keep-alive connections are cheap.

## How it fits together

`HttpServer` owns the listening `Socket` and accepts clients; each one becomes a `Connection`, which is the interesting part. It receives into a buffer, asks `RequestParser` whether a complete head is there yet, parses it, reads the body (`ChunkedDecoder` handles the chunked case incrementally), builds an `HttpRequest`, calls the handler, and writes the `HttpResponse` back with the headers the server is responsible for (`Date`, `Server`, `Content-Length`, `Connection`). Then it decides whether to loop. `Router` and `StaticFiles` are ordinary handlers; the server does not know they exist.

## What I learnt

- A "request" is not a unit the network gives you. TCP hands over an arbitrary slice of bytes, so the head can arrive in three pieces or two requests can arrive in one. Everything has to be written as "consume what you can, wait for more".
- Keep-alive is just not closing the socket. The hard part is agreeing on where each message ends, which is why the framing rules matter so much.
- The `Content-Length` versus `Transfer-Encoding` rules are not pedantry. If two servers in a chain disagree about where a body ends, an attacker can hide a second request inside the first.
- Timeouts are part of correctness. Without an idle timeout, one client can hold a connection forever; without a request timeout, a client can trickle one byte a minute.
- `HEAD` means "the headers you would have sent for GET", so `Content-Length` is the body's length even though no body follows.
- Testing at the byte level catches things a client library would paper over, like a missing CRLF or a response written before the body was fully read.

## What it does not do, on purpose

No TLS, no HTTP/2, no compression, no streaming responses (bodies are built in memory), no request-line normalisation beyond percent-decoding, and it does not follow symlinks safely. Each of those is a project of its own.
