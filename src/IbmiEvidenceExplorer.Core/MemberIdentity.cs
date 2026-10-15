using System.Text.RegularExpressions;

namespace IbmiEvidenceExplorer.Core;

/// <summary>Declared source type. A metadata label only; F0 does not parse language syntax.</summary>
public enum SourceType
{
    RPG,
    CL,
    DDS,
    SQL,
}

public static class SourceTypes
{
    /// <summary>Exact, case-sensitive match against the manifest labels.</summary>
    public static bool TryParse(string? value, out SourceType type)
    {
        switch (value)
        {
            case "RPG": type = SourceType.RPG; return true;
            case "CL": type = SourceType.CL; return true;
            case "DDS": type = SourceType.DDS; return true;
            case "SQL": type = SourceType.SQL; return true;
            default: type = default; return false;
        }
    }
}

/// <summary>
/// Library / source file / member / source type within a snapshot. Names follow the IBM i
/// unquoted system-name form: 1-10 characters, uppercase A-Z, 0-9, $, #, @, _ or ., not starting
/// with a digit, underscore, or period. Values are kept exactly as declared.
/// </summary>
public sealed partial record MemberIdentity(string Library, string SourceFile, string Member, SourceType SourceType)
{
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public override string ToString() => $"{Library}/{SourceFile}/{Member} ({SourceType})";

    [GeneratedRegex("^[A-Z$#@][A-Z0-9$#@_.]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
