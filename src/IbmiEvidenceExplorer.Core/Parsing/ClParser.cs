namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>
/// CL commands end at the end of a line unless the last non-blank character is a <c>+</c> or
/// <c>-</c> continuation. Recognized commands: <c>PGM</c>, <c>ENDPGM</c>, <c>DCL VAR(&amp;X) ...</c>
/// (keyword form), and <c>CALL PGM(NAME)</c> / <c>CALL PGM(LIB/NAME)</c> with optional
/// <c>PARM(...)</c>. A <c>/*</c> starts a comment only at the start of a line or after a blank, so
/// <c>LIB/*ALL</c> stays a value. Everything else is reported as unsupported.
/// </summary>
internal static class ClParser
{
    private static readonly string[] DclKeywords = ["VAR", "TYPE", "LEN", "VALUE", "STG", "BASPTR", "DEFVAR"];

    public static void Parse(IReadOnlyList<string> lines, ParseContext context)
    {
        var state = new State(context);
        foreach (var statement in Lex(lines, context))
        {
            state.Handle(statement);
        }
    }

    private static IEnumerable<Statement> Lex(IReadOnlyList<string> lines, ParseContext context)
    {
        var current = new Statement();
        int? commentStart = null;
        var stringOpen = false; // a literal continued from the previous line
        var stringStart = (Line: 0, Column: 0);
        var continued = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var number = index + 1;
            var column = 0;
            continued = false;

            if (stringOpen && current.Tokens.Count > 0)
            {
                column = ContinueString(line, number, current, ref stringOpen);
            }

            while (column < line.Length)
            {
                if (commentStart is not null)
                {
                    var close = line.IndexOf("*/", column, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        column = line.Length;
                        break;
                    }

                    commentStart = null;
                    column = close + 2;
                    continue;
                }

                var c = line[column];
                if (Chars.IsBlank(c))
                {
                    column++;
                }
                else if (c == '/' && column + 1 < line.Length && line[column + 1] == '*' && (column == 0 || Chars.IsBlank(line[column - 1])))
                {
                    var close = line.IndexOf("*/", column + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        // Treated as running to the next "*/" so commented-out commands never yield facts.
                        commentStart = number;
                        context.Problem(ParseDiagnosticCode.UnterminatedComment, number,
                            "Comment is not closed on its line; lines up to the next */ were treated as comment.");
                        column = line.Length;
                    }
                    else
                    {
                        column = close + 2;
                    }
                }
                else if (c is '+' or '-' && IsContinuation(line, column + 1))
                {
                    continued = true;
                    column = line.Length;
                }
                else if (c == '\'')
                {
                    stringStart = (number, column);
                    current.Tokens.Add(new(TokenKind.String, "'", number, column, 1));
                    stringOpen = true;
                    column = ScanString(line, column + 1, ref stringOpen);
                    if (stringOpen && !EndsWithContinuation(line))
                    {
                        current.Errors.Add(new(ParseDiagnosticCode.UnterminatedString, stringStart.Line));
                        stringOpen = false;
                    }
                }
                else if ((c == '&' || c == '*') && column + 1 < line.Length && Chars.IsNameStart(line[column + 1]))
                {
                    column = AddWord(line, column, column + 1, number, current);
                }
                else if (Chars.IsNameStart(c))
                {
                    column = AddWord(line, column, column, number, current);
                }
                else if (Chars.IsDigit(c))
                {
                    var end = column + 1;
                    while (end < line.Length && (Chars.IsDigit(line[end]) || line[end] is '.' or ','))
                    {
                        end++;
                    }

                    current.Tokens.Add(new(TokenKind.Number, line[column..end], number, column, end - column));
                    column = end;
                }
                else if (Chars.IsSymbol(c))
                {
                    current.Tokens.Add(new(TokenKind.Symbol, c.ToString(), number, column, 1));
                    column++;
                }
                else
                {
                    current.Errors.Add(new(ParseDiagnosticCode.InvalidCharacter, number));
                    current.Tokens.Add(new(TokenKind.Invalid, string.Empty, number, column, 1));
                    column++;
                }
            }

            if (!continued && !stringOpen && !current.IsEmpty)
            {
                yield return current;
                current = new Statement();
            }
        }

        if (stringOpen)
        {
            current.Errors.Add(new(ParseDiagnosticCode.UnterminatedString, stringStart.Line));
        }

        if (commentStart is { } start)
        {
            context.Problem(ParseDiagnosticCode.UnterminatedComment, start, "Comment is not closed before the end of the member.");
        }

        if (!current.IsEmpty)
        {
            if (continued)
            {
                current.Errors.Insert(0, new(ParseDiagnosticCode.MissingTerminator, current.Line));
            }

            yield return current;
        }
    }

    /// <summary>A '+' or '-' followed only by blanks (or a closed comment) ends the line as a continuation.</summary>
    private static bool IsContinuation(string line, int from)
    {
        var rest = line.AsSpan(from).Trim(" \t");
        if (rest.IsEmpty)
        {
            return true;
        }

        return rest.StartsWith("/*", StringComparison.Ordinal) && rest.EndsWith("*/", StringComparison.Ordinal)
            && rest[2..^2].IndexOf("*/", StringComparison.Ordinal) < 0;
    }

    private static bool EndsWithContinuation(string line) => line.TrimEnd(' ', '\t') is { Length: > 0 } trimmed && trimmed[^1] is '+' or '-';

    /// <summary>Scans a literal body; returns the column after the closing quote, or the line length if it stays open.</summary>
    private static int ScanString(string line, int column, ref bool open)
    {
        while (column < line.Length)
        {
            if (line[column] == '\'')
            {
                if (column + 1 < line.Length && line[column + 1] == '\'')
                {
                    column += 2;
                    continue;
                }

                open = false;
                return column + 1;
            }

            column++;
        }

        return line.Length;
    }

    /// <summary>Continues a literal on the next line; '+' continuation skips the line's leading blanks.</summary>
    private static int ContinueString(string line, int number, Statement current, ref bool open)
    {
        var start = 0;
        while (start < line.Length && Chars.IsBlank(line[start]))
        {
            start++;
        }

        var end = ScanString(line, start, ref open);
        if (open && !EndsWithContinuation(line))
        {
            current.Errors.Add(new(ParseDiagnosticCode.UnterminatedString, number));
            open = false;
        }

        return end;
    }

    private static int AddWord(string line, int start, int nameStart, int number, Statement current)
    {
        var end = nameStart + 1;
        while (end < line.Length && (Chars.IsNamePart(line[end]) || line[end] == '.'))
        {
            end++;
        }

        current.Tokens.Add(new(TokenKind.Word, line[start..end], number, start, end - start));
        return end;
    }

    private sealed record Parameter(string? Keyword, List<Token> Value, int Line);

    private sealed class State(ParseContext context)
    {
        private bool _seenCommand;
        private bool _seenPgm;
        private bool _ended;

        public void Handle(Statement statement)
        {
            context.Meaningful(statement.Line);
            if (statement.Errors.Count > 0)
            {
                if (statement.Errors[0].Code == ParseDiagnosticCode.MissingTerminator)
                {
                    context.Problem(ParseDiagnosticCode.MissingTerminator, statement.Line, "Command ends with a continuation character at the end of the member.");
                }
                else
                {
                    context.ReportLexErrors(statement);
                }

                return;
            }

            var tokens = statement.Tokens;
            var start = 0;
            if (tokens.Count >= 2 && Chars.IsPlainName(tokens[0]) && tokens[1].IsSymbol(':') && tokens[0].Touches(tokens[1]))
            {
                start = 2; // label
            }

            if (start >= tokens.Count)
            {
                context.Problem(ParseDiagnosticCode.UnsupportedStatement, statement.Line, "A label without a command is outside the F1 CL subset.");
                return;
            }

            var command = tokens[start];
            if (!Chars.IsPlainName(command))
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "Expected a CL command name.");
                return;
            }

            if (_ended)
            {
                context.Problem(ParseDiagnosticCode.StatementAfterEnd, statement.Line, "Command follows ENDPGM; it was not parsed.");
                return;
            }

            var parameters = ParseParameters(tokens, start + 1);
            var name = command.Text.ToUpperInvariant();
            var first = !_seenCommand;
            _seenCommand = true;
            if (parameters is null)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "Command has unbalanced parentheses.");
                return;
            }

            switch (name)
            {
                case "PGM":
                    Pgm(statement, parameters, first);
                    break;
                case "ENDPGM":
                    if (parameters.Count > 0)
                    {
                        context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "ENDPGM takes no parameters.");
                    }
                    else
                    {
                        context.Understood();
                    }

                    _ended = true;
                    break;
                case "DCL":
                    Dcl(statement, parameters);
                    break;
                case "CALL":
                    Call(statement, parameters);
                    break;
                default:
                    context.Gap(SyntaxGapKind.ClCommand, name, statement.Line);
                    context.Problem(ParseDiagnosticCode.UnsupportedStatement, statement.Line, "Command is outside the F1 CL subset; no facts were taken from it.");
                    break;
            }
        }

        private void Pgm(Statement statement, List<Parameter> parameters, bool first)
        {
            if (!first || _seenPgm)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "PGM must be the first command and appear once.");
            }
            else if (parameters.Any(p => p.Keyword != "PARM"))
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, statement.Line, "PGM supports only the PARM keyword in F1.");
            }
            else
            {
                context.Understood();
            }

            _seenPgm = true;
        }

        private void Dcl(Statement statement, List<Parameter> parameters)
        {
            if (parameters.Any(p => p.Keyword is null))
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, statement.Line, "Only the keyword form DCL VAR(&NAME) ... is supported in F1.");
                return;
            }

            var variable = parameters.Where(p => p.Keyword == "VAR").ToList();
            if (parameters.Any(p => !DclKeywords.Contains(p.Keyword)) || variable.Count != 1
                || variable[0].Value is not [{ Kind: TokenKind.Word } token] || !token.Text.StartsWith('&'))
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "DCL must have exactly one VAR(&NAME) and only DCL keywords.");
                return;
            }

            context.Understood();
            context.Artifacts.Add(new(ArtifactKind.Variable, token.Text, null, token.Line));
        }

        private void Call(Statement statement, List<Parameter> parameters)
        {
            if (parameters.Any(p => p.Keyword is null))
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, statement.Line, "Only the keyword form CALL PGM(...) is supported in F1.");
                return;
            }

            var program = parameters.Where(p => p.Keyword == "PGM").ToList();
            if (parameters.Any(p => p.Keyword is not "PGM" and not "PARM") || program.Count != 1 || program[0].Value.Count == 0)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "CALL must have exactly one non-empty PGM(...) and optionally PARM(...).");
                return;
            }

            var value = program[0].Value;
            var line = value[0].Line;
            ParsedReference? reference = value switch
            {
                [var n] when Chars.IsPlainName(n) => Reference(line, n.Text, null, n.Text, null),
                [var l, var slash, var n] when slash.IsSymbol('/') && Chars.IsPlainName(l) && Chars.IsPlainName(n) =>
                    Reference(line, $"{l.Text}/{n.Text}", l.Text, n.Text, null),
                [var n] when IsVariable(n) => Reference(line, n.Text, null, null, UnresolvedReason.DynamicTarget),
                [var l, var slash, var n] when slash.IsSymbol('/') && IsName(l) && IsName(n) && (IsVariable(l) || IsVariable(n)) =>
                    Reference(line, $"{l.Text}/{n.Text}", null, null, UnresolvedReason.DynamicTarget),
                [var l, var slash, var n] when slash.IsSymbol('/') && l.Text.StartsWith('*') && Chars.IsPlainName(n) =>
                    Reference(line, $"{l.Text}/{n.Text}", null, n.Text, UnresolvedReason.UnsupportedQualifier),
                [{ Kind: TokenKind.String }] => Reference(line, "(string literal)", null, null, UnresolvedReason.UnsupportedForm),
                _ => null,
            };

            if (reference is null)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "CALL PGM(...) must name NAME or LIB/NAME.");
                return;
            }

            if (reference.FixedReason is UnresolvedReason.UnsupportedQualifier or UnresolvedReason.UnsupportedForm)
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, line,
                    "CALL target uses a special-value library or a quoted name; it is recorded as an unresolved reference.");
            }

            context.Understood();
            context.References.Add(reference);
        }

        private static bool IsVariable(Token token) => token.Kind == TokenKind.Word && token.Text.StartsWith('&');

        private static bool IsName(Token token) => Chars.IsPlainName(token) || IsVariable(token);

        private static ParsedReference Reference(int line, string target, string? library, string? name, UnresolvedReason? reason) =>
            new(EdgeKind.CallsProgram, line, ArtifactKind.Member, string.Empty, target, library, name, reason);

        /// <summary>Keyword parameters are NAME(...) with the parenthesis touching the name; others are positional.</summary>
        private static List<Parameter>? ParseParameters(List<Token> tokens, int start)
        {
            var parameters = new List<Parameter>();
            for (var i = start; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Word && i + 1 < tokens.Count && tokens[i + 1].IsSymbol('(') && token.Touches(tokens[i + 1]))
                {
                    var close = Chars.MatchParen(tokens, i + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    parameters.Add(new(token.Text.ToUpperInvariant(), tokens.GetRange(i + 2, close - i - 2), token.Line));
                    i = close;
                }
                else if (token.IsSymbol('('))
                {
                    var close = Chars.MatchParen(tokens, i);
                    if (close < 0)
                    {
                        return null;
                    }

                    parameters.Add(new(null, tokens.GetRange(i + 1, close - i - 1), token.Line));
                    i = close;
                }
                else if (token.IsSymbol(')'))
                {
                    return null;
                }
                else
                {
                    parameters.Add(new(null, [token], token.Line));
                }
            }

            return parameters;
        }
    }
}
