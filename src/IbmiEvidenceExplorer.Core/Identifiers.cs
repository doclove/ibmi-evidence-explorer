using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace IbmiEvidenceExplorer.Core;

/// <summary>
/// Caller-supplied workspace identifier. Lowercase letters, digits, and hyphens only (1-63 chars,
/// starting with a letter or digit), so it is always a single safe path segment and cannot collide
/// on case-insensitive file systems.
/// </summary>
public readonly partial record struct WorkspaceId
{
    private WorkspaceId(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse([NotNullWhen(true)] string? value, out WorkspaceId id)
    {
        if (value is not null && Pattern().IsMatch(value))
        {
            id = new WorkspaceId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Generated identifier for one published import: <c>snap-</c> plus 32 lowercase hex digits.</summary>
public readonly partial record struct SnapshotId
{
    private SnapshotId(string value) => Value = value;

    public string Value { get; }

    public static SnapshotId New() => new("snap-" + Guid.CreateVersion7().ToString("N"));

    public static bool TryParse([NotNullWhen(true)] string? value, out SnapshotId id)
    {
        if (value is not null && Pattern().IsMatch(value))
        {
            id = new SnapshotId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^snap-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Identifies one execution of an import (the F0 analysis run).</summary>
public static class AnalysisRunId
{
    public static string New() => "run-" + Guid.CreateVersion7().ToString("N");
}

/// <summary>Version labels recorded with every snapshot and evidence reference.</summary>
public static class FormatVersions
{
    /// <summary>The input contract accepted by F0: a UTF-8 directory export with a JSON manifest.</summary>
    public const string ImportFormat = "ibmi-explorer-utf8-directory/1";

    /// <summary>F0 has no parser; recorded explicitly rather than invented.</summary>
    public const string ParserVersion = "none";

    /// <summary>Version of the persisted snapshot metadata and completion marker documents.</summary>
    public const int SnapshotSchema = 1;

    /// <summary>The only manifest <c>formatVersion</c> F0 accepts.</summary>
    public const int ManifestFormat = 1;

    /// <summary>The only CCSID F0 accepts (UTF-8).</summary>
    public const int Utf8Ccsid = 1208;
}
