using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Core.Exploration;

/// <summary>Version labels recorded with every F2 dossier, context packet, and explanation.</summary>
public static class ExplorerFormats
{
    /// <summary>The dossier selection and validation rules. Changing either requires a new value.</summary>
    public const string ExplorerVersion = "ibmi-explorer-f2-explorer/2";

    /// <summary>Layout of the dossier returned by <c>explore</c>.</summary>
    public const string DossierFormat = "ibmi-explorer-dossier/2";
}

/// <summary>
/// Fixed F2 retrieval bounds (feature item 4): one member, 40 direct edges per direction, 64
/// numbered source lines and 16 KiB of decoded source. Changing a bound requires a recorded decision.
/// </summary>
public static class ExplorerLimits
{
    public const int MaxEdgesPerDirection = 40;
    public const int MaxEvidenceLines = 64;
    public const int MaxEvidenceBytes = 16 * 1024;

    /// <summary>Members read for re-derivation: the selected member plus displayed callers and displayed resolved targets.</summary>
    public const int MaxRederivedMembers = 1 + (2 * MaxEdgesPerDirection);
}

public enum EdgeDirection
{
    /// <summary>A reference observed in the selected member.</summary>
    Outgoing,

    /// <summary>A reference observed in another member that F1 resolved to the selected member.</summary>
    Incoming,
}

/// <summary>
/// <c>complete</c> only when the member and index are F1-complete, every outgoing edge resolved, and
/// nothing was truncated or omitted by the dossier bounds. It never means behavior is modeled.
/// </summary>
public enum DossierCompleteness
{
    Complete,
    Partial,
}

/// <summary>Why a source line was selected, in the fixed priority order of the selection rule.</summary>
public enum EvidencePurpose
{
    MemberDefinition,
    OutgoingReference,
    IncomingReference,
    TargetDefinition,
    Diagnostic,
    MemberContext,
}

/// <summary>One numbered source line selected for the dossier, verified through F0 before display.</summary>
public sealed record DossierEvidence(string Id, EvidenceReference Reference, string Text, IReadOnlyList<EvidencePurpose> Purposes);

/// <summary>The selected member's source-backed definition (an F1 fact).</summary>
public sealed record DossierDefinition(string FactId, IndexedArtifact Artifact, string? EvidenceId);

/// <summary>
/// One direct relationship. Every F1 edge is a directly parsed reference; its resolution (if any)
/// comes from F1 rules, which <see cref="Basis"/> names. Evidence IDs are null when the line was
/// omitted by the packet bound; the edge's F0 references are still verified.
/// </summary>
public sealed record DossierRelationship(
    string FactId,
    EdgeDirection Direction,
    DependencyEdge Edge,
    string? ReferenceEvidenceId,
    string? DefinitionEvidenceId)
{
    public string Observation { get; } = "parsed-reference";

    public string Basis => Edge.Resolution != ResolutionStatus.Resolved ? "unresolved"
        : Edge.Kind == EdgeKind.KeysField ? "resolved-by-f1-record-rule"
        : Edge.AssumedCallerLibrary ? "resolved-by-f1-rule-assumed-caller-library"
        : "resolved-by-f1-rule";
}

/// <summary>Totals for one direction: all matching edges, how many are shown, and unresolved reasons.</summary>
public sealed record RelationshipCounts(int Total, int Included, int Omitted, int Unresolved, IReadOnlyDictionary<string, int> UnresolvedByReason);

/// <summary>How many candidate lines the selection rule considered and which bounds omitted lines.</summary>
public sealed record EvidenceSelection(
    int CandidateLines,
    int IncludedLines,
    int OmittedLines,
    int IncludedBytes,
    int MaxLines,
    int MaxBytes,
    IReadOnlyDictionary<string, int> OmittedByPurpose);

/// <summary>
/// Which facts were re-derived from the snapshot source with the index's parser version, and
/// which were not. Displayed outgoing and incoming edges are fully re-derived; omitted outgoing
/// edges have reference, target, and resolution re-derived but not their target definitions;
/// omitted incoming edges are not re-derived. At most <see cref="ExplorerLimits.MaxRederivedMembers"/>
/// members are re-parsed (<see cref="MembersReparsed"/>).
/// </summary>
public sealed record RederivationCheck(
    string ParserVersion,
    bool SelectedMemberReparsed,
    int OutgoingEdgesRederived,
    int OutgoingEdgesWithoutTargetDefinition,
    int IncomingEdgesRederived,
    int IncomingEdgesNotRederived,
    int MembersReparsed,
    IReadOnlyList<string> NotRederived);

/// <summary>A labeled gap: something the dossier cannot show or does not know.</summary>
public sealed record DossierGap(string Code, string Text);

/// <summary>
/// Deterministic, provider-free view of one exact member: index identity, coverage, definition,
/// bounded direct edges, verified numbered evidence, and explicit gaps. Two runs over the same index
/// produce equal content.
/// </summary>
public sealed record MemberDossier(
    string DossierFormat,
    string ExplorerVersion,
    IndexSummary Index,
    string SnapshotAnalysisRunId,
    SnapshotMember Member,
    CoverageStatus Coverage,
    IReadOnlyDictionary<string, int> DiagnosticTotals,
    int SuppressedDiagnostics,
    IReadOnlyList<IndexDiagnostic> Diagnostics,
    DossierDefinition? Definition,
    IReadOnlyList<DossierRelationship> Outgoing,
    RelationshipCounts OutgoingCounts,
    IReadOnlyList<DossierRelationship> Incoming,
    RelationshipCounts IncomingCounts,
    int UnresolvedNameMatches,
    IReadOnlyList<DossierEvidence> Evidence,
    EvidenceSelection EvidenceSelection,
    RederivationCheck Rederivation,
    DossierCompleteness Completeness,
    IReadOnlyList<DossierGap> Gaps,
    IReadOnlyList<string> Notes)
{
    public SnapshotId SnapshotId => Index.Provenance.SnapshotId;

    public MemberIdentity Identity => Member.Identity;
}
