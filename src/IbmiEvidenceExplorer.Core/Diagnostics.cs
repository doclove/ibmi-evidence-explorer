namespace IbmiEvidenceExplorer.Core;

/// <summary>Typed reasons an operation was rejected. Names are stable and appear in CLI output.</summary>
public enum DiagnosticCode
{
    InvalidWorkspaceId,
    InvalidSnapshotId,
    InvalidLimit,
    InvalidArgument,
    StorageUnavailable,
    SourceRootNotFound,
    ManifestNotFound,
    ManifestTooLarge,
    ManifestMalformed,
    ManifestSchemaInvalid,
    UnsupportedFormatVersion,
    InvalidIdentityName,
    UnsupportedSourceType,
    UnsupportedCcsid,
    DuplicateMemberIdentity,
    InvalidPath,
    PathTraversal,
    SymlinkRejected,
    SourceFileNotFound,
    InvalidUtf8,
    MemberTooLarge,
    ImportTooLarge,
    TooManyMembers,
    ImportCancelled,
    PublishFailed,
    SnapshotNotFound,
    MemberNotFound,
    LineOutOfRange,
    IntegrityFailure,
    EvidenceMismatch,
    SourceUnreadable,

    // F1 parser/index. Appended so existing names keep their meaning.
    InvalidQuery,
    IndexNotFound,
    IndexIntegrityFailure,
    IndexSchemaUnsupported,
    IndexSnapshotMismatch,
    IndexCancelled,

    // F1.5 source preparation.
    PreparationPlanInvalid,
    PreparationOutputExists,
    PreparationCancelled,
    NothingToPrepare,
    TooManyEntries,

    // F2 Explorer and local AI explanation.
    ContextTooLarge,
    ExplainCancelled,
    LocalAiNotAcknowledged,
    LocalAiEndpointRejected,
    LocalAiUnavailable,
    LocalAiModelNotFound,
    LocalAiModelNotLocal,
    LocalAiTimeout,
    LocalAiHttpError,
    LocalAiResponseTooLarge,
    LocalAiInvalidResponse,
    LocalAiInvalidOutput,
    LocalAiRefused,
    LocalAiIdentityMismatch,
    LocalAiCitationInvalid,
    LocalAiModelMismatch,
}

/// <summary>
/// A diagnostic for the operator. Messages never contain source text; they reference manifest
/// entries by index and path, and file content only by byte offset and line number.
/// </summary>
public sealed record Diagnostic(DiagnosticCode Code, string Message, int? EntryIndex = null, string? Path = null)
{
    public override string ToString()
    {
        var location = (EntryIndex, Path) switch
        {
            (not null, not null) => $" [entry {EntryIndex}, path '{Path}']",
            (not null, null) => $" [entry {EntryIndex}]",
            (null, not null) => $" [path '{Path}']",
            _ => string.Empty,
        };
        return $"{Code}: {Message}{location}";
    }
}

/// <summary>Either a value or one or more diagnostics; never both.</summary>
public sealed class OperationResult<T>
{
    private readonly T? _value;

    private OperationResult(T? value, IReadOnlyList<Diagnostic> diagnostics)
    {
        _value = value;
        Diagnostics = diagnostics;
    }

    public bool Succeeded => Diagnostics.Count == 0;

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException("Operation failed; inspect Diagnostics.");

    public static OperationResult<T> Success(T value) => new(value, []);

    public static OperationResult<T> Failure(IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Count == 0)
        {
            throw new ArgumentException("A failure requires at least one diagnostic.", nameof(diagnostics));
        }

        return new(default, diagnostics);
    }

    public static OperationResult<T> Failure(Diagnostic diagnostic) => Failure([diagnostic]);
}
