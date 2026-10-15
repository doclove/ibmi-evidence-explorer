using System.Text.RegularExpressions;

namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>
/// A deliberately narrow slice of fixed-format RPG IV (F1.5, D-014), by column position as in IBM's
/// RPG IV specifications. Positions 1-5 (sequence) and 81-100 (comments) are ignored; a line longer
/// than 100 columns, or holding a tab or control character, is not parsed.
/// <list type="bullet">
/// <item>Position 7 <c>*</c> is a comment; <c>/EJECT</c>, <c>/TITLE</c>, <c>/SPACE</c> are listing
/// directives; other directives (<c>/COPY</c>, <c>/IF</c>, ...) and embedded <c>/FREE</c> blocks are
/// diagnosed and not followed; <c>**</c> in positions 1-2 starts compile-time data.</item>
/// <item><b>H</b> (control) specifications are accepted without facts.</item>
/// <item><b>F</b> (file description): name 7-16, type 17 (I O U C), designation 18, end of file 19,
/// addition 20, sequence 21, format 22 (E or F), record length 23-27, limits 28, key length 29-33,
/// record address type 34, organization 35, device 36-42, reserved 43, keywords 44-80 with
/// keyword-only continuation lines. A valid entry yields a declared file and a <c>uses-file</c>
/// reference. Program-described files, SPECIAL/SEQ devices, and EXTFILE, EXTDESC, LIKEFILE, TEMPLATE,
/// or HANDLER leave the reference unresolved.</item>
/// <item><b>C</b> (traditional calculation): control level 7-8, indicators 9-11, factor 1 12-25,
/// operation 26-35 (left-adjusted, optional extender), factor 2 36-49, result 50-63, length 64-68,
/// decimals 69-70, resulting indicators 71-76. Only <c>CALL</c> (a literal program name in factor 2
/// yields a <c>calls-program</c> reference; a field or named constant is dynamic), <c>PARM</c>
/// directly after a <c>CALL</c>, and <c>CHAIN</c> (a <c>uses-file</c> reference only when factor 2
/// names a file declared in this member) are understood.</item>
/// </list>
/// D, I, O, P specifications, other operations, extended factor 2, and free-form statements in
/// positions 8-80 are reported as unsupported, so such members are partial.
/// </summary>
internal static partial class RpgFixedParser
{
    private const int CodeColumns = 80;
    private const int MaxColumns = 100;

    private static readonly string[] FileOverrideKeywords = ["EXTFILE", "EXTDESC", "LIKEFILE", "TEMPLATE", "HANDLER"];
    private static readonly string[] Devices = ["DISK", "PRINTER", "WORKSTN", "SPECIAL", "SEQ"];
    private static readonly string[] DescribedDevices = ["DISK", "PRINTER", "WORKSTN"];
    private static readonly string[] ListingDirectives = ["EJECT", "TITLE", "SPACE"];

    public static void Parse(IReadOnlyList<string> lines, ParseContext context)
    {
        var state = new State(context);
        int? freeBlock = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var number = index + 1;
            var text = lines[index];
            if (Chars.IsBlankLine(text))
            {
                continue;
            }

            if (text.StartsWith("**", StringComparison.Ordinal))
            {
                state.Flush();
                context.Meaningful(number);
                context.Problem(ParseDiagnosticCode.CompileTimeDataUnsupported, number,
                    "Compile-time data section is not supported; the remaining lines were not parsed.");
                break;
            }

            if (text.Any(c => c == '\t' || char.IsControl(c)) || text.Length > MaxColumns)
            {
                state.Flush();
                context.Meaningful(number);
                context.Problem(text.Length > MaxColumns ? ParseDiagnosticCode.MalformedStatement : ParseDiagnosticCode.InvalidCharacter, number,
                    $"Fixed-format line is longer than {MaxColumns} columns or holds a tab or control character; column positions are unreliable, so it was not parsed.");
                continue;
            }

            var line = text.PadRight(MaxColumns)[..CodeColumns];
            if (freeBlock is not null)
            {
                if (Directive(line) == "END-FREE")
                {
                    freeBlock = null;
                }

                continue; // the block was diagnosed once where it began
            }

            if (line[6] == '*' || string.IsNullOrWhiteSpace(line[5..]))
            {
                continue; // comment, or a sequence number only
            }

            if (line[6] == '/')
            {
                var directive = Directive(line);
                if (ListingDirectives.Contains(directive))
                {
                    continue;
                }

                state.Flush();
                context.Meaningful(number);
                if (directive == "FREE")
                {
                    freeBlock = number;
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, number, "Embedded /FREE block is not parsed in the F1.5 fixed-form slice; no facts were taken from it.");
                }
                else
                {
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, number, "Compiler directive (for example /COPY, /INCLUDE, /IF) is not supported; it was not followed.");
                }

                continue;
            }

            var spec = char.ToUpperInvariant(line[5]);
            if (spec == ' ' && line[7..].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue; // free-form comment in positions 8-80
            }

            context.Meaningful(number);
            if (spec != 'F')
            {
                state.Flush();
            }

            switch (spec)
            {
                case 'H':
                    context.Understood();
                    break;
                case 'F':
                    state.FileLine(line, number);
                    break;
                case 'C':
                    state.Calculation(line, number);
                    break;
                case 'D' or 'I' or 'O' or 'P':
                    state.NotCall();
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, number, $"{spec} specification is outside the F1.5 fixed RPG subset; no facts were taken from it.");
                    break;
                case ' ':
                    state.NotCall();
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, number, "Free-form statement in positions 8-80 of a fixed-form member is not parsed in F1.5.");
                    break;
                default:
                    state.NotCall();
                    context.Problem(ParseDiagnosticCode.MalformedStatement, number, "Position 6 must be a specification type (H, F, D, I, C, O, P).");
                    break;
            }
        }

        state.Flush();
        if (freeBlock is { } open)
        {
            context.Problem(ParseDiagnosticCode.MalformedStatement, open, "/FREE is not closed by /END-FREE before the end of the member.");
        }

        state.Finish();
    }

    /// <summary>The directive name after the '/' in position 7, upper-cased (for example COPY, END-FREE).</summary>
    private static string Directive(string line)
    {
        if (line[6] != '/')
        {
            return string.Empty;
        }

        var start = 7;
        var end = start;
        while (end < line.Length && (char.IsAsciiLetterOrDigit(line[end]) || line[end] == '-'))
        {
            end++;
        }

        return line[start..end].ToUpperInvariant();
    }

    private static bool IsRpgName(string name) =>
        name.Length is >= 1 and <= 10 && (char.IsAsciiLetter(name[0]) || name[0] is '$' or '#' or '@')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '$' or '#' or '@' or '_');

    /// <summary>A left-adjusted name filling a column range, followed only by blanks.</summary>
    private static string? Field(string line, int fromColumn, int toColumn)
    {
        var area = line[(fromColumn - 1)..toColumn];
        var name = area.TrimEnd();
        return name.Length == 0 ? string.Empty : area[0] != ' ' && !name.Contains(' ', StringComparison.Ordinal) ? name : null;
    }

    [GeneratedRegex("^(?<op>[A-Z][A-Z0-9-]*)(\\((?<ext>[A-Z]+)\\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Operation();

    [GeneratedRegex("^(?:(?<lib>[A-Z$#@*][A-Z0-9$#@_.]{0,9})/)?(?<name>[A-Za-z0-9$#@_.]{1,10})$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralTarget();

    private sealed class State(ParseContext context)
    {
        private readonly List<(ParsedArtifact Declaration, UnresolvedReason? Reason)> _declaredFiles = [];
        private readonly List<(string File, int Line)> _chains = [];
        private (string Name, int Line, List<string> Keywords, UnresolvedReason? Reason, bool Valid)? _file;
        private bool _afterCall;

        public void NotCall() => _afterCall = false;

        public void FileLine(string line, int number)
        {
            _afterCall = false;
            if (string.IsNullOrWhiteSpace(line[6..43]))
            {
                // A continuation line: keywords only, belonging to the preceding file description.
                if (_file is not { } open)
                {
                    context.Problem(ParseDiagnosticCode.MalformedStatement, number, "F-specification continuation has no preceding file description.");
                    return;
                }

                if (Keywords(line[43..CodeColumns]) is { } more)
                {
                    open.Keywords.AddRange(more);
                    context.Understood();
                }
                else
                {
                    _file = open with { Valid = false };
                    context.Problem(ParseDiagnosticCode.MalformedStatement, number, "F-specification keywords (positions 44-80) are unbalanced; the file description yields no facts.");
                }

                return;
            }

            Flush();
            var name = Field(line, 7, 16);
            var type = char.ToUpperInvariant(line[16]);
            var designation = char.ToUpperInvariant(line[17]);
            var format = char.ToUpperInvariant(line[21]);
            var device = Field(line, 36, 42)?.ToUpperInvariant();
            var valid = name is { Length: > 0 } && IsRpgName(name)
                && type is 'I' or 'O' or 'U' or 'C'
                && designation is ' ' or 'P' or 'S' or 'R' or 'T' or 'F'
                && char.ToUpperInvariant(line[18]) is ' ' or 'E'
                && char.ToUpperInvariant(line[19]) is ' ' or 'A'
                && char.ToUpperInvariant(line[20]) is ' ' or 'A' or 'D'
                && format is 'E' or 'F'
                && IsNumberOrBlank(line[22..27]) && char.ToUpperInvariant(line[27]) is ' ' or 'L' && IsNumberOrBlank(line[28..33])
                && char.ToUpperInvariant(line[33]) is ' ' or 'A' or 'P' or 'G' or 'K' or 'D' or 'T' or 'Z' or 'F'
                && char.ToUpperInvariant(line[34]) is ' ' or 'I' or 'T'
                && device is not null && Devices.Contains(device) && line[42] == ' ';
            var keywords = Keywords(line[43..CodeColumns]);
            if (!valid || keywords is null)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, number,
                    "F-specification columns are not a valid RPG IV file description (name 7-16, type 17, format 22, device 36-42, keywords 44-80); no facts were taken from it.");
                return;
            }

            context.Understood();
            UnresolvedReason? reason = format == 'E' && DescribedDevices.Contains(device) ? null : UnresolvedReason.UnsupportedForm;
            if (reason is not null)
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, number,
                    "Program-described file or SPECIAL/SEQ device; the file is recorded but its use is not resolved in F1.5.");
            }

            _file = (name!, number, keywords, reason, true);
        }

        /// <summary>Emits the pending file description once all of its continuation lines are seen.</summary>
        public void Flush()
        {
            if (_file is not { Valid: true } file)
            {
                _file = null;
                return;
            }

            _file = null;
            var reason = file.Reason;
            if (reason is null && file.Keywords.Any(k => FileOverrideKeywords.Contains(k)))
            {
                reason = UnresolvedReason.UnsupportedForm;
                context.Problem(ParseDiagnosticCode.UnsupportedForm, file.Line,
                    "File description uses EXTFILE, EXTDESC, LIKEFILE, TEMPLATE, or HANDLER; the file actually used is not determined in F1.5.");
            }

            var declaration = new ParsedArtifact(ArtifactKind.DeclaredFile, file.Name, null, file.Line);
            context.Artifacts.Add(declaration);
            context.References.Add(new(EdgeKind.UsesFile, file.Line, ArtifactKind.Member, string.Empty, file.Name, null, file.Name, reason));
            _declaredFiles.Add((declaration, reason));
        }

        public void Calculation(string line, int number)
        {
            var control = line[6..8].ToUpperInvariant();
            var operation = Operation().Match(line[25..35].TrimEnd().ToUpperInvariant());
            var leftAdjusted = line[25] != ' ';
            if (!(control is "  " or "LR" or "SR" || (control[0] == 'L' && char.IsAsciiDigit(control[1])))
                || !IsConditioning(line[8..11].ToUpperInvariant()))
            {
                _afterCall = false;
                context.Problem(control is "AN" or "OR" ? ParseDiagnosticCode.UnsupportedForm : ParseDiagnosticCode.MalformedStatement, number,
                    "C-specification control level (7-8) or conditioning (9-11) is outside the F1.5 subset; no facts were taken from it.");
                return;
            }

            if (!leftAdjusted || !operation.Success)
            {
                _afterCall = false;
                context.Problem(ParseDiagnosticCode.UnsupportedStatement, number,
                    "C-specification operation (26-35) is not a left-adjusted operation code in the F1.5 subset; no facts were taken from it.");
                return;
            }

            var op = operation.Groups["op"].Value;
            var extender = operation.Groups["ext"].Value;
            var factor1 = line[11..25].Trim();
            var factor2 = line[35..49].Trim();
            var result = Field(line, 50, 63);
            var lengthBlank = string.IsNullOrWhiteSpace(line[63..70]);
            switch (op)
            {
                case "CALL" when extender is "" or "E":
                    Call(number, factor1, factor2, result, lengthBlank);
                    break;
                case "PARM" when extender.Length == 0 && _afterCall:
                    // Part of the preceding CALL's parameter list; no facts.
                    context.Understood();
                    break;
                case "CHAIN" when extender is "" or "N" or "E" or "NE" or "EN":
                    _afterCall = false;
                    if (factor1.Length == 0 || !IsRpgName(factor2) || result is null || !lengthBlank)
                    {
                        context.Problem(ParseDiagnosticCode.MalformedStatement, number, "CHAIN needs a search argument in factor 1 and a file or record-format name in factor 2.");
                    }
                    else
                    {
                        _chains.Add((factor2, number));
                    }

                    break;
                default:
                    _afterCall = false;
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, number, "Operation is outside the F1.5 fixed RPG subset (CALL, PARM after CALL, CHAIN); no facts were taken from it.");
                    break;
            }
        }

        private void Call(int number, string factor1, string factor2, string? result, bool lengthBlank)
        {
            _afterCall = false;
            if (factor1.Length != 0 || factor2.Length == 0 || result is null || !lengthBlank)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, number, "CALL needs a program in factor 2 and blank factor 1, length, and decimals.");
                return;
            }

            ParsedReference reference;
            if (factor2[0] == '\'')
            {
                var body = factor2.Length >= 2 && factor2[^1] == '\'' ? factor2[1..^1] : null;
                if (body is null || body.Contains('\'', StringComparison.Ordinal))
                {
                    context.Problem(ParseDiagnosticCode.MalformedStatement, number, "CALL literal in factor 2 must be one closed literal within positions 36-49.");
                    return;
                }

                var match = LiteralTarget().Match(body);
                var library = match.Groups["lib"].Success ? match.Groups["lib"].Value : null;
                var name = match.Groups["name"].Value;
                if (!match.Success)
                {
                    reference = Reference(number, "(string literal)", null, null, UnresolvedReason.UnsupportedForm);
                }
                else if (library is not null && library.StartsWith('*'))
                {
                    reference = Reference(number, body, null, name, UnresolvedReason.UnsupportedQualifier);
                }
                else if (!MemberIdentity.IsValidName(name) || (library is not null && !MemberIdentity.IsValidName(library)))
                {
                    // A literal is case-sensitive: 'custinq' does not name the object CUSTINQ.
                    reference = Reference(number, body, null, null, UnresolvedReason.InvalidTargetName);
                }
                else
                {
                    reference = Reference(number, body, library, name, null);
                }

                if (reference.FixedReason is not null)
                {
                    context.Problem(ParseDiagnosticCode.UnsupportedForm, number,
                        "CALL literal uses a special-value library or is not an IBM i system name; it is recorded as an unresolved reference.");
                }
            }
            else if (IsRpgName(factor2))
            {
                reference = Reference(number, factor2, null, null, UnresolvedReason.DynamicTarget);
            }
            else
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, number, "CALL factor 2 must be a literal or a name.");
                return;
            }

            context.Understood();
            context.References.Add(reference);
            _afterCall = true;
        }

        public void Finish()
        {
            // A CHAIN yields a file observation only when factor 2 names a file declared in this member.
            foreach (var (file, line) in _chains)
            {
                var key = IdentifierKey.Of(file);
                var matches = _declaredFiles.Where(d => IdentifierKey.Of(d.Declaration.Name) == key).ToList();
                if (matches.Count == 0)
                {
                    context.Problem(ParseDiagnosticCode.UnsupportedForm, line,
                        "CHAIN factor 2 is not a file declared in this member (it may be a record format); no file observation was recorded.");
                    continue;
                }

                context.Understood();
                context.References.Add(new(EdgeKind.UsesFile, line, ArtifactKind.Member, string.Empty, file, null, file,
                    matches.Count == 1 ? matches[0].Reason : UnresolvedReason.AmbiguousTarget));
            }
        }

        private static ParsedReference Reference(int line, string target, string? library, string? name, UnresolvedReason? reason) =>
            new(EdgeKind.CallsProgram, line, ArtifactKind.Member, string.Empty, target, library, name, reason);

        /// <summary>Blank, or digits with no embedded blank.</summary>
        /// <summary>
        /// Positions 9-11: all blank, or an optional N (9) and a conditioning indicator (10-11): 01-99,
        /// KA-KN, KP-KY, L1-L9, LR, MR, H1-H9, RT, U1-U8, OA-OG, or OV.
        /// </summary>
        private static bool IsConditioning(string area) =>
            area == "   " || (area[0] is ' ' or 'N' && area[1..] switch
            {
                [var d1, var d2] when char.IsAsciiDigit(d1) && char.IsAsciiDigit(d2) => area[1..] != "00",
                ['K', >= 'A' and <= 'Y' and not 'O'] => true,
                ['L' or 'H', >= '1' and <= '9'] => true,
                ['U', >= '1' and <= '8'] => true,
                ['O', >= 'A' and <= 'G'] => true,
                "LR" or "MR" or "RT" or "OV" => true,
                _ => false,
            });

        private static bool IsNumberOrBlank(string area) => area.Trim().All(char.IsAsciiDigit);

        /// <summary>Keyword names in an area: NAME or NAME(...) with quoted values; null if unbalanced.</summary>
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
                                i++;
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
    }
}
