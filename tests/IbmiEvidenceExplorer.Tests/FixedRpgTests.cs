using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1.5 criterion 3: the narrow fixed-format RPG IV slice.</summary>
public sealed class FixedRpgTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");
    private static readonly string[] Decoys = ["COMMENTED", "INSTRING", "SHIFTED", "SHIFTFILE", "CUSTMASTIF", "NOTDECL", "FREEPGM", "BADEND", "COPYBOOK"];

    private readonly IndexEnvironment _index = new();

    public void Dispose() => _index.Dispose();

    private static ParsedMember Fixture(string member) =>
        SourceParser.Parse(SourceType.RPG, File.ReadAllLines(Path.Combine(TestEnvironment.FixturePath("f1-5-fixed-rpg"), "QRPGLESRC", member)));

    private static (EdgeKind, string, int, UnresolvedReason?)[] References(ParsedMember parsed) =>
        parsed.References.Select(r => (r.Kind, r.Target, r.Line, r.FixedReason)).ToArray();

    [Fact]
    public void FixtureFileDescriptionsCallAndChainYieldOnlyCitedFacts()
    {
        var parsed = Fixture("FIXEDINQ.rpgle");
        Assert.Equal([(ArtifactKind.DeclaredFile, "CUSTMAS", 3), (ArtifactKind.DeclaredFile, "CUSTRPT", 4)],
            parsed.Artifacts.Select(a => (a.Kind, a.Name, a.Line)));
        Assert.Equal(
            [(EdgeKind.UsesFile, "CUSTMAS", 3, (UnresolvedReason?)null), (EdgeKind.UsesFile, "CUSTRPT", 4, null),
             (EdgeKind.CallsProgram, "CUSTUPD", 7, null), (EdgeKind.UsesFile, "CUSTMAS", 6, null)],
            References(parsed));

        // The D-specification and SETON remain unparsed, so the member is partial, not complete.
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);
        Assert.Equal([(ParseDiagnosticCode.UnsupportedStatement, 5), (ParseDiagnosticCode.UnsupportedStatement, 9)],
            parsed.Diagnostics.Select(d => (d.Code, d.Line ?? 0)));
    }

    [Fact]
    public void DecoysYieldNoFalseFactsAndUnclearTargetsStayUnresolved()
    {
        var parsed = Fixture("DECOYS.rpgle");
        Assert.Equal(["ORDFILE", "FLATFILE"], parsed.Artifacts.Select(a => a.Name));
        Assert.Equal(
            [(EdgeKind.CallsProgram, "PGMVAR", 5, (UnresolvedReason?)UnresolvedReason.DynamicTarget),
             (EdgeKind.CallsProgram, "lowercase", 6, UnresolvedReason.InvalidTargetName),
             (EdgeKind.CallsProgram, "*LIBL/PGMX", 7, UnresolvedReason.UnsupportedQualifier),
             (EdgeKind.UsesFile, "ORDFILE", 12, UnresolvedReason.UnsupportedForm),
             (EdgeKind.UsesFile, "FLATFILE", 14, UnresolvedReason.UnsupportedForm)],
            References(parsed));
        foreach (var decoy in Decoys)
        {
            Assert.DoesNotContain(parsed.References, r => r.Target.Contains(decoy, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(parsed.Artifacts, a => a.Name.Contains(decoy, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);
    }

    [Fact]
    public void MemberWithOnlySupportedFixedFormsIsComplete()
    {
        var parsed = Parse.Lines(SourceType.RPG,
            "     H DFTACTGRP(*NO)",
            Parse.RpgF("ORDHDR"),
            Parse.RpgC("CHAIN(N)", factor1: "KEY", factor2: "ORDHDR"),
            Parse.RpgC("CALL(E)", factor2: "'LIB1/PGM1'", result: "PLIST1"),
            Parse.RpgC("PARM", result: "KEY"),
            Parse.RpgC("PARM", result: "OTHER"));
        Assert.Equal(CoverageStatus.Complete, parsed.Coverage);
        Assert.Equal([("ORDHDR", (string?)null), ("LIB1/PGM1", "LIB1"), ("ORDHDR", null)], parsed.References.Select(r => (r.Target, r.TargetLibrary)));
    }

    [Fact]
    public void ColumnsBeyond80AreCommentsAndLongerLinesAreNotParsed()
    {
        var comment = Parse.RpgC("CALL", factor2: "'REAL'").PadRight(80) + "'DECOYPGM'";
        var parsed = Parse.Lines(SourceType.RPG, comment, Parse.RpgC("CALL", factor2: "'TOOLONG'").PadRight(101, 'X'), "     C\t   CALL      'TABBED'");
        Assert.Equal(["REAL"], parsed.References.Select(r => r.Target));
        Assert.Equal([ParseDiagnosticCode.MalformedStatement, ParseDiagnosticCode.InvalidCharacter], parsed.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void FileContinuationKeywordsAreReadBeforeTheDeclarationIsEmitted()
    {
        var overridden = Parse.Lines(SourceType.RPG, Parse.RpgF("ORDHDR"), Parse.Rpg('F', (44, "RENAME(R1:R2)")), Parse.Rpg('F', (44, "EXTDESC('X')")));
        Assert.Equal(UnresolvedReason.UnsupportedForm, Assert.Single(overridden.References).FixedReason);

        var unbalanced = Parse.Lines(SourceType.RPG, Parse.RpgF("ORDHDR"), Parse.Rpg('F', (44, "EXTFILE('X'")));
        Assert.Empty(unbalanced.References);
        Assert.Empty(unbalanced.Artifacts);
        Assert.Contains(ParseDiagnosticCode.MalformedStatement, unbalanced.Diagnostics.Select(d => d.Code));
    }

    [Theory]
    [InlineData("IF", 'X', "DISK")] // format must be E or F
    [InlineData("XF", 'E', "DISK")] // type must be I, O, U, or C
    [InlineData("IF", 'E', "TAPE")] // unknown device
    [InlineData("IF", 'E', " DISK")] // device not left-adjusted
    public void MalformedFileDescriptionsYieldNothing(string type, char format, string device)
    {
        var parsed = Parse.Lines(SourceType.RPG, Parse.RpgF("ORDHDR", type, format, device: device));
        Assert.Empty(parsed.References);
        Assert.Equal([ParseDiagnosticCode.MalformedStatement], parsed.Diagnostics.Select(d => d.Code));
    }

    /// <summary>Positions 9-11 of the fixture's CALL line: the whole field is validated before any fact is taken.</summary>
    [Theory]
    [InlineData(" X ", false)] // position 10 outside any indicator
    [InlineData("  X", false)]
    [InlineData("N  ", false)] // N without an indicator
    [InlineData(" 00", false)]
    [InlineData(" 0X", false)]
    [InlineData(" KO", false)] // function keys are KA-KN and KP-KY
    [InlineData(" KZ", false)]
    [InlineData(" U9", false)]
    [InlineData(" OH", false)]
    [InlineData(" L0", false)]
    [InlineData("X01", false)] // position 9 is N or blank
    [InlineData("   ", true)]
    [InlineData(" 01", true)]
    [InlineData("N99", true)]
    [InlineData(" KA", true)]
    [InlineData(" KY", true)]
    [InlineData(" L9", true)]
    [InlineData(" LR", true)]
    [InlineData(" MR", true)]
    [InlineData(" H1", true)]
    [InlineData(" RT", true)]
    [InlineData(" U8", true)]
    [InlineData("NOA", true)]
    [InlineData(" OV", true)]
    public void CallIsTakenOnlyWithAValidConditioningField(string conditioning, bool valid)
    {
        var lines = File.ReadAllLines(Path.Combine(TestEnvironment.FixturePath("f1-5-fixed-rpg"), "QRPGLESRC", "FIXEDINQ.rpgle"));
        var call = lines[6].ToCharArray();
        Assert.Equal("CALL", new string(call, 25, 4));
        conditioning.CopyTo(0, call, 8, 3);
        lines[6] = new string(call);

        var parsed = SourceParser.Parse(SourceType.RPG, lines);
        Assert.Equal(valid, parsed.References.Any(r => r.Kind == EdgeKind.CallsProgram));
        Assert.Equal(valid, !parsed.Diagnostics.Any(d => d.Line == 7));
        if (!valid)
        {
            Assert.Equal(ParseDiagnosticCode.MalformedStatement, Assert.Single(parsed.Diagnostics, d => d.Line == 7).Code);
        }
    }

    [Fact]
    public void InvalidConditioningInTheFixtureYieldsNoCallEdgeAfterIndexing()
    {
        var copy = Path.Combine(_index.Env.Root, "sources", "fixed-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var file in Directory.EnumerateFiles(TestEnvironment.FixturePath("f1-5-fixed-rpg"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(TestEnvironment.FixturePath("f1-5-fixed-rpg"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var program = Path.Combine(copy, "QRPGLESRC", "FIXEDINQ.rpgle");
        var text = File.ReadAllText(program);
        const string call = "     C                   CALL      'CUSTUPD'";
        Assert.Contains(call, text, StringComparison.Ordinal);
        File.WriteAllText(program, text.Replace(call, "     C    X              CALL      'CUSTUPD'", StringComparison.Ordinal));

        var imported = _index.Env.Import("acme", copy);
        Assert.True(imported.Succeeded, string.Join("; ", imported.Diagnostics));
        var snapshot = imported.Value.Snapshot;
        _index.BuildIndex("acme", snapshot.SnapshotId);
        var result = _index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.Of("FIXLIB", "QRPGLESRC", "FIXEDINQ", SourceType.RPG)).Value;
        Assert.DoesNotContain(result.Edges, e => e.Kind == EdgeKind.CallsProgram);
        Assert.Equal(CoverageStatus.Partial, result.Coverage);
    }

    [Fact]
    public void ParmOutsideACallAndOtherOperationsAreUnsupported()
    {
        var parsed = Parse.Lines(SourceType.RPG, Parse.RpgC("PARM", result: "X"), Parse.RpgC("CALL", factor2: "'P1'"),
            Parse.RpgC("EVAL", factor2: "X = 1"), Parse.RpgC("PARM", result: "Y"));
        Assert.Equal(["P1"], parsed.References.Select(r => r.Target));
        Assert.Equal(3, parsed.Diagnostics.Count(d => d.Code == ParseDiagnosticCode.UnsupportedStatement));
    }

    [Fact]
    public void FixtureIndexesWithResolvedEdgesAndResolvableEvidence()
    {
        var snapshot = _index.Import("acme", "f1-5-fixed-rpg");
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Equal(IndexStatus.Partial, summary.Status);
        var service = _index.Service();
        var edges = service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("FIXLIB", "QRPGLESRC", "FIXEDINQ", SourceType.RPG)).Value.Edges;
        Assert.Equal(
            [("CUSTMAS", 3, "FIXLIB/QDDSSRC/CUSTMAS (DDS)"), ("CUSTRPT", 4, "FIXLIB/QDDSSRC/CUSTRPT (DDS)"),
             ("CUSTMAS", 6, "FIXLIB/QDDSSRC/CUSTMAS (DDS)"), ("CUSTUPD", 7, "FIXLIB/QCLSRC/CUSTUPD (CL)")],
            edges.Select(e => (e.Target, e.ReferenceEvidence.LineNumber, e.ResolvedMember?.ToString())));
        foreach (var edge in edges)
        {
            Assert.True(_index.Env.Store.Resolve(Acme, edge.ReferenceEvidence).Succeeded);
        }

        var decoys = service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("FIXLIB", "QRPGLESRC", "DECOYS", SourceType.RPG)).Value;
        Assert.All(decoys.Edges, e => Assert.Equal(ResolutionStatus.Unresolved, e.Resolution));
        foreach (var decoy in Decoys)
        {
            Assert.Empty(service.Search(Acme, snapshot.SnapshotId, decoy).Value.Hits);
        }

        Canaries.AssertAbsent(_index.Env.Log.AllText());
    }
}
