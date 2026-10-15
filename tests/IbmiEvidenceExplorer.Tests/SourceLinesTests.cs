using System.Security.Cryptography;
using System.Text;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

public class SourceLinesTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void EmptyFileHasZeroLines() => Assert.Empty(SourceLines.Index([]));

    [Theory]
    [InlineData("abc", 1)]
    [InlineData("abc\n", 1)]
    [InlineData("\n", 1)]
    [InlineData("\n\n", 2)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("a\rb", 1)]
    public void CountsLines(string text, int expected) => Assert.Equal(expected, SourceLines.Index(Bytes(text)).Count);

    [Fact]
    public void LineRangesExcludeTerminatorsAndResolveToExactBytes()
    {
        var bytes = Bytes("first\r\nsecond\n\nlast");
        var lines = SourceLines.Index(bytes);

        Assert.Equal(["first", "second", "", "last"], lines.Select(l => Encoding.UTF8.GetString(bytes, (int)l.ByteOffset, l.ByteLength)));
        Assert.Equal([2, 1, 1, 0], lines.Select(l => l.TerminatorLength));
        Assert.Equal([1, 2, 3, 4], lines.Select(l => l.Number));

        // Concatenating every line and terminator reproduces the original bytes exactly.
        var rebuilt = lines.SelectMany(l => bytes.Skip((int)l.ByteOffset).Take(l.ByteLength + l.TerminatorLength)).ToArray();
        Assert.Equal(bytes, rebuilt);
    }

    [Fact]
    public void LineHashIsSha256OfLineBytes()
    {
        var line = Assert.Single(SourceLines.Index(Bytes("DCL VAR(&X)\n")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes("DCL VAR(&X)"))), line.Sha256);
    }

    [Fact]
    public void ByteOrderMarkIsExcludedFromFirstLine()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("x\ny")).ToArray();
        var lines = SourceLines.Index(bytes);
        Assert.Equal(3, lines[0].ByteOffset);
        Assert.Equal(1, lines[0].ByteLength);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ManyLinesAreIndexedDeterministically()
    {
        var bytes = Bytes(string.Concat(Enumerable.Range(1, 5000).Select(i => $"line {i}\n")));
        var first = SourceLines.Index(bytes);
        var second = SourceLines.Index(bytes.ToArray());
        Assert.Equal(5000, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MultibyteCharactersAreCountedInBytes()
    {
        var bytes = Bytes("é€😀\nx");
        var lines = SourceLines.Index(bytes);
        Assert.Equal(2 + 3 + 4, lines[0].ByteLength);
        Assert.Equal(10, lines[1].ByteOffset);
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0xFF }, 1)]
    [InlineData(new byte[] { 0x41, 0xC3 }, 1)]
    [InlineData(new byte[] { 0xC0, 0xAF }, 0)]
    [InlineData(new byte[] { 0x41, 0x42, 0xED, 0xA0, 0x80 }, 2)]
    [InlineData(new byte[] { 0x80 }, 0)]
    public void ReportsFirstInvalidUtf8Offset(byte[] bytes, long expectedOffset)
    {
        Assert.False(Utf8Text.TryValidate(bytes, out var offset));
        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public void AcceptsValidUtf8() => Assert.True(Utf8Text.TryValidate(Bytes("ok é € 😀"), out _));

    [Fact]
    public void LineNumberAtOffsetCountsNewlines() => Assert.Equal(3, SourceLines.LineNumberAt(Bytes("a\nb\nc"), 4));
}
