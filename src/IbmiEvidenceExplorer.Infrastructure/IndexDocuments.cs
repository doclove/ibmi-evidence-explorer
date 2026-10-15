using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Infrastructure;

// Persisted F1 index documents (schemaVersion 1). facts.json holds only deterministic content, so
// re-indexing an unchanged snapshot with the same parser version reproduces it byte for byte;
// run metadata lives in index.json. Members are referenced by F0 ordinal; evidence is stored as
// line coordinates and rebuilt into the unchanged F0 reference from the verified snapshot.

internal sealed class IndexMetadataDocument
{
    public required int SchemaVersion { get; init; }
    public required string IndexFormatVersion { get; init; }
    public required string ParserVersion { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SnapshotId { get; init; }
    public required string SnapshotAnalysisRunId { get; init; }
    public required string AnalysisRunId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required IndexStatus Status { get; init; }
    public required IndexLimitsDocument Limits { get; init; }
    public required IndexCounts Counts { get; init; }
    public required string FactsSha256 { get; init; }
}

internal sealed class IndexLimitsDocument
{
    public required long MaxMemberBytes { get; init; }
    public required long MaxTotalBytes { get; init; }
    public required int MaxMembers { get; init; }
    public required int MaxItems { get; init; }
    public required int MaxQueryResults { get; init; }
}

internal sealed class IndexFactsDocument
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<IndexMemberDocument> Members { get; init; }
    public required IReadOnlyList<ArtifactDocument> Artifacts { get; init; }
    public required IReadOnlyList<EdgeDocument> Edges { get; init; }
    public required SyntaxGapsDocument SyntaxGaps { get; init; }
}

internal sealed class IndexMemberDocument
{
    public required int Ordinal { get; init; }
    public required string Library { get; init; }
    public required string SourceFile { get; init; }
    public required string Member { get; init; }
    public required SourceType SourceType { get; init; }
    public required string Sha256 { get; init; }
    public required CoverageStatus Coverage { get; init; }
    public required IReadOnlyList<DiagnosticDocument> Diagnostics { get; init; }

    /// <summary>Every diagnostic encountered, by kebab-case code label; the sample above is bounded.</summary>
    public required IReadOnlyDictionary<string, int> DiagnosticTotals { get; init; }
    public required int SuppressedDiagnostics { get; init; }
}

internal sealed class SyntaxGapsDocument
{
    public required IReadOnlyList<SyntaxGapGroupDocument> Groups { get; init; }
    public required int OmittedGroups { get; init; }
    public required int OmittedOccurrences { get; init; }
}

internal sealed class SyntaxGapGroupDocument
{
    public required SyntaxGapKind Kind { get; init; }
    public required string Key { get; init; }
    public required int Occurrences { get; init; }
    public required int Members { get; init; }
    public required IReadOnlyList<LineDocument> Examples { get; init; }
}

internal sealed class DiagnosticDocument
{
    public required ParseDiagnosticCode Code { get; init; }
    public required string Message { get; init; }
    public LineDocument? Evidence { get; init; }
}

internal sealed class ArtifactDocument
{
    public required ArtifactKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Qualifier { get; init; }
    public required int Member { get; init; }
    public required LineDocument Evidence { get; init; }
}

internal sealed class EdgeDocument
{
    public required EdgeKind Kind { get; init; }
    public required int Member { get; init; }
    public required ArtifactKind SourceKind { get; init; }
    public required string SourceName { get; init; }
    public required string Target { get; init; }
    public string? TargetLibrary { get; init; }
    public string? TargetName { get; init; }
    public required LineDocument Evidence { get; init; }
    public required ResolutionStatus Resolution { get; init; }
    public int? ResolvedMember { get; init; }
    public LineDocument? DefinitionEvidence { get; init; }
    public UnresolvedReason? Reason { get; init; }
    public required int CandidateCount { get; init; }
    public required bool AssumedCallerLibrary { get; init; }
}

/// <summary>One cited line of one member (by F0 ordinal), as F0 recorded it.</summary>
internal sealed class LineDocument
{
    public required int Member { get; init; }
    public required int Line { get; init; }
    public required long ByteOffset { get; init; }
    public required int ByteLength { get; init; }
    public required string LineSha256 { get; init; }
}

internal sealed class IndexMarkerDocument
{
    public required int SchemaVersion { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SnapshotId { get; init; }
    public required string AnalysisRunId { get; init; }
    public required string ParserVersion { get; init; }
    public required string IndexSha256 { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
}

/// <summary>
/// Structural checks run on every deserialized document before any field is used. A stored
/// document is untrusted: JSON <c>null</c> can stand in for any required collection, object, or
/// string even when the hashes agree, so each one is checked here rather than dereferenced.
/// </summary>
internal static class IndexDocumentShape
{
    public static bool IsValid([NotNullWhen(true)] IndexMarkerDocument? marker) =>
        marker is { WorkspaceId: not null, SnapshotId: not null, AnalysisRunId: not null, ParserVersion: not null, IndexSha256: not null };

    public static bool IsValid([NotNullWhen(true)] IndexMetadataDocument? metadata) =>
        metadata is
        {
            IndexFormatVersion: not null, ParserVersion: not null, WorkspaceId: not null, SnapshotId: not null,
            SnapshotAnalysisRunId: not null, AnalysisRunId: not null, Limits: not null, Counts: not null, FactsSha256: not null,
        };

    public static bool IsValid([NotNullWhen(true)] IndexFactsDocument? facts) =>
        facts is { Members: not null, Artifacts: not null, Edges: not null, SyntaxGaps: { Groups: not null } }
        && facts.Members.All(IsValid) && facts.Artifacts.All(IsValid) && facts.Edges.All(IsValid)
        && facts.SyntaxGaps.Groups.All(g => g is { Key: not null, Examples: not null } && g.Examples.All(IsValid));

    private static bool IsValid(IndexMemberDocument? member) =>
        member is { Library: not null, SourceFile: not null, Member: not null, Sha256: not null, Diagnostics: not null, DiagnosticTotals: not null }
        && member.Diagnostics.All(d => d is { Message: not null } && (d.Evidence is null || IsValid(d.Evidence)))
        && member.DiagnosticTotals.Keys.All(k => k is not null);

    private static bool IsValid(ArtifactDocument? artifact) =>
        artifact is { Name: not null, Evidence: not null } && IsValid(artifact.Evidence);

    private static bool IsValid(EdgeDocument? edge) =>
        edge is { SourceName: not null, Target: not null, Evidence: not null } && IsValid(edge.Evidence)
        && (edge.DefinitionEvidence is null || IsValid(edge.DefinitionEvidence));

    private static bool IsValid(LineDocument? line) => line is { LineSha256: not null };
}

internal static class IndexJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter<SourceType>(namingPolicy: null, allowIntegerValues: false),
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false),
        },
    };
}
