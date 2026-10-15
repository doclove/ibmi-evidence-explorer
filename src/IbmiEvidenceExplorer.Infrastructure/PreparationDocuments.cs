using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Preparation;

namespace IbmiEvidenceExplorer.Infrastructure;

// The private preparation report (preparation-report.json). It is written beside the prepared
// export for an authorized operator; F0 never reads it and it is not copied into any index.

internal enum ReportOutcome
{
    Prepared,
    Rejected,
    Skipped,
}

internal sealed class PreparationReportDocument
{
    public required int FormatVersion { get; init; }

    public required string TransformVersion { get; init; }

    public required string Layout { get; init; }

    public required string PreparationRunId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string PlanSha256 { get; init; }

    public required PreparationStatus Status { get; init; }

    /// <summary>How prepared lines map back to the original input; the same rule for every member.</summary>
    public required string LineMapping { get; init; }

    public required IReadOnlyList<ReportEntryDocument> Entries { get; init; }
}

internal sealed class ReportEntryDocument
{
    public required string OriginalPath { get; init; }

    public required ReportOutcome Outcome { get; init; }

    public PreparationReason? Reason { get; init; }

    /// <summary>Byte offset in the original input where conversion failed, when known.</summary>
    public long? ReasonOffset { get; init; }

    public string? Library { get; init; }

    public string? SourceFile { get; init; }

    public string? Member { get; init; }

    public SourceType? SourceType { get; init; }

    public int? Ccsid { get; init; }

    public RecordLayout? RecordLayout { get; init; }

    public int? RecordWidth { get; init; }

    public long? OriginalBytes { get; init; }

    public string? OriginalSha256 { get; init; }

    public int? ManifestIndex { get; init; }

    public string? PreparedPath { get; init; }

    public long? PreparedBytes { get; init; }

    public string? PreparedSha256 { get; init; }

    public string? Transform { get; init; }

    /// <summary>Original records, equal to the prepared member's line count.</summary>
    public int? RecordCount { get; init; }
}

/// <summary>The F0 manifest written for the prepared export (unchanged F0 format, CCSID 1208 only).</summary>
internal sealed class PreparedManifestDocument
{
    public required int FormatVersion { get; init; }

    public required IReadOnlyList<PreparedManifestEntry> Members { get; init; }
}

internal sealed class PreparedManifestEntry
{
    public required string Path { get; init; }

    public required string Library { get; init; }

    public required string SourceFile { get; init; }

    public required string Member { get; init; }

    public required SourceType SourceType { get; init; }

    public required int Ccsid { get; init; }
}
