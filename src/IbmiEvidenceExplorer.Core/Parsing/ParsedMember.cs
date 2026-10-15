using System.Text.Json;

namespace IbmiEvidenceExplorer.Core.Parsing;

/// <summary>How much of a member the F1 parser understood (per member, never per language).</summary>
public enum CoverageStatus
{
    /// <summary>Every meaningful statement is one of the supported F1 forms. Not a claim that behavior is modeled.</summary>
    Complete,

    /// <summary>Some statements were understood; others were unsupported or malformed and produced no facts.</summary>
    Partial,

    /// <summary>
    /// The member was not parsed (limits, unsupported source form), no statement was understood, or
    /// it has no statements at all (empty, blank, or comments only) and so no source-backed definition.
    /// </summary>
    Unsupported,
}

/// <summary>Kinds of directly observed definitions.</summary>
public enum ArtifactKind
{
    Member,
    View,
    Record,
    Field,
    Variable,
    DeclaredFile,
}

/// <summary>The only relationship kinds F1 records. No transitive or inferred kinds exist.</summary>
public enum EdgeKind
{
    CallsProgram,
    UsesFile,
    SelectsFrom,
    KeysField,
}

/// <summary>Why an observed reference is not a resolved edge.</summary>
public enum UnresolvedReason
{
    TargetNotFound,
    AmbiguousTarget,
    DynamicTarget,
    NotDeclaredInMember,
    InvalidTargetName,
    UnsupportedQualifier,
    UnsupportedForm,

    // F1.5: TargetNotFound remains for keys-field (no such field in the record).

    /// <summary>No member of a compatible type has the target name anywhere in the snapshot.</summary>
    TargetNotInSnapshot,

    /// <summary>The name exists only under another library of the snapshot; no library context selects it.</summary>
    TargetInOtherLibrary,
}

/// <summary>Grouping keys for the private syntax-gap report.</summary>
public enum SyntaxGapKind
{
    /// <summary>A CL command verb outside the F1 subset.</summary>
    ClCommand,

    /// <summary>A DDS keyword outside the descriptive set.</summary>
    DdsKeyword,
}

/// <summary>One unsupported verb or keyword in one member: how often, and the first line it appears on.</summary>
public sealed record SyntaxGapOccurrence(SyntaxGapKind Kind, string Key, int Count, int FirstLine);

/// <summary>Per-member parse and indexing diagnostics. Messages never contain source text.</summary>
public enum ParseDiagnosticCode
{
    UnsupportedStatement,
    UnsupportedForm,
    MalformedStatement,
    UnterminatedString,
    UnterminatedComment,
    InvalidCharacter,
    MissingTerminator,
    StatementAfterEnd,
    FixedFormUnsupported,
    CompileTimeDataUnsupported,
    MemberTooLarge,
    SourceLimitExceeded,
    MemberLimitExceeded,
    ItemLimitExceeded,
    DiagnosticsTruncated,
    NoStatements,
}

/// <summary>Stable, lowercase hyphenated labels for F1 vocabulary (for example <c>calls-program</c>).</summary>
public static class Labels
{
    public static string Of<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}

/// <summary>A definition observed at one line. <see cref="Name"/> keeps source spelling.</summary>
public sealed record ParsedArtifact(ArtifactKind Kind, string Name, string? Qualifier, int Line);

/// <summary>
/// A reference observed at one line. <see cref="Target"/> is the observed target identifier (names
/// and separators only, never literal text). <see cref="FixedReason"/> is set when the parser
/// already knows the reference cannot resolve (for example a variable-valued call).
/// </summary>
public sealed record ParsedReference(
    EdgeKind Kind,
    int Line,
    ArtifactKind SourceKind,
    string SourceName,
    string Target,
    string? TargetLibrary,
    string? TargetName,
    UnresolvedReason? FixedReason);

public sealed record ParseDiagnostic(ParseDiagnosticCode Code, int? Line, string Message);

/// <summary>
/// Everything the parser extracted from one member, by line number only. <see cref="Diagnostics"/>
/// is a bounded sample (the first ones encountered, plus a truncation marker); <see cref="DiagnosticTotals"/>
/// counts every diagnostic encountered, by code, so <c>sum(totals) = sample + suppressed</c>.
/// </summary>
public sealed record ParsedMember(
    CoverageStatus Coverage,
    int? DefinitionLine,
    IReadOnlyList<ParsedArtifact> Artifacts,
    IReadOnlyList<ParsedReference> References,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    int SuppressedDiagnostics,
    IReadOnlyDictionary<ParseDiagnosticCode, int> DiagnosticTotals,
    IReadOnlyList<SyntaxGapOccurrence> Gaps);

/// <summary>Case-insensitive identifier keys; display always keeps source spelling.</summary>
public static class IdentifierKey
{
    /// <summary>Upper-cases (invariant) and drops a CL variable's leading <c>&amp;</c>.</summary>
    public static string Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return (name.StartsWith('&') ? name[1..] : name).ToUpperInvariant();
    }
}
