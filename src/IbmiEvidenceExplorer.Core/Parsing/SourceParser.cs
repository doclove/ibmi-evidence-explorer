namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>
/// Entry point for the F1 extractor: a deliberately narrow subset of RPG, CL, DDS, and SQL. Input
/// is the member's decoded lines (line 1 first), exactly as F0 split them. Pure and deterministic.
/// </summary>
public static class SourceParser
{
    /// <summary>Per-member cap on stored diagnostics; the remainder is counted, not listed.</summary>
    public const int MaxDiagnosticsPerMember = 50;

    public static ParsedMember Parse(SourceType sourceType, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var context = new ParseContext();
        switch (sourceType)
        {
            case SourceType.RPG: RpgParser.Parse(lines, context); break;
            case SourceType.CL: ClParser.Parse(lines, context); break;
            case SourceType.DDS: DdsParser.Parse(lines, context); break;
            case SourceType.SQL: SqlParser.Parse(lines, context); break;
            default: throw new ArgumentOutOfRangeException(nameof(sourceType));
        }

        return context.ToResult();
    }
}

internal enum TokenKind
{
    Word,
    Number,
    String,
    QuotedIdentifier,
    Symbol,
    Invalid,
}

/// <summary>A lexical token. <see cref="Column"/> is a 0-based character index in its line.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int Length)
{
    public int End => Column + Length;

    public bool Is(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(char symbol) => Kind == TokenKind.Symbol && Text.Length == 1 && Text[0] == symbol;

    /// <summary>True when <paramref name="next"/> starts immediately after this token on the same line.</summary>
    public bool Touches(Token next) => next.Line == Line && next.Column == End;
}

/// <summary>A lexical problem found inside one statement; the statement then yields no facts.</summary>
internal readonly record struct LexError(ParseDiagnosticCode Code, int Line);

internal sealed class Statement
{
    public List<Token> Tokens { get; } = [];

    public List<LexError> Errors { get; } = [];

    public int Line => Tokens.Count > 0 ? Tokens[0].Line : Errors.Count > 0 ? Errors[0].Line : 0;

    public bool IsEmpty => Tokens.Count == 0 && Errors.Count == 0;
}

/// <summary>Collects facts and diagnostics and decides coverage. Diagnostics are capped per member.</summary>
internal sealed class ParseContext
{
    private readonly List<ParseDiagnostic> _diagnostics = [];
    private readonly SortedDictionary<ParseDiagnosticCode, int> _totals = [];
    private readonly Dictionary<(SyntaxGapKind, string), (int Count, int FirstLine)> _gaps = [];
    private int _suppressed;
    private int _understood;
    private int _problems;
    private bool _memberUnsupported;

    public List<ParsedArtifact> Artifacts { get; } = [];

    public List<ParsedReference> References { get; } = [];

    public int? DefinitionLine { get; private set; }

    /// <summary>Records the first line holding a meaningful statement (the member's evidence line).</summary>
    public void Meaningful(int line) => DefinitionLine ??= line;

    public void Understood() => _understood++;

    /// <summary>A coverage-reducing problem: the statement (or part of it) produced no facts.</summary>
    public void Problem(ParseDiagnosticCode code, int? line, string message)
    {
        _problems++;
        Add(new ParseDiagnostic(code, line, message));
    }

    /// <summary>Records an unsupported CL verb or DDS keyword for the private syntax-gap report (no effect on coverage).</summary>
    public void Gap(SyntaxGapKind kind, string key, int line)
    {
        var upper = key.ToUpperInvariant();
        _gaps[(kind, upper)] = _gaps.TryGetValue((kind, upper), out var seen) ? (seen.Count + 1, seen.FirstLine) : (1, line);
    }

    /// <summary>The whole member is outside the supported subset (for example fixed-form RPG).</summary>
    public void MemberUnsupported(ParseDiagnosticCode code, int? line, string message)
    {
        _memberUnsupported = true;
        Problem(code, line, message);
    }

    /// <summary>Reports the first lexical error of a statement; returns true if there was one.</summary>
    public bool ReportLexErrors(Statement statement)
    {
        if (statement.Errors.Count == 0)
        {
            return false;
        }

        var error = statement.Errors[0];
        Problem(error.Code, error.Line, error.Code switch
        {
            ParseDiagnosticCode.UnterminatedString => "Statement contains an unterminated string literal; no facts were taken from it.",
            ParseDiagnosticCode.UnterminatedComment => "Statement contains an unterminated comment; no facts were taken from it.",
            ParseDiagnosticCode.MissingTerminator => "Statement is not terminated by ';' before the end of the member; no facts were taken from it.",
            _ => "Statement contains a character outside the supported identifier and punctuation set (for example a lone CR or control character); no facts were taken from it.",
        });
        return true;
    }

    public ParsedMember ToResult()
    {
        if (DefinitionLine is null)
        {
            // Nothing to cite: an empty, blank, or comment-only member is listed but never presented
            // as a complete, source-backed definition.
            _memberUnsupported = true;
            if (_problems == 0)
            {
                Add(new ParseDiagnostic(ParseDiagnosticCode.NoStatements, null,
                    "Member has no statements (it is empty or holds only blank lines or comments); it has no source-backed definition."));
            }
        }

        var coverage = _memberUnsupported || (_problems > 0 && _understood == 0)
            ? CoverageStatus.Unsupported
            : _problems > 0 ? CoverageStatus.Partial : CoverageStatus.Complete;
        var diagnostics = new List<ParseDiagnostic>(_diagnostics);
        if (_suppressed > 0)
        {
            diagnostics.Add(new(ParseDiagnosticCode.DiagnosticsTruncated, null,
                $"{_suppressed} further diagnostic(s) were not recorded (limit {SourceParser.MaxDiagnosticsPerMember} per member)."));
        }

        var gaps = _gaps.Select(g => new SyntaxGapOccurrence(g.Key.Item1, g.Key.Item2, g.Value.Count, g.Value.FirstLine))
            .OrderBy(g => g.Kind).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        return new ParsedMember(coverage, DefinitionLine, Artifacts, References, diagnostics, _suppressed, _totals, gaps);
    }

    private void Add(ParseDiagnostic diagnostic)
    {
        _totals[diagnostic.Code] = _totals.GetValueOrDefault(diagnostic.Code) + 1;
        if (_diagnostics.Count < SourceParser.MaxDiagnosticsPerMember)
        {
            _diagnostics.Add(diagnostic);
        }
        else
        {
            _suppressed++;
        }
    }
}

internal static class Chars
{
    public static bool IsNameStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '$' or '#' or '@' or '_';

    public static bool IsNamePart(char c) => IsNameStart(c) || c is >= '0' and <= '9';

    public static bool IsDigit(char c) => c is >= '0' and <= '9';

    public static bool IsBlank(char c) => c is ' ' or '\t';

    /// <summary>ASCII punctuation that may appear as a symbol token. Anything else outside strings/comments is invalid.</summary>
    public static bool IsSymbol(char c) => c is > ' ' and < '\u007f' && !IsNamePart(c) && c is not '\'' and not '"';

    public static bool IsBlankLine(string line) => line.All(IsBlank);

    /// <summary>Finds the closing parenthesis matching the '(' at <paramref name="open"/>; -1 if unbalanced.</summary>
    public static int MatchParen(IReadOnlyList<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol('('))
            {
                depth++;
            }
            else if (tokens[i].IsSymbol(')') && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A plain name token: starts with a letter or $ # @ _, no sigil.</summary>
    public static bool IsPlainName(Token token) => token.Kind == TokenKind.Word && IsNameStart(token.Text[0]);
}
