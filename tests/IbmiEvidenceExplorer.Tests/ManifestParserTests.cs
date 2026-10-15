using System.Text;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

public class ManifestParserTests
{
    private const string Entry = """{ "path": "QCLSRC/A.clle", "library": "LIB", "sourceFile": "QCLSRC", "member": "A", "sourceType": "CL", "ccsid": 1208 }""";

    private static OperationResult<IReadOnlyList<ManifestEntry>> Parse(string json) => ManifestParser.Parse(Encoding.UTF8.GetBytes(json));

    private static string Manifest(params string[] entries) => $$"""{ "formatVersion": 1, "members": [ {{string.Join(",", entries)}} ] }""";

    /// <summary>Builds the standard entry with one field's raw JSON value replaced.</summary>
    private static string EntryWith(string field, string rawJsonValue)
    {
        var fields = new Dictionary<string, string>
        {
            ["path"] = "\"QCLSRC/A.clle\"",
            ["library"] = "\"LIB\"",
            ["sourceFile"] = "\"QCLSRC\"",
            ["member"] = "\"A\"",
            ["sourceType"] = "\"CL\"",
            ["ccsid"] = "1208",
        };
        fields[field] = rawJsonValue;
        return "{ " + string.Join(", ", fields.Select(f => $"\"{f.Key}\": {f.Value}")) + " }";
    }

    private static void AssertCode(OperationResult<IReadOnlyList<ManifestEntry>> result, DiagnosticCode code)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void ParsesValidManifest()
    {
        var result = Parse(Manifest(Entry));

        Assert.True(result.Succeeded);
        var entry = Assert.Single(result.Value);
        Assert.Equal(new MemberIdentity("LIB", "QCLSRC", "A", SourceType.CL), entry.Identity);
        Assert.Equal("QCLSRC/A.clle", entry.RelativePath);
        Assert.Equal(1208, entry.Ccsid);
    }

    [Fact]
    public void AcceptsUtf8ByteOrderMark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Manifest(Entry))).ToArray();
        Assert.True(ManifestParser.Parse(bytes).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{ \"formatVersion\": 1, \"members\": [], }")]
    [InlineData("{ // comment\n \"formatVersion\": 1 }")]
    public void RejectsMalformedJson(string json)
    {
        var result = Parse(json);
        Assert.False(result.Succeeded);
        Assert.All(result.Diagnostics, d => Assert.True(d.Code is DiagnosticCode.ManifestMalformed or DiagnosticCode.ManifestSchemaInvalid));
    }

    [Fact]
    public void MalformedJsonDiagnosticDoesNotEchoContent()
    {
        var result = Parse("{ \"formatVersion\": FIXTURE-CANARY-JSON }");
        AssertCode(result, DiagnosticCode.ManifestMalformed);
        Canaries.AssertAbsent(string.Join("\n", result.Diagnostics));
    }

    [Fact]
    public void RejectsInvalidUtf8Manifest()
    {
        var bytes = Encoding.UTF8.GetBytes(Manifest(Entry));
        bytes[10] = 0xFF;
        AssertCode(ManifestParser.Parse(bytes), DiagnosticCode.ManifestMalformed);
    }

    [Theory]
    [InlineData("{ \"members\": [] }")]
    [InlineData("{ \"formatVersion\": 1 }")]
    [InlineData("{ \"formatVersion\": \"1\", \"members\": [] }")]
    [InlineData("{ \"formatVersion\": 1, \"members\": {} }")]
    [InlineData("{ \"formatVersion\": 1, \"members\": [] }")]
    [InlineData("{ \"formatVersion\": 1, \"members\": [ 42 ] }")]
    [InlineData("{ \"formatVersion\": 1, \"members\": [], \"extra\": true }")]
    [InlineData("{ \"formatVersion\": 1, \"formatVersion\": 1, \"members\": [] }")]
    public void RejectsInvalidRootSchema(string json) => AssertCode(Parse(json), DiagnosticCode.ManifestSchemaInvalid);

    [Fact]
    public void RejectsUnsupportedFormatVersion() =>
        AssertCode(Parse(Manifest(Entry).Replace("\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal)),
            DiagnosticCode.UnsupportedFormatVersion);

    [Theory]
    [InlineData("path")]
    [InlineData("library")]
    [InlineData("sourceFile")]
    [InlineData("member")]
    [InlineData("sourceType")]
    [InlineData("ccsid")]
    public void RejectsMissingEntryField(string field)
    {
        var entry = Entry.Replace($"\"{field}\":", $"\"x{field}\":", StringComparison.Ordinal);
        var result = Parse(Manifest(entry));
        AssertCode(result, DiagnosticCode.ManifestSchemaInvalid);
        Assert.Contains(result.Diagnostics, d => d.EntryIndex == 0);
    }

    [Theory]
    [InlineData("library", "7")]
    [InlineData("ccsid", "\"1208\"")]
    [InlineData("ccsid", "1208.5")]
    [InlineData("path", "null")]
    public void RejectsWrongFieldTypes(string field, string value)
    {
        var result = Parse(Manifest(EntryWith(field, value)));
        AssertCode(result, DiagnosticCode.ManifestSchemaInvalid);
        Assert.Single(result.Diagnostics);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(819)]
    [InlineData(0)]
    public void RejectsUnsupportedCcsid(int ccsid) =>
        AssertCode(Parse(Manifest(Entry.Replace("1208", ccsid.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))),
            DiagnosticCode.UnsupportedCcsid);

    [Theory]
    [InlineData("rpg")]
    [InlineData("RPGLE")]
    [InlineData("")]
    public void RejectsUnsupportedSourceType(string type) =>
        AssertCode(Parse(Manifest(Entry.Replace("\"CL\"", $"\"{type}\"", StringComparison.Ordinal))), DiagnosticCode.UnsupportedSourceType);

    [Theory]
    [InlineData("lib")]
    [InlineData("1LIB")]
    [InlineData("_LIB")]
    [InlineData("TOOLONGNAME")]
    [InlineData("")]
    [InlineData("LI B")]
    [InlineData("LIB/X")]
    public void RejectsInvalidIdentityNames(string name) =>
        AssertCode(Parse(Manifest(Entry.Replace("\"LIB\"", $"\"{name}\"", StringComparison.Ordinal))), DiagnosticCode.InvalidIdentityName);

    [Theory]
    [InlineData("$LIB")]
    [InlineData("#A@B_C.D9")]
    [InlineData("ABCDEFGHIJ")]
    public void AcceptsIbmISystemNames(string name) =>
        Assert.True(Parse(Manifest(Entry.Replace("\"LIB\"", $"\"{name}\"", StringComparison.Ordinal))).Succeeded);

    [Fact]
    public void RejectsDuplicateIdentityAndNamesBothEntries()
    {
        var result = Parse(Manifest(Entry, Entry.Replace("A.clle", "B.clle", StringComparison.Ordinal)));
        AssertCode(result, DiagnosticCode.DuplicateMemberIdentity);
        var duplicate = Assert.Single(result.Diagnostics);
        Assert.Equal(1, duplicate.EntryIndex);
        Assert.Contains("entry 0", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameMemberWithDifferentSourceTypeIsADistinctIdentity()
    {
        var result = Parse(Manifest(Entry, Entry.Replace("\"CL\"", "\"SQL\"", StringComparison.Ordinal)));
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value.Count);
    }

    [Theory]
    [InlineData("../A.clle", DiagnosticCode.PathTraversal)]
    [InlineData("QCLSRC/../../A.clle", DiagnosticCode.PathTraversal)]
    [InlineData("/etc/passwd", DiagnosticCode.PathTraversal)]
    [InlineData("QCLSRC//A.clle", DiagnosticCode.InvalidPath)]
    [InlineData("./A.clle", DiagnosticCode.InvalidPath)]
    [InlineData("QCLSRC/", DiagnosticCode.InvalidPath)]
    [InlineData("QCLSRC\\\\A.clle", DiagnosticCode.InvalidPath)]
    [InlineData("C:A.clle", DiagnosticCode.InvalidPath)]
    [InlineData("A\\u0000.clle", DiagnosticCode.InvalidPath)]
    [InlineData("", DiagnosticCode.InvalidPath)]
    public void RejectsUnsafePaths(string path, DiagnosticCode code) =>
        AssertCode(Parse(Manifest(Entry.Replace("QCLSRC/A.clle", path, StringComparison.Ordinal))), code);

    [Fact]
    public void ReportsEveryProblemNotJustTheFirst()
    {
        var bad1 = Entry.Replace("1208", "37", StringComparison.Ordinal);
        var bad2 = Entry.Replace("\"LIB\"", "\"lib\"", StringComparison.Ordinal).Replace("QCLSRC/A.clle", "../x", StringComparison.Ordinal);
        var result = Parse(Manifest(bad1, bad2));
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCode.UnsupportedCcsid && d.EntryIndex == 0);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCode.InvalidIdentityName && d.EntryIndex == 1);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCode.PathTraversal && d.EntryIndex == 1);
    }

    [Fact]
    public void EntryBuilderMatchesStandardEntry() => Assert.True(Parse(Manifest(EntryWith("ccsid", "1208"))).Succeeded);
}
