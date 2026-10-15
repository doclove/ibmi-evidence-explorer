using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace IbmiEvidenceExplorer.Core;

public static class Utf8Text
{
    public static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Strict UTF-8 check. On failure reports the offset of the first invalid byte.</summary>
    public static bool TryValidate(ReadOnlySpan<byte> bytes, out long invalidOffset)
    {
        if (Utf8.IsValid(bytes))
        {
            invalidOffset = -1;
            return true;
        }

        var offset = 0;
        while (offset < bytes.Length)
        {
            if (System.Text.Rune.DecodeFromUtf8(bytes[offset..], out _, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                break;
            }

            offset += consumed;
        }

        invalidOffset = offset;
        return false;
    }

    public static string Decode(ReadOnlySpan<byte> bytes) => Strict.GetString(bytes);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// The byte range of one source line. Offsets index the original stored bytes; the range excludes
/// the line terminator. A leading UTF-8 BOM is excluded from line 1.
/// </summary>
public sealed record SourceLine(int Number, long ByteOffset, int ByteLength, int TerminatorLength, string Sha256);

/// <summary>
/// Deterministic line splitting over raw bytes. Terminators are LF or CRLF; a lone CR is content.
/// An empty file has zero lines; a final terminator does not start an extra empty line.
/// </summary>
public static class SourceLines
{
    public static IReadOnlyList<SourceLine> Index(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<SourceLine>();
        var start = bytes.StartsWith(Utf8Text.Bom) ? Utf8Text.Bom.Length : 0;
        var position = start;
        while (position < bytes.Length)
        {
            var newline = bytes[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                lines.Add(Create(bytes, lines.Count + 1, position, bytes.Length - position, 0));
                break;
            }

            var end = position + newline;
            var crlf = end > position && bytes[end - 1] == (byte)'\r';
            var length = crlf ? newline - 1 : newline;
            lines.Add(Create(bytes, lines.Count + 1, position, length, crlf ? 2 : 1));
            position = end + 1;
        }

        return lines;
    }

    /// <summary>1-based line number containing the byte offset (used for diagnostics only).</summary>
    public static long LineNumberAt(ReadOnlySpan<byte> bytes, long offset)
    {
        var bounded = (int)Math.Min(offset, bytes.Length);
        return bytes[..bounded].Count((byte)'\n') + 1;
    }

    private static SourceLine Create(ReadOnlySpan<byte> bytes, int number, int offset, int length, int terminator) =>
        new(number, offset, length, terminator, Utf8Text.Sha256Hex(bytes.Slice(offset, length)));
}
