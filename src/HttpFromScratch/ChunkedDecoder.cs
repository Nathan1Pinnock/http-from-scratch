using System.Buffers;

namespace HttpFromScratch;

/// <summary>
/// Decodes a chunked transfer coding incrementally. Feed it whatever bytes have
/// arrived so far; it reports how many it consumed and whether the body is
/// complete, and bytes it did not consume must be offered again next time.
/// Chunk extensions are ignored and trailer fields are skipped.
/// </summary>
public sealed class ChunkedDecoder(long maxBodyBytes)
{
    private enum State
    {
        Size,
        Data,
        DataCrlf,
        Trailers,
        Done,
    }

    private readonly ArrayBufferWriter<byte> _body = new();
    private State _state = State.Size;
    private long _remaining;

    public bool IsComplete => _state == State.Done;

    /// <summary>The decoded body so far.</summary>
    public ReadOnlyMemory<byte> Body => _body.WrittenMemory;

    /// <summary>Consumes as much of <paramref name="input"/> as it can and returns how many bytes that was.</summary>
    public int Feed(ReadOnlySpan<byte> input)
    {
        int consumed = 0;
        while (consumed < input.Length && _state != State.Done)
        {
            var rest = input[consumed..];
            switch (_state)
            {
                case State.Size:
                {
                    int eol = rest.IndexOf("\r\n"u8);
                    if (eol < 0)
                    {
                        if (rest.Length > 1024)
                        {
                            throw new HttpParseException(400, "Chunk size line is too long.");
                        }

                        return consumed; // need more bytes to finish the line
                    }

                    _remaining = ParseChunkSize(rest[..eol]);
                    consumed += eol + 2;
                    if (_remaining == 0)
                    {
                        _state = State.Trailers;
                    }
                    else
                    {
                        if (_body.WrittenCount + _remaining > maxBodyBytes)
                        {
                            throw new HttpParseException(413, "The body is larger than the server allows.");
                        }

                        _state = State.Data;
                    }

                    break;
                }

                case State.Data:
                {
                    int take = (int)Math.Min(_remaining, rest.Length);
                    _body.Write(rest[..take]);
                    _remaining -= take;
                    consumed += take;
                    if (_remaining == 0)
                    {
                        _state = State.DataCrlf;
                    }

                    break;
                }

                case State.DataCrlf:
                {
                    if (rest.Length < 2)
                    {
                        return consumed;
                    }

                    if (rest[0] != '\r' || rest[1] != '\n')
                    {
                        throw new HttpParseException(400, "Chunk data must be followed by CRLF.");
                    }

                    consumed += 2;
                    _state = State.Size;
                    break;
                }

                case State.Trailers:
                {
                    // After the last chunk comes either an empty line, or trailer
                    // fields followed by an empty line. We skip the fields.
                    int eol = rest.IndexOf("\r\n"u8);
                    if (eol < 0)
                    {
                        if (rest.Length > 8 * 1024)
                        {
                            throw new HttpParseException(400, "Trailer line is too long.");
                        }

                        return consumed;
                    }

                    consumed += eol + 2;
                    if (eol == 0)
                    {
                        _state = State.Done;
                    }

                    break;
                }

                default:
                    return consumed;
            }
        }

        return consumed;
    }

    private static long ParseChunkSize(ReadOnlySpan<byte> line)
    {
        // chunk-size [ chunk-ext ]: hex digits, then optionally ";" and extensions we ignore.
        int digits = 0;
        while (digits < line.Length && HexValue(line[digits]) >= 0)
        {
            digits++;
        }

        if (digits is 0 or > 15)
        {
            throw new HttpParseException(400, "Bad chunk size.");
        }

        var rest = line[digits..].TrimStart(" \t"u8);
        if (!rest.IsEmpty && rest[0] != ';')
        {
            throw new HttpParseException(400, "Bad chunk size.");
        }

        long size = 0;
        foreach (byte b in line[..digits])
        {
            size = size * 16 + HexValue(b);
        }

        return size;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
