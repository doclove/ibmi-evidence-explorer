namespace IbmiEvidenceExplorer.Core;

/// <summary>Resource limits applied to one import. Every value is bounded by a hard maximum.</summary>
public sealed record ImportLimits(long MaxMemberBytes, long MaxImportBytes, int MaxMembers)
{
    public const long MiB = 1024 * 1024;

    public const long DefaultMaxMemberBytes = 10 * MiB;
    public const long DefaultMaxImportBytes = 100 * MiB;
    public const int DefaultMaxMembers = 10_000;

    public const long UpperBoundMemberBytes = 64 * MiB;
    public const long UpperBoundImportBytes = 1024 * MiB;
    public const int UpperBoundMembers = 100_000;

    /// <summary>The manifest itself is capped separately and is not configurable.</summary>
    public const long MaxManifestBytes = 8 * MiB;

    public static ImportLimits Default { get; } = new(DefaultMaxMemberBytes, DefaultMaxImportBytes, DefaultMaxMembers);

    /// <summary>Builds limits from optional overrides, rejecting values outside 1..upper bound.</summary>
    public static OperationResult<ImportLimits> Create(long? maxMemberBytes = null, long? maxImportBytes = null, int? maxMembers = null)
    {
        var diagnostics = new List<Diagnostic>();
        var member = maxMemberBytes ?? DefaultMaxMemberBytes;
        var import = maxImportBytes ?? DefaultMaxImportBytes;
        var members = maxMembers ?? DefaultMaxMembers;

        if (member < 1 || member > UpperBoundMemberBytes)
        {
            diagnostics.Add(new(DiagnosticCode.InvalidLimit, $"Max member bytes must be between 1 and {UpperBoundMemberBytes}."));
        }

        if (import < 1 || import > UpperBoundImportBytes)
        {
            diagnostics.Add(new(DiagnosticCode.InvalidLimit, $"Max import bytes must be between 1 and {UpperBoundImportBytes}."));
        }

        if (members < 1 || members > UpperBoundMembers)
        {
            diagnostics.Add(new(DiagnosticCode.InvalidLimit, $"Max members must be between 1 and {UpperBoundMembers}."));
        }

        return diagnostics.Count == 0
            ? OperationResult<ImportLimits>.Success(new(member, import, members))
            : OperationResult<ImportLimits>.Failure(diagnostics);
    }
}
