using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Infrastructure;

// Persisted, versioned documents (schemaVersion 1). Stored file names are derived from the member
// ordinal and never read from metadata, so tampered metadata cannot redirect reads.

internal sealed class SnapshotMetadataDocument
{
    public required int SchemaVersion { get; init; }
    public required string ImportFormatVersion { get; init; }
    public required string ParserVersion { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SnapshotId { get; init; }
    public required string AnalysisRunId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string ManifestSha256 { get; init; }
    public required LimitsDocument Limits { get; init; }
    public required long TotalBytes { get; init; }
    public required int MemberCount { get; init; }
    public required IReadOnlyList<MemberDocument> Members { get; init; }
}

internal sealed class LimitsDocument
{
    public required long MaxMemberBytes { get; init; }
    public required long MaxImportBytes { get; init; }
    public required int MaxMembers { get; init; }
}

internal sealed class MemberDocument
{
    public required int Ordinal { get; init; }
    public required string Library { get; init; }
    public required string SourceFile { get; init; }
    public required string Member { get; init; }
    public required SourceType SourceType { get; init; }
    public required int Ccsid { get; init; }
    public required string SourcePath { get; init; }
    public required long ByteLength { get; init; }
    public required string Sha256 { get; init; }
    public required int LineCount { get; init; }
}

internal sealed class CompletionMarkerDocument
{
    public required int SchemaVersion { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SnapshotId { get; init; }
    public required string MetadataSha256 { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
}

internal static class SnapshotJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
