namespace IbmiEvidenceExplorer.Core.Preparation;

/// <summary>Result of <c>prepare scan</c>: counts only. Paths and identities are in the private plan file.</summary>
public sealed record PreparationScanOutcome(string PlanPath, string PlanSha256, PreparationCounts Counts);

public enum PreparationStatus
{
    /// <summary>Every included plan entry was prepared.</summary>
    Complete,

    /// <summary>At least one included entry was rejected with a typed reason and is absent from the export.</summary>
    Partial,
}

/// <summary>Result of <c>prepare build</c>: counts and codes only. Details are in the private report.</summary>
public sealed record PreparationBuildOutcome(
    string OutputPath,
    string PreparationRunId,
    PreparationStatus Status,
    int PlanEntries,
    int Included,
    int Prepared,
    int Rejected,
    int Skipped,
    long InputBytes,
    long PreparedBytes,
    IReadOnlyDictionary<string, int> PreparedByType,
    IReadOnlyDictionary<string, int> RejectedByReason,
    IReadOnlyDictionary<string, int> SkippedByReason);

public enum PreparationStage
{
    MembersWritten,
    ManifestWritten,
    ReportWritten,
}
