namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>
/// Fixed-format physical-file DDS, one 80-column line per entry: position 6 form type (A or
/// blank), 7 comment '*', 7-16 conditioning (must be blank), 17 name type (R record, blank field,
/// K key), 19-28 name, 29 reference, 30-34 length, 35 data type, 36-37 decimals, 38 usage, 39-44
/// location, 45-80 keywords. Keywords outside a small descriptive set (TEXT, COLHDG, ...) and
/// other entry types (select/omit, join, help) are reported as unsupported.
/// </summary>
internal static class DdsParser
{
    private const int MaxColumns = 80;
    private const string DataTypes = "APSBFHLTZOJEG5";
    private static readonly string[] DescriptiveKeywords = ["TEXT", "COLHDG", "EDTCDE", "EDTWRD", "UNIQUE", "DFT", "ALWNULL", "VARLEN", "DESCEND"];

    public static void Parse(IReadOnlyList<string> lines, ParseContext context)
    {
        string? record = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var number = index + 1;
            var text = lines[index];
            if (Chars.IsBlankLine(text))
            {
                continue;
            }

            if (text.Any(c => c == '\t' || char.IsControl(c)))
            {
                context.Meaningful(number);
                context.Problem(ParseDiagnosticCode.InvalidCharacter, number,
                    "Fixed-format line contains a tab or control character (for example a lone CR); column positions are unreliable, so it was not parsed.");
                continue;
            }

            if (text.Length > MaxColumns)
            {
                context.Meaningful(number);
                context.Problem(ParseDiagnosticCode.MalformedStatement, number, $"Fixed-format line is longer than {MaxColumns} columns.");
                continue;
            }

            var line = text.PadRight(MaxColumns);
            var formType = line[5];
            if (formType is not ('A' or 'a' or ' '))
            {
                context.Meaningful(number);
                context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Position 6 must be A or blank.");
                continue;
            }

            if (line[6] == '*' || string.IsNullOrWhiteSpace(line[5..]))
            {
                continue; // comment, or a line holding only a sequence number
            }

            context.Meaningful(number);
            var keywords = Keywords(line[44..].Trim());
            foreach (var keyword in keywords ?? [])
            {
                if (!DescriptiveKeywords.Contains(keyword))
                {
                    context.Gap(SyntaxGapKind.DdsKeyword, keyword, number);
                }
            }

            if (!string.IsNullOrWhiteSpace(line[6..16]))
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, number, "Conditioning (positions 7-16) is not supported in F1.");
                continue;
            }

            var nameType = char.ToUpperInvariant(line[16]);
            var nameArea = line[18..28];
            var name = nameArea.TrimEnd();
            if (line[17] != ' ' || (name.Length > 0 && (name[0] == ' ' || name.Contains(' ', StringComparison.Ordinal) || !IsName(name))))
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Name in positions 19-28 must be a left-aligned DDS name.");
                continue;
            }

            if (keywords is null)
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, number,
                    "Keywords in positions 45-80 are unbalanced or continue onto the next line; continuation is not supported in F1.");
                continue;
            }

            var definitionBlank = string.IsNullOrWhiteSpace(line[28..44]);
            switch (nameType)
            {
                case 'R' when name.Length > 0 && definitionBlank:
                    record = name;
                    context.Artifacts.Add(new(ArtifactKind.Record, name, null, number));
                    Accept(context, number, keywords);
                    break;
                case 'K' when name.Length > 0 && definitionBlank:
                    if (record is null)
                    {
                        context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Key appears before any record format.");
                        break;
                    }

                    context.References.Add(new(EdgeKind.KeysField, number, ArtifactKind.Record, record, name, null, name, null));
                    Accept(context, number, keywords);
                    break;
                case ' ' when name.Length > 0:
                    Field(context, number, line, name, record, keywords);
                    break;
                case ' ' when definitionBlank:
                    Accept(context, number, keywords); // file-level or continued keywords
                    break;
                case 'R' or 'K' or ' ':
                    context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Entry has a missing name or unexpected definition columns.");
                    break;
                case 'S' or 'O' or 'J' or 'H':
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, number, "Select/omit, join, and help entries are outside the F1 DDS subset.");
                    break;
                default:
                    context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Position 17 must be R, K, or blank in the F1 DDS subset.");
                    break;
            }
        }
    }

    private static void Field(ParseContext context, int number, string line, string name, string? record, List<string> keywords)
    {
        if (record is null)
        {
            context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Field appears before any record format.");
            return;
        }

        var reference = line[28];
        var length = line[29..34].Trim();
        var type = char.ToUpperInvariant(line[34]);
        var decimals = line[35..37].Trim();
        var usage = line[37];
        if (reference is not (' ' or 'R' or 'r') || !length.All(Chars.IsDigit) || (type != ' ' && !DataTypes.Contains(type, StringComparison.Ordinal))
            || !decimals.All(Chars.IsDigit) || !string.IsNullOrWhiteSpace(line[38..44]) || usage is not (' ' or 'B' or 'b'))
        {
            context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Field definition columns 29-44 are not valid for a physical-file field.");
            return;
        }

        context.Artifacts.Add(new(ArtifactKind.Field, name, record, number));
        if (reference != ' ' || (length.Length == 0 && type == ' '))
        {
            context.Problem(ParseDiagnosticCode.UnsupportedForm, number, "Field takes its definition from a reference file; reference resolution is not supported in F1.");
            context.Understood();
            return;
        }

        Accept(context, number, keywords);
    }

    /// <summary>Counts the entry as understood, unless it uses a keyword outside the descriptive set.</summary>
    private static void Accept(ParseContext context, int number, List<string> keywords)
    {
        context.Understood();
        if (keywords.Any(k => !DescriptiveKeywords.Contains(k)))
        {
            context.Problem(ParseDiagnosticCode.UnsupportedForm, number,
                "Entry uses a keyword outside the F1 descriptive set (TEXT, COLHDG, EDTCDE, EDTWRD, UNIQUE, DFT, ALWNULL, VARLEN, DESCEND); its effect is not indexed.");
        }
    }

    /// <summary>Keyword names in positions 45-80: NAME or NAME(...) with quoted values; null if unbalanced or unterminated.</summary>
    private static List<string>? Keywords(string area)
    {
        var names = new List<string>();
        var i = 0;
        while (i < area.Length)
        {
            if (area[i] == ' ')
            {
                i++;
                continue;
            }

            if (!char.IsAsciiLetter(area[i]))
            {
                return null;
            }

            var start = i;
            while (i < area.Length && Chars.IsNamePart(area[i]))
            {
                i++;
            }

            names.Add(area[start..i].ToUpperInvariant());
            if (i < area.Length && area[i] == '(')
            {
                var depth = 0;
                var inString = false;
                for (; i < area.Length; i++)
                {
                    var c = area[i];
                    if (inString)
                    {
                        if (c == '\'' && i + 1 < area.Length && area[i + 1] == '\'')
                        {
                            i++; // doubled quote inside a literal
                        }
                        else if (c == '\'')
                        {
                            inString = false;
                        }
                    }
                    else if (c == '\'')
                    {
                        inString = true;
                    }
                    else if (c == '(')
                    {
                        depth++;
                    }
                    else if (c == ')' && --depth == 0)
                    {
                        i++;
                        break;
                    }
                }

                if (depth != 0 || inString)
                {
                    return null;
                }
            }
        }

        return names;
    }

    private static bool IsName(string name) =>
        name.Length is >= 1 and <= 10 && Chars.IsNameStart(name[0]) && name.All(Chars.IsNamePart);
}
