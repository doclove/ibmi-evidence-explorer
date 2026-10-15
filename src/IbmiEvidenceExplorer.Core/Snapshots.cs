namespace IbmiEvidenceExplorer.Core;

/// <summary>A member as recorded in a published snapshot.</summary>
public sealed record SnapshotMember(
    int Ordinal,
    MemberIdentity Identity,
    string SourcePath,
    int Ccsid,
    long ByteLength,
    string Sha256,
    int LineCount);

/// <summary>A snapshot that passed atomic publication and integrity checks.</summary>
public sealed record SnapshotSummary(
    WorkspaceId WorkspaceId,
    SnapshotId SnapshotId,
    string AnalysisRunId,
    DateTimeOffset CreatedUtc,
    string ImportFormatVersion,
    string ParserVersion,
    ImportLimits AppliedLimits,
    long TotalBytes,
    IReadOnlyList<SnapshotMember> Members)
{
    /// <summary>Only published, verified snapshots are ever returned, so the status is always complete.</summary>
    public string Status { get; } = "complete";
}

/// <summary>Result of listing a workspace: the complete snapshots, plus how many entries were ignored as invalid.</summary>
public sealed record SnapshotListing(WorkspaceId WorkspaceId, IReadOnlyList<SnapshotSummary> Snapshots, int IgnoredEntries);

/// <summary>
/// A stable reference from a derived item (or an operator) to one line of exact source bytes.
/// </summary>
public sealed record EvidenceReference(
    SnapshotId SnapshotId,
    MemberIdentity Member,
    int LineNumber,
    string MemberSha256,
    string LineSha256,
    long ByteOffset,
    int ByteLength,
    string ImportFormatVersion,
    string ParserVersion,
    string AnalysisRunId)
{
    public override string ToString() =>
        $"ibmi-explorer:{SnapshotId}/{Member.Library}/{Member.SourceFile}/{Member.Member}/{Member.SourceType}#L{LineNumber}";
}

/// <summary>One line of a member together with its evidence reference and decoded text.</summary>
public sealed record EvidenceLine(EvidenceReference Reference, string Text);

/// <summary>Member metadata and the requested numbered lines, verified against the stored hash.</summary>
public sealed record MemberInspection(SnapshotSummary Snapshot, SnapshotMember Member, IReadOnlyList<EvidenceLine> Lines);

/// <summary>The exact bytes an evidence reference points to.</summary>
public sealed record ResolvedEvidence(EvidenceReference Reference, byte[] Bytes, string Text);

/// <summary>Inclusive 1-based line range for inspection.</summary>
public sealed record LineRange(int First, int Last);

public enum ImportStage
{
    SourcesValidated,
    MemberStaged,
    MetadataWritten,
    MarkerWritten,
}

public sealed record ImportRequest(WorkspaceId WorkspaceId, string SourceRoot, ImportLimits Limits);

public sealed record ImportOutcome(SnapshotSummary Snapshot);

/// <summary>
/// Workspace-scoped snapshot operations. Every call requires an explicit workspace; no operation can
/// enumerate or read another workspace.
/// </summary>
public interface ISnapshotStore
{
    OperationResult<ImportOutcome> Import(ImportRequest request, CancellationToken cancellationToken = default);

    OperationResult<SnapshotListing> List(WorkspaceId workspaceId);

    OperationResult<MemberInspection> Inspect(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member, LineRange? lines = null);

    OperationResult<ResolvedEvidence> Resolve(WorkspaceId workspaceId, EvidenceReference reference);

    /// <summary>
    /// Verifies one published snapshot (storage, completion marker, metadata) once and returns a
    /// reader for its members. Used by F1 indexing so metadata is not re-parsed for every member.
    /// </summary>
    OperationResult<ISnapshotReader> OpenSnapshot(WorkspaceId workspaceId, SnapshotId snapshotId);
}

/// <summary>
/// Reads members of one verified snapshot. Every read re-verifies the stored bytes against the
/// SHA-256 recorded in the snapshot metadata; evidence references are the unchanged F0 references.
/// </summary>
public interface ISnapshotReader
{
    SnapshotSummary Snapshot { get; }

    /// <summary>All lines of a member with their F0 evidence references, after hash verification.</summary>
    OperationResult<MemberInspection> ReadMember(MemberIdentity member);

    /// <summary>Re-hashes a member's stored bytes without decoding lines.</summary>
    OperationResult<SnapshotMember> VerifyMember(MemberIdentity member);
}
