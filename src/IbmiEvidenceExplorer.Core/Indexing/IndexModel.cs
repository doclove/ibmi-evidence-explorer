using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Core.Indexing;

/// <summary>Version labels recorded with every F1 index. F0's evidence references keep ParserVersion "none".</summary>
public static class IndexFormats
{
    /// <summary>
    /// The extractor (F1.5: fixed-form RPG slice, refined unresolved reasons). Changing parsing
    /// behavior requires a new value, which yields a distinct index; F1 indexes are never reused.
    /// </summary>
    public const string ParserVersion = "ibmi-explorer-f1.5-parser/1";

    /// <summary>Layout and meaning of the persisted index documents (2: diagnostic totals, syntax gaps).</summary>
    public const string IndexFormat = "ibmi-explorer-index/2";

    /// <summary>Version of the persisted index, facts, and completion-marker documents.</summary>
    public const int IndexSchema = 2;
}

/// <summary>
/// Index work bounds. The defaults are the approved F1 values (D-012) and apply unless the operator
/// explicitly raises total bytes, members, or facts/edges within the F1.5 ceilings (D-014) through
/// <see cref="Create"/>. Per-member bytes and query results stay fixed. Changing any bound requires
/// a recorded decision.
/// </summary>
public sealed record IndexLimits(long MaxMemberBytes, long MaxTotalBytes, int MaxMembers, int MaxItems, int MaxQueryResults)
{
    public const long UpperBoundTotalBytes = 512 * ImportLimits.MiB;
    public const int UpperBoundMembers = 50_000;
    public const int UpperBoundItems = 500_000;

    public static IndexLimits Default { get; } = new(10 * ImportLimits.MiB, 100 * ImportLimits.MiB, 10_000, 100_000, 1_000);

    /// <summary>Defaults with optional, opt-in raises; each value must lie between the F1 default and the F1.5 ceiling.</summary>
    public static OperationResult<IndexLimits> Create(long? maxTotalBytes = null, int? maxMembers = null, int? maxItems = null)
    {
        var diagnostics = new List<Diagnostic>();
        var d = Default;
        if (maxTotalBytes is { } bytes && (bytes < d.MaxTotalBytes || bytes > UpperBoundTotalBytes))
        {
            diagnostics.Add(new(DiagnosticCode.InvalidLimit, $"Max index bytes must be between {d.MaxTotalBytes} and {UpperBoundTotalBytes}."));
        }

        if (maxMembers is { } members && (members < d.MaxMembers || members > UpperBoundMembers))
        {
            diagnostics.Add(new(DiagnosticCode.InvalidLimit, $"Max index members must be between {d.MaxMembers} and {UpperBoundMembers}."));
        }

        if (maxItems is { } items && (items < d.MaxItems || items > UpperBoundItems))
        {
            diagnostics.Add(new(DiagnosticCode.InvalidLimit, $"Max index facts and edges must be between {d.MaxItems} and {UpperBoundItems}."));
        }

        return diagnostics.Count == 0
            ? OperationResult<IndexLimits>.Success(d with { MaxTotalBytes = maxTotalBytes ?? d.MaxTotalBytes, MaxMembers = maxMembers ?? d.MaxMembers, MaxItems = maxItems ?? d.MaxItems })
            : OperationResult<IndexLimits>.Failure(diagnostics);
    }

    /// <summary>True when every bound is positive and none exceeds its accepted ceiling (stored limits are checked on load).</summary>
    public bool IsWithinBounds =>
        MaxMemberBytes is >= 1 and <= 10 * ImportLimits.MiB && MaxQueryResults is >= 1 and <= 1_000
        && MaxTotalBytes is >= 1 and <= UpperBoundTotalBytes && MaxMembers is >= 1 and <= UpperBoundMembers && MaxItems is >= 1 and <= UpperBoundItems;
}

public enum IndexStatus
{
    /// <summary>Every member has complete coverage and every supported reference resolved.</summary>
    Complete,

    /// <summary>At least one member is partial/unsupported/skipped, or at least one edge is unresolved.</summary>
    Partial,
}

public enum ResolutionStatus
{
    Resolved,
    Unresolved,
}

/// <summary>
/// Provenance carried by every derived item: the snapshot, the F1 parser version and index format,
/// and the index analysis run. The cited F0 evidence references are separate and unchanged.
/// </summary>
public sealed record IndexProvenance(WorkspaceId WorkspaceId, SnapshotId SnapshotId, string AnalysisRunId, string ParserVersion, string IndexFormatVersion);

/// <summary>A per-member diagnostic with the F0 evidence for its line, when it has one.</summary>
public sealed record IndexDiagnostic(ParseDiagnosticCode Code, string Message, EvidenceReference? Evidence);

/// <summary>
/// Coverage for one snapshot member. <see cref="Diagnostics"/> is a bounded sample;
/// <see cref="DiagnosticTotals"/> counts every diagnostic encountered, by code, and
/// <see cref="SuppressedDiagnostics"/> how many of those are not in the sample.
/// </summary>
public sealed record IndexedMember(
    SnapshotMember Member,
    CoverageStatus Coverage,
    IReadOnlyList<IndexDiagnostic> Diagnostics,
    IReadOnlyDictionary<ParseDiagnosticCode, int> DiagnosticTotals,
    int SuppressedDiagnostics)
{
    public int DiagnosticTotal => DiagnosticTotals.Values.Sum();
}

/// <summary>One unsupported CL verb or DDS keyword across the snapshot, with a few cited examples.</summary>
public sealed record SyntaxGapGroup(SyntaxGapKind Kind, string Key, int Occurrences, int Members, IReadOnlyList<EvidenceReference> Examples);

/// <summary>
/// The private CL/DDS syntax-gap report: unsupported verbs and keywords grouped by key, bounded to
/// <see cref="MaxGroupsPerKind"/> groups per kind (most frequent first) with up to
/// <see cref="MaxExamples"/> cited examples each. It is local analysis data, never logged.
/// </summary>
public sealed record SyntaxGapReport(IReadOnlyList<SyntaxGapGroup> Groups, int OmittedGroups, int OmittedOccurrences)
{
    public const int MaxGroupsPerKind = 1_000;
    public const int MaxExamples = 3;
    public const int MaxKeyLength = 30;
}

/// <summary>Coverage and diagnostic totals for one source type.</summary>
public sealed record SourceTypeCoverage(SourceType SourceType, int Members, int Complete, int Partial, int Unsupported, int Diagnostics, int SuppressedDiagnostics);

/// <summary>A directly observed definition. <see cref="Name"/> keeps source spelling; matching uses <see cref="Key"/>.</summary>
public sealed record IndexedArtifact(
    ArtifactKind Kind,
    string Name,
    string? Qualifier,
    MemberIdentity Member,
    EvidenceReference Evidence,
    IndexProvenance Provenance)
{
    public string Key => IdentifierKey.Of(Name);
}

/// <summary>
/// One directly observed outgoing reference. <see cref="ReferenceEvidence"/> is where it was
/// observed; <see cref="DefinitionEvidence"/> is the resolved target's definition, when known.
/// <see cref="AssumedCallerLibrary"/> marks an unqualified target resolved in the source member's
/// manifest library (an F1 assumption, not IBM i library-list resolution).
/// </summary>
public sealed record DependencyEdge(
    EdgeKind Kind,
    MemberIdentity SourceMember,
    ArtifactKind SourceKind,
    string SourceName,
    string Target,
    string? TargetLibrary,
    string? TargetName,
    EvidenceReference ReferenceEvidence,
    ResolutionStatus Resolution,
    MemberIdentity? ResolvedMember,
    EvidenceReference? DefinitionEvidence,
    UnresolvedReason? Reason,
    int CandidateCount,
    bool AssumedCallerLibrary,
    IndexProvenance Provenance);

/// <summary><see cref="Diagnostics"/> is the true total encountered; <see cref="DiagnosticsShown"/> are stored samples.</summary>
public sealed record IndexCounts(
    int Members,
    int CompleteMembers,
    int PartialMembers,
    int UnsupportedMembers,
    int Artifacts,
    int Edges,
    int ResolvedEdges,
    int UnresolvedEdges,
    int Diagnostics,
    int DiagnosticsShown,
    int SuppressedDiagnostics);

/// <summary>A complete, snapshot-scoped index as built or as loaded after validation.</summary>
public sealed record SourceIndex(
    IndexProvenance Provenance,
    string SnapshotAnalysisRunId,
    DateTimeOffset CreatedUtc,
    IndexLimits Limits,
    IReadOnlyList<IndexedMember> Members,
    IReadOnlyList<IndexedArtifact> Artifacts,
    IReadOnlyList<DependencyEdge> Edges,
    SyntaxGapReport SyntaxGaps)
{
    public IndexCounts Counts { get; } = new(
        Members.Count,
        Members.Count(m => m.Coverage == CoverageStatus.Complete),
        Members.Count(m => m.Coverage == CoverageStatus.Partial),
        Members.Count(m => m.Coverage == CoverageStatus.Unsupported),
        Artifacts.Count,
        Edges.Count,
        Edges.Count(e => e.Resolution == ResolutionStatus.Resolved),
        Edges.Count(e => e.Resolution == ResolutionStatus.Unresolved),
        Members.Sum(m => m.DiagnosticTotal),
        Members.Sum(m => m.Diagnostics.Count(d => d.Code != ParseDiagnosticCode.DiagnosticsTruncated)),
        Members.Sum(m => m.SuppressedDiagnostics));

    /// <summary>Derived, never stored independently: complete only with full coverage and no unresolved edge.</summary>
    public IndexStatus Status => Counts.CompleteMembers == Counts.Members && Counts.UnresolvedEdges == 0
        ? IndexStatus.Complete
        : IndexStatus.Partial;

    public IndexSummary Summary => new(Provenance, CreatedUtc, Status, Limits, Counts,
        Members.GroupBy(m => m.Member.Identity.SourceType).OrderBy(g => g.Key).Select(g => new SourceTypeCoverage(
            g.Key, g.Count(), g.Count(m => m.Coverage == CoverageStatus.Complete), g.Count(m => m.Coverage == CoverageStatus.Partial),
            g.Count(m => m.Coverage == CoverageStatus.Unsupported), g.Sum(m => m.DiagnosticTotal), g.Sum(m => m.SuppressedDiagnostics))).ToList(),
        new SortedDictionary<string, int>(Members.SelectMany(m => m.DiagnosticTotals).GroupBy(t => Labels.Of(t.Key))
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Value)), StringComparer.Ordinal));
}

/// <summary>Index identity, status, counts, coverage by source type, and diagnostic totals by code, as shown with every command result.</summary>
public sealed record IndexSummary(
    IndexProvenance Provenance,
    DateTimeOffset CreatedUtc,
    IndexStatus Status,
    IndexLimits Limits,
    IndexCounts Counts,
    IReadOnlyList<SourceTypeCoverage> ByType,
    IReadOnlyDictionary<string, int> DiagnosticsByCode);

public sealed record IndexBuildOutcome(IndexSummary Index, IReadOnlyList<IndexedMember> Members);

public sealed record SearchHit(
    ArtifactKind Kind,
    string Name,
    string? Qualifier,
    MemberIdentity Member,
    CoverageStatus Coverage,
    EvidenceReference Evidence,
    IndexProvenance Provenance);

public sealed record SearchResult(IndexSummary Index, string Query, int TotalMatches, bool Truncated, IReadOnlyList<SearchHit> Hits);

public sealed record SyntaxGapResult(IndexSummary Index, SyntaxGapReport Report);

public sealed record DependencyResult(
    IndexSummary Index,
    MemberIdentity Member,
    CoverageStatus Coverage,
    int TotalEdges,
    int UnresolvedEdges,
    bool Truncated,
    IReadOnlyList<DependencyEdge> Edges);

/// <summary>
/// Persistence boundary for published indexes. Implementations stage and atomically publish, and
/// refuse to load anything that does not validate against the given F0 snapshot.
/// </summary>
public interface IIndexRepository
{
    OperationResult<IndexSummary> Publish(SourceIndex index, SnapshotSummary snapshot, CancellationToken cancellationToken = default);

    /// <summary>Loads the most recent published index run for the snapshot and parser version.</summary>
    OperationResult<SourceIndex> LoadLatest(SnapshotSummary snapshot, string parserVersion);
}
