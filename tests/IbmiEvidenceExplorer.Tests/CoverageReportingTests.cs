using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IbmiEvidenceExplorer.Cli;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1.5 criteria 4-6: exact diagnostic totals, the private syntax-gap report, refined unresolved reasons, and opt-in limits.</summary>
public sealed class CoverageReportingTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");

    private readonly IndexEnvironment _index = new();

    public void Dispose() => _index.Dispose();

    /// <summary>An export whose members carry their own library and source file.</summary>
    private string Export(params (string Library, string SourceFile, string Member, string Type, string Text)[] members)
    {
        var root = Path.Combine(_index.Env.Root, "sources", "multi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var entries = new List<string>();
        for (var i = 0; i < members.Length; i++)
        {
            var m = members[i];
            File.WriteAllText(Path.Combine(root, $"m{i}.src"), m.Text, new UTF8Encoding(false));
            entries.Add($$"""{ "path": "m{{i}}.src", "library": "{{m.Library}}", "sourceFile": "{{m.SourceFile}}", "member": "{{m.Member}}", "sourceType": "{{m.Type}}", "ccsid": 1208 }""");
        }

        TestEnvironment.WriteManifest(root, $$"""{ "formatVersion": 1, "members": [ {{string.Join(", ", entries)}} ] }""");
        return root;
    }

    private SnapshotSummary ImportAndIndex(string source, IndexLimits? limits = null, ImportLimits? importLimits = null)
    {
        var imported = _index.Env.Import("acme", source, importLimits);
        Assert.True(imported.Succeeded, string.Join("; ", imported.Diagnostics));
        var built = _index.Service(limits).Build(Acme, imported.Value.Snapshot.SnapshotId);
        Assert.True(built.Succeeded, string.Join("; ", built.Diagnostics));
        return imported.Value.Snapshot;
    }

    private (int Exit, string Out, string Log) Cli(params string[] args)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var exit = CliApp.Run(args, output, log, name => name == CliApp.StorageEnvironmentVariable ? _index.Env.StorageRoot : null);
        return (exit, output.ToString(), log.ToString());
    }

    /// <summary>120 problems of three codes: 100 unsupported commands, 15 malformed CALLs, 5 lone-CR lines.</summary>
    private static string NoisyCl()
    {
        var lines = new List<string> { "PGM" };
        for (var i = 0; i < 100; i++)
        {
            lines.Add(i % 2 == 0 ? "  MONMSG MSGID(CPF0000)" : "  CHGVAR VAR(&X) VALUE(1)");
        }

        lines.AddRange(Enumerable.Repeat("  CALL", 15));
        lines.AddRange(Enumerable.Repeat("  DCL\rVAR(&Y)", 5));
        lines.Add("ENDPGM");
        return string.Join("\n", lines) + "\n";
    }

    // ---- Criterion 4: totals beyond the bounded sample -------------------------------------------

    [Fact]
    public void TotalsByCodeEqualTheTrueCountsWhileTheSampleStaysBounded()
    {
        var parsed = SourceParser.Parse(SourceType.CL, NoisyCl().Split('\n'));
        Assert.Equal(new Dictionary<ParseDiagnosticCode, int>
        {
            [ParseDiagnosticCode.UnsupportedStatement] = 100,
            [ParseDiagnosticCode.MalformedStatement] = 15,
            [ParseDiagnosticCode.InvalidCharacter] = 5,
        }, parsed.DiagnosticTotals);
        Assert.Equal(SourceParser.MaxDiagnosticsPerMember + 1, parsed.Diagnostics.Count);
        Assert.Equal(70, parsed.SuppressedDiagnostics);
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);

        var snapshot = ImportAndIndex(Export(("LIB", "QSRC", "NOISY", "CL", NoisyCl())));
        var member = Assert.Single(_index.Load(snapshot).Members);
        Assert.Equal(parsed.DiagnosticTotals, member.DiagnosticTotals);
        Assert.Equal((120, 70), (member.DiagnosticTotal, member.SuppressedDiagnostics));

        var summary = _index.Load(snapshot).Summary;
        Assert.Equal(IndexStatus.Partial, summary.Status);
        Assert.Equal((120, 50, 70), (summary.Counts.Diagnostics, summary.Counts.DiagnosticsShown, summary.Counts.SuppressedDiagnostics));
        Assert.Equal(100, summary.DiagnosticsByCode["unsupported-statement"]);
        var cl = Assert.Single(summary.ByType);
        Assert.Equal((SourceType.CL, 1, 0, 1, 0, 120, 70), (cl.SourceType, cl.Members, cl.Complete, cl.Partial, cl.Unsupported, cl.Diagnostics, cl.SuppressedDiagnostics));
    }

    [Fact]
    public void CliTextAndJsonMakeSuppressionExplicit()
    {
        var snapshot = ImportAndIndex(Export(("LIB", "QSRC", "NOISY", "CL", NoisyCl())));
        var (exit, output, log) = Cli("index", "--workspace", "acme", "--snapshot", snapshot.SnapshotId.Value);
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("120 diagnostic(s) (50 shown, 70 suppressed)", output, StringComparison.Ordinal);
        Assert.Contains("totals by code: invalid-character 5, malformed-statement 15, unsupported-statement 100", output, StringComparison.Ordinal);
        Assert.Contains("diagnostics 120 (50 shown, 70 suppressed)", output, StringComparison.Ordinal);
        Assert.Contains("70 further diagnostic(s) were not recorded", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MONMSG", log, StringComparison.Ordinal);

        (exit, output, _) = Cli("index", "--workspace", "acme", "--snapshot", snapshot.SnapshotId.Value, "--json");
        Assert.Equal(CliApp.Ok, exit);
        var json = JsonNode.Parse(output)!;
        var member = json["members"]![0]!;
        Assert.Equal(70, (int)member["suppressedDiagnostics"]!);
        Assert.Equal(100, (int)member["diagnosticTotals"]!["unsupported-statement"]!);
        Assert.Equal(70, (int)json["index"]!["counts"]!["suppressedDiagnostics"]!);
        Assert.Equal(120, (int)json["index"]!["diagnosticsByCode"]!.AsObject().Sum(p => (int)p.Value!));
    }

    [Theory]
    [InlineData("\"unsupported-statement\": 100", "\"unsupported-statement\": 99")]
    [InlineData("\"unsupported-statement\": 100", "\"unsupported-statement\": 100, \"no-such-code\": 1")]
    [InlineData("\"unsupported-statement\": 100", "\"unsupported-statement\": 100, \"diagnostics-truncated\": 1")]
    [InlineData("\"suppressedDiagnostics\": 70", "\"suppressedDiagnostics\": 0")]
    [InlineData("\"suppressedDiagnostics\": 70", "\"suppressedDiagnostics\": -1")]
    public void RehashedIndexWithInconsistentTotalsIsRejected(string find, string replace)
    {
        var snapshot = ImportAndIndex(Export(("LIB", "QSRC", "NOISY", "CL", NoisyCl())));
        var directory = Directory.GetDirectories(Path.Combine(_index.IndexesDirectory("acme"), snapshot.SnapshotId.Value), "run-*", SearchOption.AllDirectories).Single();
        var facts = Path.Combine(directory, "facts.json");
        Access.MakeWritable(facts);
        var text = File.ReadAllText(facts);
        Assert.Contains(find, text, StringComparison.Ordinal);
        File.WriteAllText(facts, text.Replace(find, replace, StringComparison.Ordinal), new UTF8Encoding(false));
        Rehash(directory);

        var result = _index.Service().Search(Acme, snapshot.SnapshotId, "NOISY");
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>
    /// Stored totals of 5 (invalid-character), 15 (malformed-statement) and X (unsupported-statement)
    /// with S suppressed and 50 shown. Sums are checked in long, so extreme values are a typed
    /// integrity failure rather than an overflow, and a grand total beyond the int index counts is
    /// rejected even when the member is internally consistent.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue, 70, false)] // inconsistent, and its int sum would overflow
    [InlineData(int.MaxValue, int.MaxValue - 30, false)] // consistent, but 2^31 + 19 exceeds the index counts
    [InlineData(int.MaxValue - 20, int.MaxValue - 50, true)] // consistent and exactly int.MaxValue: still loads
    public void RehashedIndexWithExtremeTotalsFailsTypedOrLoads(int unsupported, int suppressed, bool loads)
    {
        var snapshot = ImportAndIndex(Export(("LIB", "QSRC", "NOISY", "CL", NoisyCl())));
        var directory = Directory.GetDirectories(Path.Combine(_index.IndexesDirectory("acme"), snapshot.SnapshotId.Value), "run-*", SearchOption.AllDirectories).Single();
        var facts = Path.Combine(directory, "facts.json");
        Access.MakeWritable(facts);
        var text = File.ReadAllText(facts);
        Assert.Contains("\"unsupported-statement\": 100", text, StringComparison.Ordinal);
        Assert.Contains("\"suppressedDiagnostics\": 70", text, StringComparison.Ordinal);
        text = text.Replace("\"unsupported-statement\": 100", $"\"unsupported-statement\": {unsupported}", StringComparison.Ordinal)
            .Replace("\"suppressedDiagnostics\": 70", $"\"suppressedDiagnostics\": {suppressed}", StringComparison.Ordinal);
        File.WriteAllText(facts, text, new UTF8Encoding(false));

        if (loads)
        {
            // The header's counts are compared with counts re-derived from the facts, so they must agree.
            // Rejected cases keep the original header: they must fail while the facts are validated.
            var header = Path.Combine(directory, "index.json");
            Access.MakeWritable(header);
            var headerText = File.ReadAllText(header);
            Assert.Contains("\"diagnostics\": 120", headerText, StringComparison.Ordinal);
            File.WriteAllText(header, headerText
                .Replace("\"diagnostics\": 120", $"\"diagnostics\": {5L + 15 + unsupported}", StringComparison.Ordinal)
                .Replace("\"suppressedDiagnostics\": 70", $"\"suppressedDiagnostics\": {suppressed}", StringComparison.Ordinal), new UTF8Encoding(false));
        }

        Rehash(directory);

        var search = _index.Service().Search(Acme, snapshot.SnapshotId, "NOISY");
        var dependencies = _index.Service().Dependencies(Acme, snapshot.SnapshotId, snapshot.Members[0].Identity);
        if (loads)
        {
            Assert.True(search.Succeeded, string.Join("; ", search.Diagnostics));
            Assert.Equal(int.MaxValue, search.Value.Index.Counts.Diagnostics);
            Assert.True(dependencies.Succeeded);
        }
        else
        {
            Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Assert.Single(search.Diagnostics).Code);
            Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Assert.Single(dependencies.Diagnostics).Code);
        }
    }

    private static void Rehash(string directory)
    {
        static void Replace(string file, string pattern, string value)
        {
            Access.MakeWritable(file);
            File.WriteAllText(file, Regex.Replace(File.ReadAllText(file), pattern, value), new UTF8Encoding(false));
        }

        Replace(Path.Combine(directory, "index.json"), "\"factsSha256\": \"[0-9a-f]+\"",
            $"\"factsSha256\": \"{Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "facts.json")))}\"");
        Replace(Path.Combine(directory, "complete.json"), "\"indexSha256\": \"[0-9a-f]+\"",
            $"\"indexSha256\": \"{Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "index.json")))}\"");
    }

    // ---- Criterion 5: syntax-gap report and unresolved reasons -----------------------------------

    private string GapCorpus() => Export(
        ("LIB", "QCLSRC", "JOBA", "CL", "PGM\n  MONMSG MSGID(CPF0000)\n  MYSECRETCMD X(1)\n  MONMSG MSGID(CPF9999)\nENDPGM\n"),
        ("LIB", "QCLSRC", "JOBB", "CL", "PGM\n  MONMSG MSGID(CPF0000)\n  CHGVAR VAR(&A) VALUE(2)\n/* MONMSG in a comment is not a gap */\nENDPGM\n"),
        ("LIB", "QDDSSRC", "ORDLF", "DDS", string.Join("\n",
            Parse.Dds('R', "ORDREC", keywords: "PFILE(ORDHDR)"),
            Parse.Dds(' ', "OHID", keywords: "REFFLD(OHID ORDHDR)"),
            Parse.Dds(' ', "OHST", "1", 'A', keywords: "TEXT('PFILE in text') COLHDG('X')"),
            Parse.Dds('K', "OHID")) + "\n"));

    [Fact]
    public void SyntaxGapReportGroupsUnsupportedVerbsAndKeywordsWithCitedExamples()
    {
        var snapshot = ImportAndIndex(GapCorpus());
        var gaps = _index.Service().SyntaxGaps(Acme, snapshot.SnapshotId).Value;
        Assert.Equal(
            [(SyntaxGapKind.ClCommand, "MONMSG", 3, 2), (SyntaxGapKind.ClCommand, "CHGVAR", 1, 1), (SyntaxGapKind.ClCommand, "MYSECRETCMD", 1, 1),
             (SyntaxGapKind.DdsKeyword, "PFILE", 1, 1), (SyntaxGapKind.DdsKeyword, "REFFLD", 1, 1)],
            gaps.Report.Groups.Select(g => (g.Kind, g.Key, g.Occurrences, g.Members)));
        foreach (var example in gaps.Report.Groups.SelectMany(g => g.Examples))
        {
            Assert.True(_index.Env.Store.Resolve(Acme, example).Succeeded);
        }

        // Unparsed behavior is not declared understood, and source-derived tokens stay out of routine logs.
        Assert.All(_index.Load(snapshot).Members, m => Assert.Equal(CoverageStatus.Partial, m.Coverage));
        var log = _index.Env.Log.AllText();
        foreach (var token in new[] { "MONMSG", "MYSECRETCMD", "CHGVAR", "PFILE", "REFFLD" })
        {
            Assert.DoesNotContain(token, log, StringComparison.Ordinal);
        }

        var (exit, output, cliLog) = Cli("gaps", "--workspace", "acme", "--snapshot", snapshot.SnapshotId.Value);
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("cl-command  MONMSG       3 occurrence(s) in 2 member(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MYSECRETCMD", cliLog, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntaxGapReportIsBoundedPerKind()
    {
        var verbs = Enumerable.Range(0, SyntaxGapReport.MaxGroupsPerKind + 5).Select(i => $"  CMD{i:D5}").ToList();
        var snapshot = ImportAndIndex(Export(("LIB", "QSRC", "MANY", "CL", "PGM\n" + string.Join("\n", verbs) + "\n  CMD00000 X(1)\nENDPGM\n")));
        var report = _index.Service().SyntaxGaps(Acme, snapshot.SnapshotId).Value.Report;
        Assert.Equal(SyntaxGapReport.MaxGroupsPerKind, report.Groups.Count);
        Assert.Equal(("CMD00000", 2), (report.Groups[0].Key, report.Groups[0].Occurrences)); // most frequent first
        Assert.Equal((5, 5), (report.OmittedGroups, report.OmittedOccurrences));
    }

    [Fact]
    public void AbsentAndOtherLibraryTargetsHaveDifferentReasonsAndNeitherResolves()
    {
        var snapshot = ImportAndIndex(Export(
            ("APPLIB", "QCLSRC", "CALLER", "CL", "PGM\n  CALL PGM(NOWHERE)\n  CALL PGM(ELSEPGM)\n  CALL PGM(LOCALPGM)\n  CALL PGM(OTHERLIB/LOCALPGM)\nENDPGM\n"),
            ("APPLIB", "QCLSRC", "LOCALPGM", "CL", "PGM\nENDPGM\n"),
            ("UTILLIB", "QCLSRC", "ELSEPGM", "CL", "PGM\nENDPGM\n"),
            ("UTILLIB", "QDDSSRC", "NOWHERE", "DDS", Parse.Dds('R', "R1") + "\n"))); // same name, wrong type: not a program
        var edges = _index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.Of("APPLIB", "QCLSRC", "CALLER", SourceType.CL)).Value.Edges;
        Assert.Equal(
            [("NOWHERE", UnresolvedReason.TargetNotInSnapshot), ("ELSEPGM", UnresolvedReason.TargetInOtherLibrary),
             ("LOCALPGM", (UnresolvedReason?)null), ("OTHERLIB/LOCALPGM", UnresolvedReason.TargetInOtherLibrary)],
            edges.Select(e => (e.Target, e.Reason)));
        Assert.Equal([null, null, Members.Of("APPLIB", "QCLSRC", "LOCALPGM", SourceType.CL), null], edges.Select(e => e.ResolvedMember));
        Assert.True(_index.Env.Store.Resolve(Acme, edges[2].DefinitionEvidence!).Succeeded);
        Assert.True(_index.Env.Store.Resolve(Acme, edges[2].ReferenceEvidence).Succeeded);
    }

    // ---- Criterion 6: limits, determinism, and versioning ----------------------------------------

    [Fact]
    public void DefaultLimitsAreTheF1ValuesAndRaisesAreBounded()
    {
        Assert.Equal(new IndexLimits(10 * ImportLimits.MiB, 100 * ImportLimits.MiB, 10_000, 100_000, 1_000), IndexLimits.Default);
        Assert.Equal(IndexLimits.Default, IndexLimits.Create().Value);
        Assert.Equal(IndexLimits.Default with { MaxTotalBytes = 512 * ImportLimits.MiB, MaxMembers = 50_000, MaxItems = 500_000 },
            IndexLimits.Create(512 * ImportLimits.MiB, 50_000, 500_000).Value);
        Assert.False(IndexLimits.Create(512 * ImportLimits.MiB + 1).Succeeded);
        Assert.False(IndexLimits.Create(maxMembers: 50_001).Succeeded);
        Assert.False(IndexLimits.Create(maxItems: 500_001).Succeeded);
        Assert.False(IndexLimits.Create(maxItems: 99_999).Succeeded); // below the F1 default is not an opt-in raise
        Assert.Equal(CliApp.Rejected, Cli("index", "--workspace", "acme", "--snapshot", "snap-" + new string('0', 32), "--max-index-bytes", "999999999999").Exit);
    }

    /// <summary>Eleven ~9.5 MiB SQL members (about 104 MiB): mostly long comment lines, then one view.</summary>
    private string LargeCorpus()
    {
        var comment = "-- " + new string('x', 65_000) + "\n";
        var members = Enumerable.Range(0, 11).Select(i =>
            ("BIGLIB", "QSQLSRC", $"V{i:D2}", "SQL", string.Concat(Enumerable.Repeat(comment, 152)) + $"CREATE VIEW VIEW{i:D2} AS SELECT A FROM BASE;\n")).ToList();
        members.Add(("BIGLIB", "QDDSSRC", "BASE", "DDS", Parse.Dds('R', "BASEREC") + "\n" + Parse.Dds(' ', "A", "1", 'A') + "\n"));
        return Export([.. members]);
    }

    [Fact]
    public void RaisedLimitAdmitsACorpusAbove100MiBWhileTheDefaultMarksItPartial()
    {
        var source = LargeCorpus();
        var importLimits = ImportLimits.Create(maxImportBytes: 128 * ImportLimits.MiB).Value;
        var imported = _index.Env.Import("acme", source, importLimits).Value.Snapshot;
        Assert.True(imported.TotalBytes > 100 * ImportLimits.MiB);

        var byDefault = _index.Service().Build(Acme, imported.SnapshotId).Value;
        Assert.Equal(IndexStatus.Partial, byDefault.Index.Status);
        Assert.Contains(byDefault.Members, m => m.Diagnostics.Any(d => d.Code == ParseDiagnosticCode.SourceLimitExceeded));

        var raised = IndexLimits.Create(maxTotalBytes: 128 * ImportLimits.MiB).Value;
        var first = _index.Service(raised).Build(Acme, imported.SnapshotId).Value.Index;
        Assert.Equal(IndexStatus.Complete, first.Status);
        Assert.Equal(raised, first.Limits);
        Assert.Equal(11, _index.Service().Search(Acme, imported.SnapshotId, "BASE").Value.TotalMatches + 10);
        Assert.Single(_index.Service().Search(Acme, imported.SnapshotId, "VIEW10").Value.Hits);

        // Deterministic apart from run metadata; another workspace sees nothing.
        var second = _index.Service(raised).Build(Acme, imported.SnapshotId).Value.Index;
        Assert.NotEqual(first.Provenance.AnalysisRunId, second.Provenance.AnalysisRunId);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_index.RunDirectory("acme", first), "facts.json")),
            File.ReadAllBytes(Path.Combine(_index.RunDirectory("acme", second), "facts.json")));
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Assert.Single(_index.Service().Search(TestEnvironment.Workspace("other"), imported.SnapshotId, "VIEW10").Diagnostics).Code);
    }

    [Fact]
    public void AnF1IndexIsNeverServedAsF15()
    {
        var snapshot = _index.Import("acme", "f1-complete");
        Assert.True(_index.Service(parserVersion: "ibmi-explorer-f1-parser/2").Build(Acme, snapshot.SnapshotId).Succeeded);
        Assert.Equal(DiagnosticCode.IndexNotFound, Assert.Single(_index.Service().Search(Acme, snapshot.SnapshotId, "CUSTMAS").Diagnostics).Code);
    }

    [Fact]
    public void StoredLimitsAboveTheCeilingsAreRejected()
    {
        var snapshot = ImportAndIndex(Export(("LIB", "QSRC", "A", "CL", "PGM\nENDPGM\n")));
        var directory = Directory.GetDirectories(Path.Combine(_index.IndexesDirectory("acme"), snapshot.SnapshotId.Value), "run-*", SearchOption.AllDirectories).Single();
        var metadata = Path.Combine(directory, "index.json");
        Access.MakeWritable(metadata);
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("\"maxItems\": 100000", "\"maxItems\": 900000", StringComparison.Ordinal), new UTF8Encoding(false));
        Rehash(directory);
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Assert.Single(_index.Service().Search(Acme, snapshot.SnapshotId, "A").Diagnostics).Code);
    }
}
