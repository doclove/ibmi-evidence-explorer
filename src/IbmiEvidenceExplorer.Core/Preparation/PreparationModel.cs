using System.Text.Json.Serialization;

namespace IbmiEvidenceExplorer.Core.Preparation;

/// <summary>Versions recorded in preparation plans and reports (F1.5, D-014).</summary>
public static class PreparationFormats
{
    /// <summary>Version of the editable plan document written by <c>prepare scan</c>.</summary>
    public const int PlanFormatVersion = 1;

    /// <summary>Version of the private <c>preparation-report.json</c> written by <c>prepare build</c>.</summary>
    public const int ReportFormatVersion = 1;

    /// <summary>The conversion rules in <see cref="SourceTranscoder"/>. Changing them requires a new value.</summary>
    public const string TransformVersion = "ibmi-explorer-prepare/1";

    /// <summary>The only discovery layout in F1.5: <c>LIBRARY/SOURCEFILE/[TYPE/]MEMBER.extension[.utf8]</c>.</summary>
    public const string ArchivedLibraryTree = "archived-library-tree";

    public const string PlanReportFileName = "preparation-report.json";

    /// <summary>The single fixed record width approved for F1.5 (source-data-only records).</summary>
    public const int SourceDataRecordWidth = 80;

    public const int Utf8Ccsid = 1208;

    /// <summary>Single-byte EBCDIC CCSIDs with built-in, tested mappings.</summary>
    public static readonly IReadOnlyList<int> SupportedCcsids = [Utf8Ccsid, 37, 1140];
}

/// <summary>How the bytes of one input divide into source records.</summary>
public enum RecordLayout
{
    /// <summary>Records end at LF or CRLF (F0's rule). UTF-8 only.</summary>
    Lines,

    /// <summary>Concatenated source-data-only records of a fixed width, with no separators.</summary>
    FixedSourceData,

    /// <summary>
    /// IBM i source physical-file records with sequence and date fields (for example 92 bytes for
    /// 80 source characters). Declarable so the plan can state it, but never converted in F1.5.
    /// </summary>
    RawSourceRecord,
}

public enum PlanEntryState
{
    Included,
    Skipped,
}

/// <summary>Typed reasons an archive entry was skipped at scan or rejected at build. Labels are kebab-case.</summary>
public enum PreparationReason
{
    // Discovery (scan).
    UnknownLayout,
    UnknownExtension,
    InvalidName,
    DuplicateIdentity,
    SymbolicLink,
    NotRegularFile,
    Unreadable,
    MemberTooLarge,
    TotalLimitExceeded,

    // Plan choices (build).
    PathEscape,
    IdentityMismatch,
    Unconfirmed,
    MissingChoice,
    UnsupportedCcsid,
    UnsupportedRecordLayout,
    UnsupportedRecordWidth,
    SourceFileNotFound,
    InputChanged,
    ContradictoryChoice,

    // Conversion (build).
    InvalidEncoding,
    UnmappableCharacter,
    EmbeddedLineSeparator,
    ShortFinalRecord,
}

/// <summary>Read-only facts the scan observed about an input, to help review. Never used as a decision.</summary>
public sealed class PlanObservations
{
    public required bool Utf8Bom { get; init; }

    public required bool ValidUtf8 { get; init; }

    public required bool LineSeparators { get; init; }
}

/// <summary>
/// One discovered input. Identity is derived from <see cref="Path"/> and cannot be edited into a
/// different identity; the operator chooses <see cref="State"/>, <see cref="Ccsid"/>,
/// <see cref="RecordLayout"/>, <see cref="RecordWidth"/>, and sets <see cref="Confirmed"/>.
/// </summary>
public sealed record PlanEntry
{
    public required string Path { get; init; }

    public string? Library { get; init; }

    public string? SourceFile { get; init; }

    public string? Member { get; init; }

    public SourceType? SourceType { get; init; }

    public required PlanEntryState State { get; init; }

    public PreparationReason? Reason { get; init; }

    public int? Ccsid { get; init; }

    public RecordLayout? RecordLayout { get; init; }

    public int? RecordWidth { get; init; }

    public required bool Confirmed { get; init; }

    public long? InputBytes { get; init; }

    public string? InputSha256 { get; init; }

    public PlanObservations? Observations { get; init; }

    [JsonIgnore]
    public MemberIdentity? Identity =>
        Library is not null && SourceFile is not null && Member is not null && SourceType is { } type
            ? new MemberIdentity(Library, SourceFile, Member, type)
            : null;
}

/// <summary>The editable, versioned plan. Unknown fields and duplicate properties are rejected when read.</summary>
public sealed class PreparationPlan
{
    public required int FormatVersion { get; init; }

    public required string Layout { get; init; }

    public required string TransformVersion { get; init; }

    public required IReadOnlyList<PlanEntry> Entries { get; init; }
}

/// <summary>Bounds for one scan or build. Fixed for F1.5; they bound reads and output, not the F0 import.</summary>
public sealed record PreparationLimits(long MaxMemberBytes, long MaxTotalBytes, int MaxEntries)
{
    /// <summary>Per member as F0's default; total and entries as F0's upper bounds; plan file 64 MiB.</summary>
    public static PreparationLimits Default { get; } = new(ImportLimits.DefaultMaxMemberBytes, ImportLimits.UpperBoundImportBytes, ImportLimits.UpperBoundMembers);

    public const long MaxPlanBytes = 64 * ImportLimits.MiB;
}

/// <summary>Operator choices applied to every entry a scan writes; each entry still starts unconfirmed.</summary>
public sealed record ScanDefaults(int? Ccsid, RecordLayout? RecordLayout, int? RecordWidth);

/// <summary>Counts by state, reason, and source type; contains no identifiers or paths.</summary>
public sealed record PreparationCounts(
    int Discovered,
    int Included,
    int Skipped,
    int Unconfirmed,
    long ProjectedBytes,
    IReadOnlyDictionary<string, int> IncludedByType,
    IReadOnlyDictionary<string, int> SkippedByReason)
{
    public static PreparationCounts Of(IReadOnlyList<PlanEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var included = entries.Where(e => e.State == PlanEntryState.Included).ToList();
        return new(
            entries.Count,
            included.Count,
            entries.Count - included.Count,
            included.Count(e => !e.Confirmed),
            included.Sum(e => e.InputBytes ?? 0),
            Group(included.Select(e => e.SourceType?.ToString() ?? "unknown")),
            Group(entries.Where(e => e.State == PlanEntryState.Skipped).Select(e => e.Reason is { } r ? Parsing.Labels.Of(r) : "unspecified")));
    }

    public static SortedDictionary<string, int> Group(IEnumerable<string> keys)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            result[key] = result.GetValueOrDefault(key) + 1;
        }

        return result;
    }
}

/// <summary>
/// Maps a relative archive path to an identity under <see cref="PreparationFormats.ArchivedLibraryTree"/>.
/// The first two segments are library and source file, an optional third directory is ignored, and
/// the file name is <c>MEMBER.extension</c> with an optional final <c>.utf8</c>. Names are matched
/// case-insensitively and recorded in upper case (IBM i system names); anything else is skipped.
/// </summary>
public static class ArchivePaths
{
    /// <summary>The approved extension table. The suffix is not proof of, for example, physical-file semantics.</summary>
    public static readonly IReadOnlyDictionary<string, SourceType> Extensions = new Dictionary<string, SourceType>(StringComparer.OrdinalIgnoreCase)
    {
        ["rpg"] = SourceType.RPG,
        ["rpgle"] = SourceType.RPG,
        ["clp"] = SourceType.CL,
        ["clle"] = SourceType.CL,
        ["dds"] = SourceType.DDS,
        ["pf"] = SourceType.DDS,
        ["lf"] = SourceType.DDS,
        ["dspf"] = SourceType.DDS,
        ["prtf"] = SourceType.DDS,
        ["sql"] = SourceType.SQL,
    };

    public const string Utf8Suffix = ".utf8";

    /// <summary>Directory depth (below the root) that may hold members: LIB/SRCF or LIB/SRCF/TYPE.</summary>
    public const int MaxDirectoryDepth = 3;

    /// <summary>Derives the identity, or the reason the path does not name a member.</summary>
    public static (MemberIdentity? Identity, PreparationReason? Reason) Derive(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var segments = relativePath.Split('/');
        if (segments.Length is < 3 or > 4 || segments.Any(s => s.Length == 0 || s is "." or ".."))
        {
            return (null, PreparationReason.UnknownLayout);
        }

        var file = segments[^1];
        var stem = file.EndsWith(Utf8Suffix, StringComparison.OrdinalIgnoreCase) ? file[..^Utf8Suffix.Length] : file;
        var dot = stem.LastIndexOf('.');
        if (dot <= 0 || !Extensions.TryGetValue(stem[(dot + 1)..], out var type))
        {
            return (null, PreparationReason.UnknownExtension);
        }

        var library = Upper(segments[0]);
        var sourceFile = Upper(segments[1]);
        var member = Upper(stem[..dot]);
        return MemberIdentity.IsValidName(library) && MemberIdentity.IsValidName(sourceFile) && MemberIdentity.IsValidName(member)
            ? (new MemberIdentity(library, sourceFile, member, type), null)
            : (null, PreparationReason.InvalidName);
    }

    /// <summary>True when the suffix suggests UTF-8. A suggestion only; the operator confirms the CCSID.</summary>
    public static bool SuggestsUtf8(string relativePath) => relativePath.EndsWith(Utf8Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>ASCII-only upper-casing, so no locale or Unicode case rule can change a name.</summary>
    private static string Upper(string name) => string.Create(name.Length, name, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - 32) : source[i];
        }
    });
}
