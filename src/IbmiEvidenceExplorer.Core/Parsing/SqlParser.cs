namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>
/// One <c>CREATE [OR REPLACE] VIEW [SCHEMA.]NAME AS SELECT ... FROM [SCHEMA.]NAME [WHERE ...]</c>
/// statement per member, with one base table and no join, subquery, set operation, CTE, or quoted
/// identifier. Statements end at <c>;</c>; a statement still open at the end of the member is
/// reported and yields no facts. <c>--</c> and <c>/* */</c> comments (block comments nest, as in
/// Db2 for i) and <c>'...'</c> literals never produce facts. Other statements are reported as unsupported.
/// </summary>
internal static class SqlParser
{
    /// <summary>Words that, anywhere after FROM, mean more than one table or a clause outside the subset.</summary>
    private static readonly string[] ClauseWords =
        ["SELECT", "FROM", "JOIN", "UNION", "EXCEPT", "INTERSECT", "WITH", "GROUP", "HAVING", "ORDER", "FETCH", "LIMIT", "OFFSET"];

    public static void Parse(IReadOnlyList<string> lines, ParseContext context)
    {
        var seenView = false;
        foreach (var statement in Lex(lines))
        {
            if (statement.IsEmpty)
            {
                continue;
            }

            context.Meaningful(statement.Line);
            if (context.ReportLexErrors(statement))
            {
                continue;
            }

            var tokens = statement.Tokens;
            var isView = tokens.Count > 1 && tokens[0].Is("CREATE")
                && (tokens[1].Is("VIEW") || (tokens.Count > 3 && tokens[1].Is("OR") && tokens[2].Is("REPLACE") && tokens[3].Is("VIEW")));
            if (!isView)
            {
                context.Problem(ParseDiagnosticCode.UnsupportedStatement, statement.Line, "Statement is outside the F1 SQL subset (CREATE VIEW only).");
            }
            else if (seenView)
            {
                context.Problem(ParseDiagnosticCode.UnsupportedStatement, statement.Line, "Only one CREATE VIEW statement per member is supported in F1.");
            }
            else
            {
                seenView = true;
                View(tokens, statement.Line, context);
            }
        }
    }

    private static void View(List<Token> tokens, int line, ParseContext context)
    {
        var i = tokens[1].Is("VIEW") ? 2 : 4;
        if (!TryName(tokens, ref i, out var schema, out var view) || i >= tokens.Count || !tokens[i].Is("AS"))
        {
            var quoted = tokens.Any(t => t.Kind == TokenKind.QuotedIdentifier);
            context.Problem(quoted ? ParseDiagnosticCode.UnsupportedForm : ParseDiagnosticCode.MalformedStatement, line, quoted
                ? "Quoted identifiers are not supported in F1."
                : "CREATE VIEW must be followed by [SCHEMA.]NAME AS SELECT ...; a column list is not supported in F1.");
            return;
        }

        context.Understood();
        context.Artifacts.Add(new(ArtifactKind.View, view.Text, schema?.Text, view.Line));

        // AS SELECT <list> FROM <table> [WHERE <condition>]
        i++;
        if (i >= tokens.Count || !tokens[i].Is("SELECT"))
        {
            Unsupported(context, line);
            return;
        }

        var from = -1;
        var depth = 0;
        for (var j = i + 1; j < tokens.Count && from < 0; j++)
        {
            var t = tokens[j];
            depth += t.IsSymbol('(') ? 1 : t.IsSymbol(')') ? -1 : 0;
            if (t.Kind == TokenKind.QuotedIdentifier || t.Is("SELECT") || t.Is("WITH"))
            {
                Unsupported(context, line);
                return;
            }

            if (depth == 0 && t.Is("FROM"))
            {
                from = j;
            }
        }

        var table = from + 1;
        if (from <= i + 1 || !TryName(tokens, ref table, out var tableSchema, out var tableName))
        {
            Unsupported(context, line);
            return;
        }

        if (table < tokens.Count)
        {
            if (!tokens[table].Is("WHERE") || table + 1 >= tokens.Count
                || tokens.Skip(table + 1).Any(t => t.Kind == TokenKind.QuotedIdentifier || ClauseWords.Any(t.Is)))
            {
                Unsupported(context, line);
                return;
            }
        }

        var target = tableSchema is null ? tableName.Text : $"{tableSchema.Value.Text}.{tableName.Text}";
        context.References.Add(new(EdgeKind.SelectsFrom, tableName.Line, ArtifactKind.View, view.Text, target, tableSchema?.Text, tableName.Text, null));
    }

    private static void Unsupported(ParseContext context, int line) =>
        context.Problem(ParseDiagnosticCode.UnsupportedForm, line,
            "View body is outside the F1 subset (one base table; no join, subquery, set operation, CTE, column list, or quoted identifier); no base-table reference was recorded.");

    /// <summary>NAME or SCHEMA.NAME of plain identifiers.</summary>
    private static bool TryName(List<Token> tokens, ref int i, out Token? schema, out Token name)
    {
        schema = null;
        name = default;
        if (i >= tokens.Count || !Chars.IsPlainName(tokens[i]))
        {
            return false;
        }

        if (i + 2 < tokens.Count && tokens[i + 1].IsSymbol('.'))
        {
            if (!Chars.IsPlainName(tokens[i + 2]))
            {
                return false;
            }

            schema = tokens[i];
            name = tokens[i + 2];
            i += 3;
            return true;
        }

        if (i + 1 < tokens.Count && tokens[i + 1].IsSymbol('/'))
        {
            return false; // system naming (LIB/NAME) is not in the F1 subset
        }

        name = tokens[i];
        i++;
        return true;
    }

    private static IEnumerable<Statement> Lex(IReadOnlyList<string> lines)
    {
        var current = new Statement();
        char? open = null; // ' or " literal, or '*' for a block comment, spanning lines
        var openLine = 0;
        var commentDepth = 0; // nesting level while open == '*'
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var number = index + 1;
            var column = 0;
            while (column < line.Length)
            {
                if (open is not null)
                {
                    column = Close(line, column, ref open, ref commentDepth);
                    continue;
                }

                var c = line[column];
                if (Chars.IsBlank(c))
                {
                    column++;
                }
                else if (c == '-' && column + 1 < line.Length && line[column + 1] == '-')
                {
                    break;
                }
                else if (c == '/' && column + 1 < line.Length && line[column + 1] == '*')
                {
                    open = '*';
                    openLine = number;
                    commentDepth = 1;
                    column += 2;
                }
                else if (c is '\'' or '"')
                {
                    current.Tokens.Add(new(c == '"' ? TokenKind.QuotedIdentifier : TokenKind.String, c.ToString(), number, column, 1));
                    open = c;
                    openLine = number;
                    column++;
                }
                else if (Chars.IsNameStart(c))
                {
                    var end = column + 1;
                    while (end < line.Length && Chars.IsNamePart(line[end]))
                    {
                        end++;
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

        if (open is not null)
        {
            current.Errors.Add(new(open == '*' ? ParseDiagnosticCode.UnterminatedComment : ParseDiagnosticCode.UnterminatedString, openLine));
        }
        else if (current.Tokens.Count > 0)
        {
            // Text after the last ';' is an unfinished statement, never a fact. (An open literal or
            // comment already explains the missing ';' and is reported instead.)
            current.Errors.Insert(0, new(ParseDiagnosticCode.MissingTerminator, current.Line));
        }

        if (!current.IsEmpty)
        {
            yield return current;
        }
    }

    /// <summary>
    /// Advances through an open literal or block comment; clears <paramref name="open"/> when it closes.
    /// Block comments nest: each <c>/*</c> inside one needs its own <c>*/</c>.
    /// </summary>
    private static int Close(string line, int column, ref char? open, ref int commentDepth)
    {
        if (open == '*')
        {
            for (var i = column; i + 1 < line.Length; i++)
            {
                if (line[i] == '/' && line[i + 1] == '*')
                {
                    commentDepth++;
                    i++;
                }
                else if (line[i] == '*' && line[i + 1] == '/')
                {
                    i++;
                    if (--commentDepth == 0)
                    {
                        open = null;
                        return i + 1;
                    }
                }
            }

            return line.Length;
        }

        var quote = open!.Value;
        for (var i = column; i < line.Length; i++)
        {
            if (line[i] != quote)
            {
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == quote)
            {
                i++;
                continue;
            }

            open = null;
            return i + 1;
        }

        return line.Length;
    }
}
