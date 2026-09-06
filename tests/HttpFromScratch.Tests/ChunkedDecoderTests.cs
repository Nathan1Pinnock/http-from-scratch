using System.Text;

namespace HttpFromScratch.Tests;

public class ChunkedDecoderTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string Text(ChunkedDecoder decoder) => Encoding.ASCII.GetString(decoder.Body.Span);

    [Fact]
    public void DecodesASingleChunk()
    {
        var decoder = new ChunkedDecoder(1024);
        byte[] input = Ascii("5\r\nhello\r\n0\r\n\r\n");

        Assert.Equal(input.Length, decoder.Feed(input));
        Assert.True(decoder.IsComplete);
        Assert.Equal("hello", Text(decoder));
    }

    [Fact]
    public void DecodesSeveralChunks_IgnoringExtensionsAndTrailers()
    {
        var decoder = new ChunkedDecoder(1024);
        byte[] input = Ascii("3;name=value\r\nabc\r\n2\r\nde\r\n0\r\nX-Trailer: v\r\n\r\n");

        Assert.Equal(input.Length, decoder.Feed(input));
        Assert.True(decoder.IsComplete);
        Assert.Equal("abcde", Text(decoder));
    }

    [Fact]
    public void HexSizesAreCaseInsensitive()
    {
        var decoder = new ChunkedDecoder(1024);
        decoder.Feed(Ascii("A\r\n0123456789\r\na\r\nabcdefghij\r\n0\r\n\r\n"));

        Assert.True(decoder.IsComplete);
        Assert.Equal("0123456789abcdefghij", Text(decoder));
    }

    [Fact]
    public void FeedingOneByteAtATime_GivesTheSameResult()
    {
        var decoder = new ChunkedDecoder(1024);
        var pending = new List<byte>();
        foreach (byte b in Ascii("3\r\nabc\r\n2\r\nde\r\n0\r\n\r\n"))
        {
            pending.Add(b);
            int consumed = decoder.Feed(pending.ToArray());
            pending.RemoveRange(0, consumed);
        }

        Assert.True(decoder.IsComplete);
        Assert.Empty(pending);
        Assert.Equal("abcde", Text(decoder));
    }

    [Fact]
    public void LeavesBytesAfterTheEndAlone()
    {
        var decoder = new ChunkedDecoder(1024);

        Assert.Equal(5, decoder.Feed(Ascii("0\r\n\r\nGET / HTTP/1.1")));
        Assert.True(decoder.IsComplete);
    }

    [Fact]
    public void ConsumesWhatItCan_AndWaitsForTheRest()
    {
        var decoder = new ChunkedDecoder(1024);

        Assert.Equal(6, decoder.Feed(Ascii("5\r\nhel")));
        Assert.False(decoder.IsComplete);
        Assert.Equal("hel", Text(decoder));
        Assert.Equal(0, decoder.Feed(Ascii("")));
    }

    [Theory]
    [InlineData("zz\r\n")]
    [InlineData("\r\n")]
    [InlineData("5 x\r\n")]
    [InlineData("5\r\nhelloXX")]
    public void RejectsMalformedInput(string input)
    {
        var decoder = new ChunkedDecoder(1024);

        var ex = Assert.Throws<HttpParseException>(() => decoder.Feed(Ascii(input)));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void EnforcesTheBodyLimit_BeforeReadingTheChunk()
    {
        var decoder = new ChunkedDecoder(maxBodyBytes: 4);

        var ex = Assert.Throws<HttpParseException>(() => decoder.Feed(Ascii("5\r\n")));
        Assert.Equal(413, ex.StatusCode);
    }

    [Fact]
    public void EnforcesTheBodyLimit_AcrossChunks()
    {
        var decoder = new ChunkedDecoder(maxBodyBytes: 4);
        decoder.Feed(Ascii("3\r\nabc\r\n"));

        var ex = Assert.Throws<HttpParseException>(() => decoder.Feed(Ascii("2\r\n")));
        Assert.Equal(413, ex.StatusCode);
    }
}
