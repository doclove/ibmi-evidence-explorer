using System.Security.Cryptography;
using System.Text;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1 acceptance criteria 1-4 and 6 over the synthetic fixtures.</summary>
public sealed class IndexAcceptanceTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");

    private readonly IndexEnvironment _index = new();

    public void Dispose() => _index.Dispose();

    private void AssertResolves(EvidenceReference? reference, WorkspaceId workspace)
    {
        Assert.NotNull(reference);
        Assert.Equal(FormatVersions.ParserVersion, reference.ParserVersion); // F0 references stay unchanged
        var resolved = _index.Env.Store.Resolve(workspace, reference);
        Assert.True(resolved.Succeeded, string.Join("; ", resolved.Diagnostics));
    }

    // ---- Criterion 1 ----------------------------------------------------------------------------

    [Fact]
    public void FixtureIsIndexedAndExactNamesAreFoundWithResolvableEvidence()
    {
        var snapshot = _index.Import("acme", "valid-export");
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Equal(4, summary.Counts.Members);
        Assert.Equal(IndexFormats.ParserVersion, summary.Provenance.ParserVersion);
        Assert.Equal(snapshot.SnapshotId, summary.Provenance.SnapshotId);
        Assert.NotEqual(snapshot.AnalysisRunId, summary.Provenance.AnalysisRunId);

        var service = _index.Service();
        var expected = new (string Query, ArtifactKind Kind, MemberIdentity Member, int Line)[]
        {
            ("ORDINQ", ArtifactKind.Member, Members.OrdInq, 1),
            ("ORDBATCH", ArtifactKind.Member, Members.OrdBatch, 2),
            ("ORDHDR", ArtifactKind.Member, Members.OrdHdr, 2),
            ("OHSTAT", ArtifactKind.Field, Members.OrdHdr, 4),
            ("OPENORD", ArtifactKind.View, Members.OrdView, 2),
            ("ohstat", ArtifactKind.Field, Members.OrdHdr, 4), // case-insensitive
        };
        foreach (var (query, kind, member, line) in expected)
        {
            var result = service.Search(Acme, snapshot.SnapshotId, query);
            Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
            var hit = Assert.Single(result.Value.Hits);
            Assert.Equal((kind, member, line), (hit.Kind, hit.Member, hit.Evidence!.LineNumber));
            Assert.Equal(summary.Provenance, hit.Provenance);
            Assert.False(result.Value.Truncated);
            AssertResolves(hit.Evidence, Acme);
        }

        Assert.Empty(service.Search(Acme, snapshot.SnapshotId, "ORDHDRX").Value.Hits);
        Assert.Empty(service.Search(Acme, snapshot.SnapshotId, "ORDH").Value.Hits); // exact, not prefix
    }

    [Fact]
    public void EveryStoredDefinitionReferenceAndDiagnosticResolvesThroughF0()
    {
        foreach (var fixture in new[] { "valid-export", "f1-complete", "f1-edge-cases" })
        {
            var snapshot = _index.Import("acme", fixture);
            _index.BuildIndex("acme", snapshot.SnapshotId);
            var index = _index.Load(snapshot);
            foreach (var artifact in index.Artifacts)
            {
                AssertResolves(artifact.Evidence, Acme);
            }

            foreach (var edge in index.Edges)
            {
                AssertResolves(edge.ReferenceEvidence, Acme);
                if (edge.DefinitionEvidence is not null)
                {
                    AssertResolves(edge.DefinitionEvidence, Acme);
                }
            }

            foreach (var diagnostic in index.Members.SelectMany(m => m.Diagnostics).Where(d => d.Evidence is not null))
            {
                AssertResolves(diagnostic.Evidence, Acme);
            }
        }
    }

    // ---- Criterion 2 ----------------------------------------------------------------------------

    [Fact]
    public void FixtureGraphHasTheFourCrossLanguageEdgesWithLinesAndResolution()
    {
        var snapshot = _index.Import("acme", "valid-export");
        _index.BuildIndex("acme", snapshot.SnapshotId);
        var service = _index.Service();
        DependencyEdge Single(MemberIdentity member) => Assert.Single(service.Dependencies(Acme, snapshot.SnapshotId, member).Value.Edges);

        var call = Single(Members.OrdBatch);
        Assert.Equal((EdgeKind.CallsProgram, 4, "DEMOLIB/ORDINQ", ResolutionStatus.Resolved, Members.OrdInq, false),
            (call.Kind, call.ReferenceEvidence.LineNumber, call.Target, call.Resolution, call.ResolvedMember, call.AssumedCallerLibrary));

        var rpg = service.Dependencies(Acme, snapshot.SnapshotId, Members.OrdInq).Value.Edges;
        Assert.Equal([(EdgeKind.UsesFile, 5), (EdgeKind.UsesFile, 11)], rpg.Select(e => (e.Kind, e.ReferenceEvidence.LineNumber)));
        Assert.All(rpg, e => Assert.Equal((ResolutionStatus.Resolved, Members.OrdHdr, true), (e.Resolution, e.ResolvedMember, e.AssumedCallerLibrary)));
        Assert.All(rpg, e => Assert.Equal(2, e.DefinitionEvidence!.LineNumber));

        var view = Single(Members.OrdView);
        Assert.Equal((EdgeKind.SelectsFrom, 4, ArtifactKind.View, "OPENORD", ResolutionStatus.Resolved, Members.OrdHdr),
            (view.Kind, view.ReferenceEvidence.LineNumber, view.SourceKind, view.SourceName, view.Resolution, view.ResolvedMember));

        var key = Single(Members.OrdHdr);
        Assert.Equal((EdgeKind.KeysField, 6, "ORDHDRR", "OHORID", ResolutionStatus.Resolved, 3),
            (key.Kind, key.ReferenceEvidence.LineNumber, key.SourceName, key.Target, key.Resolution, key.DefinitionEvidence!.LineNumber));

        // Determinism of the direct dependency output across calls and across index runs.
        var first = service.Dependencies(Acme, snapshot.SnapshotId, Members.OrdInq).Value.Edges;
        _index.BuildIndex("acme", snapshot.SnapshotId);
        var second = service.Dependencies(Acme, snapshot.SnapshotId, Members.OrdInq).Value.Edges;
        Assert.Equal(first.Select(e => e with { Provenance = null! }), second.Select(e => e with { Provenance = null! }));
    }

    [Fact]
    public void SupportedSubsetFixtureProducesACompleteIndex()
    {
        var snapshot = _index.Import("acme", "f1-complete");
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Equal(IndexStatus.Complete, summary.Status);
        Assert.Equal((4, 4, 0, 5, 0, 0), (summary.Counts.Members, summary.Counts.CompleteMembers, summary.Counts.PartialMembers,
            summary.Counts.ResolvedEdges, summary.Counts.UnresolvedEdges, summary.Counts.Diagnostics));

        var search = _index.Service().Search(Acme, snapshot.SnapshotId, "&custid");
        Assert.Equal(ArtifactKind.Variable, Assert.Single(search.Value.Hits).Kind);
    }

    [Fact]
    public void OriginalValidExportIsPartialBecauseItHasStatementsOutsideTheSubset()
    {
        var snapshot = _index.Import("acme", "valid-export");
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Equal(IndexStatus.Partial, summary.Status);
        var coverage = _index.Load(snapshot).Members.ToDictionary(m => m.Member.Identity, m => m.Coverage);
        Assert.Equal(CoverageStatus.Partial, coverage[Members.OrdInq]); // if / dsply / endif / *inlr
        Assert.Equal(CoverageStatus.Partial, coverage[Members.OrdBatch]); // MONMSG / RETURN / label / SNDPGMMSG
        Assert.Equal(CoverageStatus.Complete, coverage[Members.OrdHdr]);
        Assert.Equal(CoverageStatus.Complete, coverage[Members.OrdView]);
    }

    // ---- Criteria 3 and 4 -----------------------------------------------------------------------

    [Fact]
    public void EdgeCasesStayUnresolvedWithReasonsAndOtherMembersRemainQueryable()
    {
        var snapshot = _index.Import("acme", "f1-edge-cases");
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Equal(IndexStatus.Partial, summary.Status);
        var service = _index.Service();
        IReadOnlyList<DependencyEdge> Edges(string file, string member, SourceType type) =>
            service.Dependencies(Acme, snapshot.SnapshotId, Members.Edge(file, member, type)).Value.Edges;

        var calls = Edges("QCLSRC", "CALLS", SourceType.CL);
        Assert.Equal(
            [("&PGMNAME", UnresolvedReason.DynamicTarget), ("NOSUCHPGM", UnresolvedReason.TargetNotInSnapshot), ("DUPPGM", UnresolvedReason.AmbiguousTarget),
             ("*LIBL/CYCLEA", UnresolvedReason.UnsupportedQualifier), ("OTHERLIB/CYCLEA", UnresolvedReason.TargetInOtherLibrary), ("CYCLEA", (UnresolvedReason?)null)],
            calls.Select(e => (e.Target, e.Reason)));
        Assert.Equal(2, calls[2].CandidateCount);
        Assert.Equal(Members.Edge("QCLSRC", "CYCLEA", SourceType.CL), calls[5].ResolvedMember);
        Assert.Equal(11, calls[5].ReferenceEvidence.LineNumber); // continued CALL on mixed CRLF/LF lines

        var rpg = Edges("QRPGLESRC", "EDGERPG", SourceType.RPG);
        Assert.Equal(
            [("NOSUCHFILE", UnresolvedReason.TargetNotInSnapshot), ("DUPFILE", UnresolvedReason.AmbiguousTarget), ("ORDFILE", UnresolvedReason.UnsupportedForm),
             ("UNDECLARED", UnresolvedReason.NotDeclaredInMember), ("DUPFILE", UnresolvedReason.AmbiguousTarget)],
            rpg.Select(e => (e.Target, e.Reason!.Value)));

        var keys = Edges("QDDSSRC", "EDGEPF", SourceType.DDS);
        Assert.Equal([ResolutionStatus.Resolved, ResolutionStatus.Unresolved], keys.Select(k => k.Resolution));
        Assert.Equal(UnresolvedReason.TargetNotFound, keys[1].Reason);

        Assert.Empty(Edges("QSQLSRC", "JOINVW", SourceType.SQL));
        Assert.Empty(Edges("QSQLSRC", "QUOTED", SourceType.SQL));
        Assert.Empty(Edges("QSQLSRC", "LONECR", SourceType.SQL));
        // F1.5 parses the narrow fixed-form slice: an externally described DISK file and a CHAIN of it.
        Assert.Equal([ResolutionStatus.Resolved, ResolutionStatus.Resolved], Edges("QRPGLESRC", "FIXED", SourceType.RPG).Select(e => e.Resolution));
        Assert.Empty(Edges("QCLSRC", "LONECR", SourceType.CL));
        Assert.Equal(ResolutionStatus.Resolved, Assert.Single(Edges("QSQLSRC", "STRVW", SourceType.SQL)).Resolution);

        var index = _index.Load(snapshot);
        var coverage = index.Members.ToDictionary(m => m.Member.Identity.ToString(), m => m.Coverage);
        Assert.Equal(CoverageStatus.Complete, coverage["EDGELIB/QRPGLESRC/FIXED (RPG)"]);
        Assert.Equal(CoverageStatus.Unsupported, coverage["EDGELIB/QSQLSRC/QUOTED (SQL)"]);
        Assert.Equal(CoverageStatus.Unsupported, coverage["EDGELIB/QSQLSRC/MALFORM (SQL)"]);
        Assert.Equal(CoverageStatus.Partial, coverage["EDGELIB/QCLSRC/CALLS (CL)"]);
        Assert.Equal(CoverageStatus.Partial, coverage["EDGELIB/QSQLSRC/JOINVW (SQL)"]);
        Assert.Equal(CoverageStatus.Complete, coverage["EDGELIB/QSQLSRC/STRVW (SQL)"]);
        Assert.All(index.Edges.Where(e => e.Resolution == ResolutionStatus.Resolved), e => Assert.Equal(1, e.CandidateCount));
        Assert.All(index.Edges.Where(e => e.Resolution == ResolutionStatus.Unresolved), e => Assert.Null(e.ResolvedMember));
    }

    [Fact]
    public void CommentsStringsAndLoneCarriageReturnsEmitNoFalseSymbolsOrEdges()
    {
        var snapshot = _index.Import("acme", "f1-edge-cases");
        _index.BuildIndex("acme", snapshot.SnapshotId);
        var index = _index.Load(snapshot);
        string[] decoys = ["COMMENTED", "INSTRING", "INMSG", "FAKEVW", "FAKE2", "BLOCKED", "HIDDENT", "CRHIDDEN", "CRVW", "QUOTEDVW", "JOIN"];
        foreach (var decoy in decoys)
        {
            Assert.DoesNotContain(index.Artifacts, a => a.Key == decoy);
            Assert.DoesNotContain(index.Edges, e => e.Target.Contains(decoy, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(_index.Service().Search(Acme, snapshot.SnapshotId, decoy).Value.Hits);
        }
    }

    [Fact]
    public void DecoyViewInsideANestedSqlCommentIsNeitherSearchableNorAnEdge()
    {
        var sql = "/* outer\n   /* inner */\n   CREATE VIEW FAKEVW AS SELECT A FROM ORDHDR;\n*/\nCREATE VIEW REALVW AS SELECT A FROM ORDHDR;\n";
        var dds = Parse.Dds('R', "ORDREC") + "\n" + Parse.Dds(' ', "A", "1", 'A') + "\n";
        var export = _index.Env.CreateExport(("QSRC/VIEWS.sql", "VIEWS", "SQL", Encoding.UTF8.GetBytes(sql)), ("QSRC/ORDHDR.pf", "ORDHDR", "DDS", Encoding.UTF8.GetBytes(dds)));
        var snapshot = _index.Env.Import("acme", export).Value.Snapshot;
        Assert.Equal(IndexStatus.Complete, _index.BuildIndex("acme", snapshot.SnapshotId).Status);
        var service = _index.Service();

        Assert.Empty(service.Search(Acme, snapshot.SnapshotId, "FAKEVW").Value.Hits);
        var real = Assert.Single(service.Search(Acme, snapshot.SnapshotId, "REALVW").Value.Hits);
        Assert.Equal(5, real.Evidence.LineNumber);
        var edge = Assert.Single(service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("TESTLIB", "QSRC", "VIEWS", SourceType.SQL)).Value.Edges);
        Assert.Equal(("REALVW", "ORDHDR", ResolutionStatus.Resolved, 5), (edge.SourceName, edge.Target, edge.Resolution, edge.ReferenceEvidence.LineNumber));
        Assert.DoesNotContain(_index.Load(snapshot).Artifacts, a => a.Key == "FAKEVW");
    }

    [Fact]
    public void SqlFixtureWithoutItsFinalSemicolonIsPartialWithNoViewOrEdge()
    {
        var source = _index.Env.CopyFixture("f1-complete");
        var view = Path.Combine(source, "QSQLSRC", "CUSTVW.sql");
        File.WriteAllText(view, File.ReadAllText(view).Replace("FROM CUSTMAS;", "FROM CUSTMAS", StringComparison.Ordinal), new UTF8Encoding(false));
        var snapshot = _index.Env.Import("acme", source).Value.Snapshot;
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        var service = _index.Service();

        Assert.Equal(IndexStatus.Partial, summary.Status);
        var member = Members.Of("DEMOLIB", "QSQLSRC", "CUSTVW", SourceType.SQL);
        var indexed = _index.Load(snapshot).Members.Single(m => m.Member.Identity == member);
        Assert.Equal(CoverageStatus.Unsupported, indexed.Coverage);
        var diagnostic = Assert.Single(indexed.Diagnostics);
        Assert.Equal((ParseDiagnosticCode.MissingTerminator, 2), (diagnostic.Code, diagnostic.Evidence!.LineNumber));
        Assert.Empty(service.Search(Acme, snapshot.SnapshotId, "CUSTLIST").Value.Hits);
        var dependencies = service.Dependencies(Acme, snapshot.SnapshotId, member).Value;
        Assert.Equal((CoverageStatus.Unsupported, 0), (dependencies.Coverage, dependencies.TotalEdges));
    }

    [Fact]
    public void EmptyMemberIsListedButIsNotACompleteOrSearchableDefinition()
    {
        var export = _index.Env.CreateExport(
            ("QSRC/EMPTY.rpgle", "EMPTY", "RPG", []),
            ("QSRC/CALLER.clle", "CALLER", "CL", "PGM\nCALL PGM(EMPTY)\nENDPGM\n"u8.ToArray()));
        var snapshot = _index.Env.Import("acme", export).Value.Snapshot;
        var summary = _index.BuildIndex("acme", snapshot.SnapshotId);
        var service = _index.Service();
        var empty = Members.Of("TESTLIB", "QSRC", "EMPTY", SourceType.RPG);

        Assert.Equal(IndexStatus.Partial, summary.Status);
        Assert.Equal((2, 1, 1), (summary.Counts.Members, summary.Counts.CompleteMembers, summary.Counts.UnsupportedMembers));
        var indexed = _index.Load(snapshot).Members.Single(m => m.Member.Identity == empty);
        Assert.Equal(CoverageStatus.Unsupported, indexed.Coverage);
        var diagnostic = Assert.Single(indexed.Diagnostics);
        Assert.Equal((ParseDiagnosticCode.NoStatements, (EvidenceReference?)null), (diagnostic.Code, diagnostic.Evidence));

        // No evidence-free search hit; every hit that remains carries evidence.
        Assert.Empty(service.Search(Acme, snapshot.SnapshotId, "EMPTY").Value.Hits);
        Assert.All(_index.Load(snapshot).Artifacts, a => AssertResolves(a.Evidence, Acme));

        var dependencies = service.Dependencies(Acme, snapshot.SnapshotId, empty).Value;
        Assert.Equal((CoverageStatus.Unsupported, 0), (dependencies.Coverage, dependencies.TotalEdges));

        // The member exists in the snapshot, so a literal call to it resolves, but no definition is cited.
        var call = Assert.Single(service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("TESTLIB", "QSRC", "CALLER", SourceType.CL)).Value.Edges);
        Assert.Equal((ResolutionStatus.Resolved, empty, (EvidenceReference?)null), (call.Resolution, call.ResolvedMember, call.DefinitionEvidence));
    }

    [Fact]
    public void CallCycleTerminatesAndIsReportedAsTwoDirectEdges()
    {
        var snapshot = _index.Import("acme", "f1-edge-cases");
        _index.BuildIndex("acme", snapshot.SnapshotId);
        var service = _index.Service();
        var a = Members.Edge("QCLSRC", "CYCLEA", SourceType.CL);
        var b = Members.Edge("QCLSRC", "CYCLEB", SourceType.CL);
        Assert.Equal(b, Assert.Single(service.Dependencies(Acme, snapshot.SnapshotId, a).Value.Edges).ResolvedMember);
        Assert.Equal(a, Assert.Single(service.Dependencies(Acme, snapshot.SnapshotId, b).Value.Edges).ResolvedMember);
    }

    // ---- Criterion 6 (determinism, staleness) and F0 immutability --------------------------------

    [Fact]
    public void ReindexingUnchangedInputIsDeterministicExceptRunMetadata()
    {
        var snapshot = _index.Import("acme", "f1-edge-cases");
        var first = _index.BuildIndex("acme", snapshot.SnapshotId);
        var second = _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.NotEqual(first.Provenance.AnalysisRunId, second.Provenance.AnalysisRunId);
        Assert.Equal(first.Counts, second.Counts);

        var factsA = File.ReadAllBytes(Path.Combine(_index.RunDirectory("acme", first), "facts.json"));
        var factsB = File.ReadAllBytes(Path.Combine(_index.RunDirectory("acme", second), "facts.json"));
        Assert.Equal(factsA, factsB);
        Assert.Equal(second.Provenance.AnalysisRunId, _index.Load(snapshot).Provenance.AnalysisRunId); // latest run is served
    }

    [Fact]
    public void IndexingLeavesTheF0SnapshotByteForByteUnchanged()
    {
        var snapshot = _index.Import("acme", "valid-export");
        var directory = Path.Combine(_index.Env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value);
        Dictionary<string, string> Hashes() => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(directory, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var before = Hashes();

        _index.BuildIndex("acme", snapshot.SnapshotId);
        _index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR");

        Assert.Equal(before, Hashes());
        Assert.Equal(snapshot, Assert.Single(_index.Env.Store.List(Acme).Value.Snapshots), new SnapshotComparer());
        Assert.Empty(Directory.EnumerateFiles(_index.RunDirectory("acme", _index.BuildIndex("acme", snapshot.SnapshotId)), "*.src"));
    }

    [Fact]
    public void ChangedSourceNeedsANewSnapshotAndIndex()
    {
        var source = _index.Env.CopyFixture("valid-export");
        var first = _index.Env.Import("acme", source).Value.Snapshot;
        _index.BuildIndex("acme", first.SnapshotId);

        File.AppendAllText(Path.Combine(source, "QSQLSRC", "ORDVIEW.sql"), "-- changed\n");
        var second = _index.Env.Import("acme", source).Value.Snapshot;
        var result = _index.Service().Search(Acme, second.SnapshotId, "OPENORD");
        Assert.Equal(DiagnosticCode.IndexNotFound, Assert.Single(result.Diagnostics).Code);
    }

    private sealed class SnapshotComparer : IEqualityComparer<SnapshotSummary>
    {
        public bool Equals(SnapshotSummary? x, SnapshotSummary? y) =>
            x!.SnapshotId == y!.SnapshotId && x.Members.SequenceEqual(y.Members) && x.AnalysisRunId == y.AnalysisRunId;

        public int GetHashCode(SnapshotSummary obj) => obj.SnapshotId.GetHashCode();
    }
}
