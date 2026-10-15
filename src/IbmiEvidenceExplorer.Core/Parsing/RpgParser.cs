namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>
/// Fully free-form RPG (<c>**FREE</c> on line 1); any other member goes to <see cref="RpgFixedParser"/>. Recognized statements: <c>ctl-opt</c>,
/// <c>dcl-f NAME ...</c>, <c>dcl-pi ... end-pi</c> (parameter lines inside are accepted without
/// facts), and <c>chain KEY NAME</c>. Statements end at <c>;</c>; <c>//</c> comments and
/// <c>'...'</c> literals never produce facts. Everything else is reported as unsupported.
/// </summary>
internal static class RpgParser
{
    /// <summary>dcl-f keywords that change which file is used; the reference is then left unresolved.</summary>
    private static readonly string[] FileOverrideKeywords = ["EXTFILE", "EXTDESC", "LIKEFILE", "TEMPLATE", "HANDLER"];

    public static void Parse(IReadOnlyList<string> lines, ParseContext context)
    {
        if (lines.All(Chars.IsBlankLine))
        {
            return;
        }

        if (!lines[0].TrimEnd(' ', '\t').Equals("**FREE", StringComparison.OrdinalIgnoreCase))
        {
            // Without **FREE on line 1 the member is column-limited: the F1.5 fixed-form slice applies.
            RpgFixedParser.Parse(lines, context);
            return;
        }

        context.Meaningful(1);
        context.Understood();
        var state = new State(context);
        foreach (var statement in Lex(lines, context))
        {
            state.Handle(statement);
        }

        state.Finish();
    }

    private static IEnumerable<Statement> Lex(IReadOnlyList<string> lines, ParseContext context)
    {
        var current = new Statement();
        for (var index = 1; index < lines.Count; index++)
        {
            var line = lines[index];
            var number = index + 1;
            if (line.StartsWith("**", StringComparison.Ordinal))
            {
                context.Meaningful(number);
                context.Problem(ParseDiagnosticCode.CompileTimeDataUnsupported, number,
                    "Compile-time data section is not supported; the remaining lines were not parsed.");
                break;
            }

            var first = 0;
            while (first < line.Length && Chars.IsBlank(line[first]))
            {
                first++;
            }

            if (first + 1 < line.Length && line[first] == '/' && char.IsAsciiLetter(line[first + 1]))
            {
                context.Meaningful(number);
                context.Problem(ParseDiagnosticCode.UnsupportedStatement, number,
                    "Compiler directive (for example /COPY or /INCLUDE) is not supported; it was not followed.");
                continue;
            }

            var column = first;
            while (column < line.Length)
            {
                var c = line[column];
                if (Chars.IsBlank(c))
                {
                    column++;
                }
                else if (c == '/' && column + 1 < line.Length && line[column + 1] == '/')
                {
                    break;
                }
                else if (c == '\'')
                {
                    var end = column + 1;
                    var closed = false;
                    while (end < line.Length)
                    {
                        if (line[end] == '\'')
                        {
                            if (end + 1 < line.Length && line[end + 1] == '\'')
                            {
                                end += 2;
                                continue;
                            }

                            closed = true;
                            end++;
                            break;
                        }

                        end++;
                    }

                    if (!closed)
                    {
                        current.Errors.Add(new(ParseDiagnosticCode.UnterminatedString, number));
                    }

                    current.Tokens.Add(new(TokenKind.String, line[column..end], number, column, end - column));
                    column = end;
                }
                else if (Chars.IsNameStart(c) || (c is '*' or '%' && column + 1 < line.Length && char.IsAsciiLetter(line[column + 1])))
                {
                    var end = column + 1;
                    while (end < line.Length && Chars.IsNamePart(line[end]))
                    {
                        end++;
                    }

                    // Free-form declaration keywords such as dcl-f, end-pi, ctl-opt are single words.
                    var word = line[column..end];
                    if (word.ToUpperInvariant() is "DCL" or "END" or "CTL" && end + 1 < line.Length && line[end] == '-' && char.IsAsciiLetter(line[end + 1]))
                    {
                        end++;
                        while (end < line.Length && Chars.IsNamePart(line[end]))
                        {
                            end++;
                        }
                    }

                    current.Tokens.Add(new(TokenKind.Word, line[column..end], number, column, end - column));
                    column = end;
                }
                else if (Chars.IsDigit(c))
                {
                    var end = column + 1;
                    while (end < line.Length && (Chars.IsDigit(line[end]) || line[end] == '.'))
                    {
                        end++;
                    }

                    current.Tokens.Add(new(TokenKind.Number, line[column..end], number, column, end - column));
                    column = end;
                }
                else if (c == ';')
                {
                    yield return current;
                    current = new Statement();
                    column++;
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
        }

        if (!current.IsEmpty)
        {
            current.Errors.Insert(0, new(ParseDiagnosticCode.MissingTerminator, current.Line));
            yield return current;
        }
    }

    private sealed class State(ParseContext context)
    {
        private readonly List<(ParsedArtifact Declaration, UnresolvedReason? Reason)> _declaredFiles = [];
        private readonly List<(Token File, int Line)> _chains = [];
        private int? _openInterface;

        public void Handle(Statement statement)
        {
            if (statement.IsEmpty)
            {
                return; // a bare ';'
            }

            context.Meaningful(statement.Line);
            if (statement.Errors.Count > 0)
            {
                if (statement.Errors[0].Code == ParseDiagnosticCode.MissingTerminator)
                {
                    context.Problem(ParseDiagnosticCode.MissingTerminator, statement.Line, "Statement is not terminated by ';' before the end of the member.");
                }
                else
                {
                    context.ReportLexErrors(statement);
                }

                return;
            }

            var tokens = statement.Tokens;
            var head = tokens[0];
            if (_openInterface is not null)
            {
                HandleInterfaceLine(statement, tokens);
                return;
            }

            if (head.Is("ctl-opt"))
            {
                context.Understood();
            }
            else if (head.Is("dcl-f"))
            {
                DeclareFile(statement, tokens);
            }
            else if (head.Is("dcl-pi"))
            {
                if (tokens.Count < 2 || tokens[1].Kind != TokenKind.Word)
                {
                    context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "dcl-pi must name the interface (or *N).");
                    return;
                }

                context.Understood();
                if (!tokens[^1].Is("end-pi"))
                {
                    _openInterface = statement.Line;
                }
            }
            else if (head.Is("end-pi"))
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "end-pi without a matching dcl-pi.");
            }
            else if (head.Is("chain"))
            {
                Chain(statement, tokens);
            }
            else
            {
                context.Problem(ParseDiagnosticCode.UnsupportedStatement, statement.Line, "Statement is outside the F1 RPG subset; no facts were taken from it.");
            }
        }

        public void Finish()
        {
            if (_openInterface is { } line)
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, line, "dcl-pi is not closed by end-pi.");
            }

            // chain may link only to a file declared in the same member.
            foreach (var (file, line2) in _chains)
            {
                var key = IdentifierKey.Of(file.Text);
                var matches = _declaredFiles.Where(d => IdentifierKey.Of(d.Declaration.Name) == key).ToList();
                var reason = matches.Count switch
                {
                    0 => UnresolvedReason.NotDeclaredInMember,
                    1 => matches[0].Reason,
                    _ => UnresolvedReason.AmbiguousTarget,
                };
                context.References.Add(new(EdgeKind.UsesFile, line2, ArtifactKind.Member, string.Empty, file.Text, null, file.Text, reason));
            }
        }

        private void HandleInterfaceLine(Statement statement, List<Token> tokens)
        {
            if (tokens[0].Is("end-pi"))
            {
                if (tokens.Count > 2)
                {
                    context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "end-pi takes at most a name.");
                }
                else
                {
                    context.Understood();
                }

                _openInterface = null;
            }
            else if (tokens[0].Is("dcl-parm") ? tokens.Count >= 2 && Chars.IsPlainName(tokens[1]) : Chars.IsPlainName(tokens[0]))
            {
                context.Understood(); // a parameter definition; F1 records no facts for parameters
            }
            else
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "Expected a parameter definition or end-pi inside dcl-pi.");
            }
        }

        private void DeclareFile(Statement statement, List<Token> tokens)
        {
            if (tokens.Count < 2 || !Chars.IsPlainName(tokens[1]))
            {
                context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "dcl-f must be followed by a file name.");
                return;
            }

            var overridden = false;
            for (var i = 2; i < tokens.Count; i++)
            {
                if (!Chars.IsPlainName(tokens[i]) && tokens[i].Kind != TokenKind.Word)
                {
                    context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "dcl-f keywords must be names with optional parenthesized values.");
                    return;
                }

                overridden |= FileOverrideKeywords.Contains(tokens[i].Text.ToUpperInvariant());
                if (i + 1 < tokens.Count && tokens[i + 1].IsSymbol('('))
                {
                    var close = Chars.MatchParen(tokens, i + 1);
                    if (close < 0)
                    {
                        context.Problem(ParseDiagnosticCode.MalformedStatement, statement.Line, "dcl-f keyword has unbalanced parentheses.");
                        return;
                    }

                    i = close;
                }
            }

            var name = tokens[1];
            var declaration = new ParsedArtifact(ArtifactKind.DeclaredFile, name.Text, null, name.Line);
            UnresolvedReason? reason = null;
            if (overridden)
            {
                reason = UnresolvedReason.UnsupportedForm;
                context.Problem(ParseDiagnosticCode.UnsupportedForm, name.Line,
                    "dcl-f uses EXTFILE, EXTDESC, LIKEFILE, TEMPLATE, or HANDLER; the file actually used is not determined in F1.");
            }

            context.Understood();
            context.Artifacts.Add(declaration);
            context.References.Add(new(EdgeKind.UsesFile, name.Line, ArtifactKind.Member, string.Empty, name.Text, null, name.Text, reason));
            _declaredFiles.Add((declaration, reason));
        }

        private void Chain(Statement statement, List<Token> tokens)
        {
            if (tokens.Count > 1 && tokens[0].Touches(tokens[1]) && tokens[1].IsSymbol('('))
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, statement.Line, "chain with an operation extender is not supported in F1.");
                return;
            }

            // chain KEY FILE  where KEY is one token or a parenthesized key list.
            var position = 1;
            if (position < tokens.Count && tokens[position].IsSymbol('('))
            {
                var close = Chars.MatchParen(tokens, position);
                position = close < 0 ? tokens.Count : close + 1;
            }
            else if (position < tokens.Count && tokens[position].Kind is TokenKind.Word or TokenKind.Number or TokenKind.String)
            {
                position++;
            }
            else
            {
                position = tokens.Count;
            }

            if (position != tokens.Count - 1 || !Chars.IsPlainName(tokens[position]))
            {
                context.Problem(ParseDiagnosticCode.UnsupportedForm, statement.Line,
                    "Only 'chain KEY FILE;' with a single key operand or key list is supported in F1.");
                return;
            }

            context.Understood();
            _chains.Add((tokens[position], tokens[position].Line));
        }
    }
}
