using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Preparation;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>A synthetic archive tree under a <see cref="TestEnvironment"/>, plus plan-editing helpers.</summary>
public sealed class ArchiveFixture
{
    static ArchiveFixture() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public ArchiveFixture(TestEnvironment env)
    {
        Env = env;
        Root = Path.Combine(env.Root, "archive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public TestEnvironment Env { get; }

    public string Root { get; }

    /// <summary>The platform's IBM037/IBM01140 code page, independent of IBM i Evidence Explorer's built-in table.</summary>
    public static Encoding Ebcdic(int ccsid) => Encoding.GetEncoding(ccsid, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public string Add(string relativePath, byte[] bytes)
    {
        var path = Path.Combine([Root, .. relativePath.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Concatenated 80-character source-data records (each padded with spaces), optionally BOM-prefixed UTF-8.</summary>
    public static byte[] Utf8Fixed(bool bom, params string[] records)
    {
        var text = string.Concat(records.Select(Pad));
        var body = Encoding.UTF8.GetBytes(text);
        return bom ? [.. Utf8Text.Bom, .. body] : body;
    }

    public static byte[] EbcdicFixed(int ccsid, params string[] records) => Ebcdic(ccsid).GetBytes(string.Concat(records.Select(Pad)));

    public static byte[] Utf8Lines(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

    public static string Pad(string record)
    {
        Assert.True(record.EnumerateRunes().Count() <= 80, "test record too long");
        return record + new string(' ', 80 - record.EnumerateRunes().Count());
    }

    public string NewPath(string name) => Path.Combine(Env.Root, $"{name}-{Guid.NewGuid().ToString("N")[..8]}");

    /// <summary>SHA-256 of every file under the archive, for proving the input tree is unchanged.</summary>
    public SortedDictionary<string, string> Hashes() => new(
        Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .ToDictionary(f => Path.GetRelativePath(Root, f), f => Utf8Text.Sha256Hex(File.ReadAllBytes(f))), StringComparer.Ordinal);

    public (ArchivePreparer Preparer, string Plan) Scan(ScanDefaults? defaults = null, PreparationLimits? limits = null)
    {
        var preparer = new ArchivePreparer(Env.Log, limits: limits);
        var plan = NewPath("plan") + ".json";
        var result = preparer.Scan(Root, plan, defaults ?? new ScanDefaults(null, null, null));
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        return (preparer, plan);
    }

    public static JsonObject Entry(string plan, string path) =>
        Entries(plan).Single(e => (string?)e!["path"] == path)!.AsObject();

    public static JsonArray Entries(string plan) => JsonNode.Parse(File.ReadAllText(plan))!["entries"]!.AsArray();

    /// <summary>Applies an edit to the plan's JSON and writes it back (the plan file is operator-writable).</summary>
    public static void EditPlan(string plan, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(plan))!.AsObject();
        edit(root);
        File.WriteAllText(plan, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>Records the operator's explicit choices for one entry and confirms it.</summary>
    public static void Choose(string plan, string path, int ccsid, string layout, int? width = null, bool confirmed = true) =>
        EditPlan(plan, root =>
        {
            var entry = root["entries"]!.AsArray().Single(e => (string?)e!["path"] == path)!.AsObject();
            entry["ccsid"] = ccsid;
            entry["recordLayout"] = layout;
            entry["recordWidth"] = width is null ? null : JsonValue.Create(width.Value);
            entry["confirmed"] = confirmed;
        });

    public static JsonObject Report(string output) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(output, PreparationFormats.PlanReportFileName)))!.AsObject();

    public static JsonObject ReportEntry(string output, string path) =>
        Report(output)["entries"]!.AsArray().Single(e => (string?)e!["originalPath"] == path)!.AsObject();
}
