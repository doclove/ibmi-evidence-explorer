using System.Text;
using System.Text.Json;

namespace IbmiEvidenceExplorer.Core;

/// <summary>One validated manifest entry: a relative path plus its declared identity.</summary>
public sealed record ManifestEntry(int Index, string RelativePath, MemberIdentity Identity, int Ccsid);

/// <summary>
/// Validates the F0 manifest contract. The manifest is untrusted; parsing is strict (no comments,
/// trailing commas, unknown or duplicate properties) and every problem found is reported.
/// </summary>
public static class ManifestParser
{
    public const string FileName = "manifest.json";

    private const int MaxPathLength = 1024;
    private const int MaxDisplayLength = 200;

    private static readonly string[] RootProperties = ["formatVersion", "members"];
    private static readonly string[] EntryProperties = ["path", "library", "sourceFile", "member", "sourceType", "ccsid"];

    public static OperationResult<IReadOnlyList<ManifestEntry>> Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (!Utf8Text.TryValidate(utf8Json, out var invalidOffset))
        {
            return Fail(new(DiagnosticCode.ManifestMalformed, $"Manifest is not valid UTF-8 (byte offset {invalidOffset})."));
        }

        var json = utf8Json.StartsWith(Utf8Text.Bom) ? utf8Json[Utf8Text.Bom.Length..] : utf8Json;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            // The exception message can quote input characters; report position only.
            return Fail(new(DiagnosticCode.ManifestMalformed,
                $"Manifest is not valid JSON (line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1})."));
        }

        using (document)
        {
            return Validate(document.RootElement);
        }
    }

    /// <summary>
    /// Lexical path rules: relative, '/'-separated, no empty, '.' or '..' segments, no backslash,
    /// colon, or control characters. Containment and links are checked again against the file system.
    /// </summary>
    public static Diagnostic? ValidateRelativePath(string path, int index)
    {
        var display = ForDisplay(path);
        if (path.Length == 0 || path.Length > MaxPathLength)
        {
            return new(DiagnosticCode.InvalidPath, $"Path must be 1-{MaxPathLength} characters.", index, display);
        }

        if (path.Any(c => char.IsControl(c) || c is '\\' or ':'))
        {
            return new(DiagnosticCode.InvalidPath, "Path contains a control character, backslash, or colon.", index, display);
        }

        if (path[0] == '/')
        {
            return new(DiagnosticCode.PathTraversal, "Path must be relative to the source root.", index, display);
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                return new(DiagnosticCode.PathTraversal, "Path must not contain '..' segments.", index, display);
            }

            if (segment.Length == 0 || segment == ".")
            {
                return new(DiagnosticCode.InvalidPath, "Path must not contain empty or '.' segments.", index, display);
            }
        }

        return null;
    }

    /// <summary>Escapes control characters and truncates, so untrusted values are safe to print or log.</summary>
    public static string ForDisplay(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxDisplayLength) + 8);
        foreach (var c in value.Length > MaxDisplayLength ? value[..MaxDisplayLength] : value)
        {
            if (char.IsControl(c))
            {
                builder.Append(@"\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        if (value.Length > MaxDisplayLength)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }

    private static OperationResult<IReadOnlyList<ManifestEntry>> Validate(JsonElement root)
    {
        var diagnostics = new List<Diagnostic>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail(new(DiagnosticCode.ManifestSchemaInvalid, "Manifest root must be a JSON object."));
        }

        var properties = ReadProperties(root, RootProperties, null, diagnostics);

        if (!properties.TryGetValue("formatVersion", out var version))
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "Manifest is missing 'formatVersion'."));
        }
        else if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v))
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "'formatVersion' must be an integer."));
        }
        else if (v != FormatVersions.ManifestFormat)
        {
            diagnostics.Add(new(DiagnosticCode.UnsupportedFormatVersion,
                $"Manifest formatVersion {v} is not supported; expected {FormatVersions.ManifestFormat}."));
        }

        var entries = new List<ManifestEntry>();
        if (!properties.TryGetValue("members", out var members))
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "Manifest is missing 'members'."));
        }
        else if (members.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "'members' must be an array."));
        }
        else if (members.GetArrayLength() == 0)
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "'members' must contain at least one entry."));
        }
        else
        {
            var index = 0;
            foreach (var element in members.EnumerateArray())
            {
                if (ValidateEntry(element, index, diagnostics) is { } entry)
                {
                    entries.Add(entry);
                }

                index++;
            }
        }

        var seen = new Dictionary<MemberIdentity, int>();
        foreach (var entry in entries)
        {
            if (!seen.TryAdd(entry.Identity, entry.Index))
            {
                diagnostics.Add(new(DiagnosticCode.DuplicateMemberIdentity,
                    $"Member identity {entry.Identity} is already declared by entry {seen[entry.Identity]}.",
                    entry.Index, ForDisplay(entry.RelativePath)));
            }
        }

        return diagnostics.Count == 0
            ? OperationResult<IReadOnlyList<ManifestEntry>>.Success(entries)
            : FailAll(diagnostics);
    }

    private static ManifestEntry? ValidateEntry(JsonElement element, int index, List<Diagnostic> diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "Member entry must be a JSON object.", index));
            return null;
        }

        var before = diagnostics.Count;
        var properties = ReadProperties(element, EntryProperties, index, diagnostics);

        var path = RequiredString(properties, "path", index, diagnostics);
        var library = RequiredString(properties, "library", index, diagnostics);
        var sourceFile = RequiredString(properties, "sourceFile", index, diagnostics);
        var member = RequiredString(properties, "member", index, diagnostics);
        var sourceTypeText = RequiredString(properties, "sourceType", index, diagnostics);

        var ccsid = 0;
        if (!properties.TryGetValue("ccsid", out var ccsidElement))
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "Member entry is missing 'ccsid'.", index));
        }
        else if (ccsidElement.ValueKind != JsonValueKind.Number || !ccsidElement.TryGetInt32(out ccsid))
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, "'ccsid' must be an integer.", index));
        }
        else if (ccsid != FormatVersions.Utf8Ccsid)
        {
            diagnostics.Add(new(DiagnosticCode.UnsupportedCcsid,
                $"CCSID {ccsid} is not supported; F0 accepts only {FormatVersions.Utf8Ccsid} (UTF-8).", index,
                path is null ? null : ForDisplay(path)));
        }

        if (path is not null && ValidateRelativePath(path, index) is { } pathDiagnostic)
        {
            diagnostics.Add(pathDiagnostic);
        }

        foreach (var (field, value) in new[] { ("library", library), ("sourceFile", sourceFile), ("member", member) })
        {
            if (value is not null && !MemberIdentity.IsValidName(value))
            {
                diagnostics.Add(new(DiagnosticCode.InvalidIdentityName,
                    $"'{field}' must be an IBM i system name: 1-10 characters A-Z 0-9 $ # @ _ ., starting with A-Z $ # @.", index));
            }
        }

        var sourceType = default(SourceType);
        if (sourceTypeText is not null && !SourceTypes.TryParse(sourceTypeText, out sourceType))
        {
            diagnostics.Add(new(DiagnosticCode.UnsupportedSourceType, "'sourceType' must be one of RPG, CL, DDS, SQL.", index));
        }

        return diagnostics.Count == before
            ? new ManifestEntry(index, path!, new MemberIdentity(library!, sourceFile!, member!, sourceType), ccsid)
            : null;
    }

    private static Dictionary<string, JsonElement> ReadProperties(
        JsonElement element, string[] allowed, int? index, List<Diagnostic> diagnostics)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, $"Unknown property '{ForDisplay(property.Name)}'.", index));
            }
            else if (!properties.TryAdd(property.Name, property.Value))
            {
                diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, $"Duplicate property '{property.Name}'.", index));
            }
        }

        return properties;
    }

    private static string? RequiredString(Dictionary<string, JsonElement> properties, string name, int index, List<Diagnostic> diagnostics)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, $"Member entry is missing '{name}'.", index));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new(DiagnosticCode.ManifestSchemaInvalid, $"'{name}' must be a string.", index));
            return null;
        }

        return value.GetString();
    }

    private static OperationResult<IReadOnlyList<ManifestEntry>> Fail(Diagnostic diagnostic) =>
        OperationResult<IReadOnlyList<ManifestEntry>>.Failure(diagnostic);

    private static OperationResult<IReadOnlyList<ManifestEntry>> FailAll(List<Diagnostic> diagnostics) =>
        OperationResult<IReadOnlyList<ManifestEntry>>.Failure(diagnostics);
}
