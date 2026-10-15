using System.Text;

namespace IbmiEvidenceExplorer.Core.Preparation;

/// <summary>The prepared UTF-8 bytes, or the typed reason the input cannot be converted.</summary>
public sealed record TranscodeResult(byte[]? Bytes, int RecordCount, string? Transform, PreparationReason? Reason, long? ReasonOffset)
{
    public bool Succeeded => Bytes is not null;

    internal static TranscodeResult Ok(byte[] bytes, int records, string transform) => new(bytes, records, transform, null, null);

    internal static TranscodeResult Fail(PreparationReason reason, long? offset = null) => new(null, 0, null, reason, offset);
}

/// <summary>
/// Converts one explicitly described input into F0-compatible UTF-8 without guessing and without
/// replacement characters. Prepared line N is always original record N:
/// <list type="bullet">
/// <item><c>lines</c> + 1208: strict UTF-8; an initial BOM is removed; all other bytes, including
/// LF/CRLF terminators, are unchanged, so lines split exactly as F0 splits the original.</item>
/// <item><c>fixed-source-data</c> + 1208: strict UTF-8; an initial BOM is removed; the remaining
/// Unicode scalar values form exact 80-value records joined by LF (no trailing LF).</item>
/// <item><c>fixed-source-data</c> + 37 or 1140: exact 80-byte records, each byte mapped by a built-in
/// table, joined by LF. Only the printable range 0x40-0xFE is mapped; control code points are rejected.</item>
/// </list>
/// Any other CCSID, layout, or width is rejected. An empty input has zero records.
/// </summary>
public static class SourceTranscoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// CCSID 37 for bytes 0x40-0xFE (index 0 is byte 0x40). Generated from the IBM037 code page and
    /// cross-checked by tests against the .NET code-page provider on every platform.
    /// </summary>
    private static readonly ushort[] Ebcdic037Printable =
    [
        0x0020, 0x00A0, 0x00E2, 0x00E4, 0x00E0, 0x00E1, 0x00E3, 0x00E5, 0x00E7, 0x00F1, 0x00A2, 0x002E, 0x003C, 0x0028, 0x002B, 0x007C,
        0x0026, 0x00E9, 0x00EA, 0x00EB, 0x00E8, 0x00ED, 0x00EE, 0x00EF, 0x00EC, 0x00DF, 0x0021, 0x0024, 0x002A, 0x0029, 0x003B, 0x00AC,
        0x002D, 0x002F, 0x00C2, 0x00C4, 0x00C0, 0x00C1, 0x00C3, 0x00C5, 0x00C7, 0x00D1, 0x00A6, 0x002C, 0x0025, 0x005F, 0x003E, 0x003F,
        0x00F8, 0x00C9, 0x00CA, 0x00CB, 0x00C8, 0x00CD, 0x00CE, 0x00CF, 0x00CC, 0x0060, 0x003A, 0x0023, 0x0040, 0x0027, 0x003D, 0x0022,
        0x00D8, 0x0061, 0x0062, 0x0063, 0x0064, 0x0065, 0x0066, 0x0067, 0x0068, 0x0069, 0x00AB, 0x00BB, 0x00F0, 0x00FD, 0x00FE, 0x00B1,
        0x00B0, 0x006A, 0x006B, 0x006C, 0x006D, 0x006E, 0x006F, 0x0070, 0x0071, 0x0072, 0x00AA, 0x00BA, 0x00E6, 0x00B8, 0x00C6, 0x00A4,
        0x00B5, 0x007E, 0x0073, 0x0074, 0x0075, 0x0076, 0x0077, 0x0078, 0x0079, 0x007A, 0x00A1, 0x00BF, 0x00D0, 0x00DD, 0x00DE, 0x00AE,
        0x005E, 0x00A3, 0x00A5, 0x00B7, 0x00A9, 0x00A7, 0x00B6, 0x00BC, 0x00BD, 0x00BE, 0x005B, 0x005D, 0x00AF, 0x00A8, 0x00B4, 0x00D7,
        0x007B, 0x0041, 0x0042, 0x0043, 0x0044, 0x0045, 0x0046, 0x0047, 0x0048, 0x0049, 0x00AD, 0x00F4, 0x00F6, 0x00F2, 0x00F3, 0x00F5,
        0x007D, 0x004A, 0x004B, 0x004C, 0x004D, 0x004E, 0x004F, 0x0050, 0x0051, 0x0052, 0x00B9, 0x00FB, 0x00FC, 0x00F9, 0x00FA, 0x00FF,
        0x005C, 0x00F7, 0x0053, 0x0054, 0x0055, 0x0056, 0x0057, 0x0058, 0x0059, 0x005A, 0x00B2, 0x00D4, 0x00D6, 0x00D2, 0x00D3, 0x00D5,
        0x0030, 0x0031, 0x0032, 0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038, 0x0039, 0x00B3, 0x00DB, 0x00DC, 0x00D9, 0x00DA,
    ];

    private const byte FirstPrintable = 0x40;
    private const byte LastPrintable = 0xFE;

    /// <summary>EBCDIC CR, NL, and LF: line separators that must not appear inside fixed records.</summary>
    private static readonly byte[] EbcdicSeparators = [0x0D, 0x15, 0x25];

    public static TranscodeResult Transcode(ReadOnlySpan<byte> input, int ccsid, RecordLayout layout, int? recordWidth)
    {
        if (!PreparationFormats.SupportedCcsids.Contains(ccsid))
        {
            return TranscodeResult.Fail(PreparationReason.UnsupportedCcsid);
        }

        if (layout == RecordLayout.RawSourceRecord || (layout == RecordLayout.Lines && ccsid != PreparationFormats.Utf8Ccsid))
        {
            return TranscodeResult.Fail(PreparationReason.UnsupportedRecordLayout);
        }

        if (layout == RecordLayout.Lines)
        {
            return recordWidth is null ? Utf8Lines(input) : TranscodeResult.Fail(PreparationReason.UnsupportedRecordWidth);
        }

        if (recordWidth != PreparationFormats.SourceDataRecordWidth)
        {
            return TranscodeResult.Fail(PreparationReason.UnsupportedRecordWidth);
        }

        return ccsid == PreparationFormats.Utf8Ccsid ? Utf8Fixed(input) : EbcdicFixed(input, ccsid);
    }

    /// <summary>The Unicode scalar for one EBCDIC byte, or null when the byte is outside the mapped printable range.</summary>
    public static char? MapEbcdic(byte value, int ccsid)
    {
        if (value is < FirstPrintable or > LastPrintable)
        {
            return null;
        }

        // CCSID 1140 is CCSID 37 with the euro sign in place of the currency sign at 0x9F.
        return ccsid == 1140 && value == 0x9F ? '€' : (char)Ebcdic037Printable[value - FirstPrintable];
    }

    private static TranscodeResult Utf8Lines(ReadOnlySpan<byte> input)
    {
        if (!Utf8Text.TryValidate(input, out var invalid))
        {
            return TranscodeResult.Fail(PreparationReason.InvalidEncoding, invalid);
        }

        var body = input.StartsWith(Utf8Text.Bom) ? input[Utf8Text.Bom.Length..] : input;
        return TranscodeResult.Ok(body.ToArray(), SourceLines.Index(body).Count, "utf8-lines");
    }

    private static TranscodeResult Utf8Fixed(ReadOnlySpan<byte> input)
    {
        if (!Utf8Text.TryValidate(input, out var invalid))
        {
            return TranscodeResult.Fail(PreparationReason.InvalidEncoding, invalid);
        }

        var bomLength = input.StartsWith(Utf8Text.Bom) ? Utf8Text.Bom.Length : 0;
        var text = StrictUtf8.GetString(input[bomLength..]);
        var width = PreparationFormats.SourceDataRecordWidth;
        var output = new StringBuilder(text.Length + (text.Length / width) + 1);
        var scalars = 0;
        var offset = (long)bomLength;
        var recordStart = offset;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsLineSeparator(rune.Value))
            {
                return TranscodeResult.Fail(PreparationReason.EmbeddedLineSeparator, offset);
            }

            if (scalars > 0 && scalars % width == 0)
            {
                output.Append('\n');
                recordStart = offset;
            }

            output.Append(rune.ToString());
            scalars++;
            offset += rune.Utf8SequenceLength;
        }

        if (scalars % width != 0)
        {
            return TranscodeResult.Fail(PreparationReason.ShortFinalRecord, recordStart);
        }

        return TranscodeResult.Ok(StrictUtf8.GetBytes(output.ToString()), scalars / width, "utf8-fixed-80");
    }

    private static TranscodeResult EbcdicFixed(ReadOnlySpan<byte> input, int ccsid)
    {
        var width = PreparationFormats.SourceDataRecordWidth;
        if (input.Length % width != 0)
        {
            return TranscodeResult.Fail(PreparationReason.ShortFinalRecord, input.Length - (input.Length % width));
        }

        var output = new StringBuilder(input.Length + (input.Length / width));
        for (var i = 0; i < input.Length; i++)
        {
            if (i > 0 && i % width == 0)
            {
                output.Append('\n');
            }

            if (MapEbcdic(input[i], ccsid) is not { } mapped)
            {
                return TranscodeResult.Fail(
                    EbcdicSeparators.Contains(input[i]) ? PreparationReason.EmbeddedLineSeparator : PreparationReason.UnmappableCharacter, i);
            }

            output.Append(mapped);
        }

        return TranscodeResult.Ok(StrictUtf8.GetBytes(output.ToString()), input.Length / width,
            ccsid == 37 ? "ebcdic-37-fixed-80" : "ebcdic-1140-fixed-80");
    }

    /// <summary>LF, CR, NEL, and the Unicode line and paragraph separators.</summary>
    private static bool IsLineSeparator(int scalar) => scalar is '\n' or '\r' or 0x85 or 0x2028 or 0x2029;
}
