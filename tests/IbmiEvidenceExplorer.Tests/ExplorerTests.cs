using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F2 criteria 1 and 2: the deterministic, provider-free exact-member dossier.</summary>
public sealed class ExplorerTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");

    private readonly ExplorerEnvironment _f2 = new();

    public void Dispose() => _f2.Dispose();

    private static DiagnosticCode Code<T>(OperationResult<T> result) => Assert.Single(result.Diagnostics).Code;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly string[] EvidenceFields = ["line", "byteOffset", "byteLength", "lineSha256"];

    private static string Serialize(MemberDossier dossier) => JsonSerializer.Serialize(dossier, Indented);

    /// <summary>Every displayed citation resolves through F0 in the same workspace and snapshot to the shown text.</summary>
    private void AssertCitationsResolve(MemberDossier dossier)
    {
        foreach (var line in dossier.Evidence)
        {
            var resolved = _f2.Env.Store.Resolve(Acme, line.Reference);
            Assert.True(resolved.Succeeded, string.Join("; ", resolved.Diagnostics));
            Assert.Equal(line.Text, resolved.Value.Text);
            Assert.Equal(dossier.SnapshotId, line.Reference.SnapshotId);
        }

        var facts = dossier.Outgoing.Concat(dossier.Incoming).SelectMany(r => new[] { r.Edge.ReferenceEvidence, r.Edge.DefinitionEvidence })
            .Append(dossier.Definition?.Artifact.Evidence).OfType<EvidenceReference>();
        Assert.All(facts, reference => Assert.True(_f2.Env.Store.Resolve(Acme, reference).Succeeded));
    }

    // ---- Criterion 1: a direct call and file use ----------------------------------------------

    [Fact]
    public void ProgramWithCallAndFileUseYieldsCitedDirectLinks()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        var job = _f2.Explore(snapshot, F2Members.OrdJob);

        Assert.Equal(CoverageStatus.Complete, job.Coverage);
        Assert.Equal(4, job.Definition!.Artifact.Evidence.LineNumber);
        Assert.Equal("F1", job.Definition.FactId);
        Assert.Equal(2, job.OutgoingCounts.Total);
        var call = job.Outgoing[0];
        Assert.Equal((EdgeKind.CallsProgram, "ORDPOST", ResolutionStatus.Resolved), (call.Edge.Kind, call.Edge.Target, call.Edge.Resolution));
        Assert.Equal(F2Members.OrdPost, call.Edge.ResolvedMember);
        Assert.Equal(("parsed-reference", "resolved-by-f1-rule-assumed-caller-library"), (call.Observation, call.Basis));
        var missing = job.Outgoing[1];
        Assert.Equal((UnresolvedReason.TargetNotInSnapshot, "unresolved"), (missing.Edge.Reason, missing.Basis));
        Assert.Equal(DossierCompleteness.Partial, job.Completeness);
        Assert.Contains(job.Gaps, g => g.Code == "unresolved-outgoing" && g.Text.Contains("target-not-in-snapshot 1", StringComparison.Ordinal));
        Assert.Empty(job.Incoming);
        AssertCitationsResolve(job);

        var post = _f2.Explore(snapshot, F2Members.OrdPost);
        var incoming = Assert.Single(post.Incoming);
        Assert.Equal((EdgeDirection.Incoming, F2Members.OrdJob, 6), (incoming.Direction, incoming.Edge.SourceMember, incoming.Edge.ReferenceEvidence.LineNumber));
        Assert.Equal(2, post.Outgoing.Count);
        Assert.All(post.Outgoing, r => Assert.Equal((EdgeKind.UsesFile, F2Members.OrdHdr), (r.Edge.Kind, r.Edge.ResolvedMember)));
        Assert.True(post.Rederivation.SelectedMemberReparsed);
        Assert.Equal((2, 1), (post.Rederivation.OutgoingEdgesRederived, post.Rederivation.IncomingEdgesRederived));
        AssertCitationsResolve(post);

        // The caller's reference line and the target's definition line are packet evidence too.
        Assert.Contains(post.Evidence, e => e.Reference.Member == F2Members.OrdJob && e.Purposes.Contains(EvidencePurpose.IncomingReference));
        Assert.Contains(post.Evidence, e => e.Reference.Member == F2Members.OrdHdr && e.Purposes.Contains(EvidencePurpose.TargetDefinition));
        var hdr = _f2.Explore(snapshot, F2Members.OrdHdr);
        Assert.Equal(2, hdr.IncomingCounts.Total);
    }

    [Fact]
    public void DossierMatchesTheF1DependenciesQueryAndIsDeterministic()
    {
        var snapshot = _f2.Prepare("f1-edge-cases");
        var calls = Members.Edge("QCLSRC", "CALLS", SourceType.CL);
        var first = _f2.Explore(snapshot, calls);
        var second = _f2.Explore(snapshot, calls);
        Assert.Equal(Serialize(first), Serialize(second));

        var dependencies = _f2.Index.Service().Dependencies(Acme, snapshot, calls).Value;
        Assert.Equal(dependencies.Edges, first.Outgoing.Select(r => r.Edge));
        Assert.Equal(["F1", "F2", "F3"], first.Outgoing.Select(r => r.FactId).Prepend(first.Definition!.FactId).Take(3));
        Assert.Equal(first.Evidence.Select((e, i) => $"S{i + 1}"), first.Evidence.Select(e => e.Id));

        // Honest unresolved reasons, never an invented resolution.
        var reasons = first.Outgoing.Where(r => r.Edge.Resolution == ResolutionStatus.Unresolved).Select(r => r.Edge.Reason!.Value).ToHashSet();
        Assert.Superset(new HashSet<UnresolvedReason> { UnresolvedReason.DynamicTarget, UnresolvedReason.TargetNotInSnapshot, UnresolvedReason.AmbiguousTarget, UnresolvedReason.UnsupportedQualifier }, reasons);
        Assert.All(first.Outgoing.Where(r => r.Edge.Resolution == ResolutionStatus.Unresolved), r => Assert.Null(r.Edge.ResolvedMember));
        AssertCitationsResolve(first);
    }

    [Fact]
    public void ReRunningTheIndexChangesOnlyRunMetadata()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        var before = _f2.Explore(snapshot, F2Members.OrdPost);
        _f2.Index.BuildIndex("acme", snapshot);
        var after = _f2.Explore(snapshot, F2Members.OrdPost);

        Assert.NotEqual(before.Index.Provenance.AnalysisRunId, after.Index.Provenance.AnalysisRunId);
        Assert.Equal(JsonSerializer.Serialize(before.Evidence), JsonSerializer.Serialize(after.Evidence));
        Assert.Equal(before.Gaps, after.Gaps);
        Assert.Equal(before.Outgoing.Select(r => r.Edge with { Provenance = null! }), after.Outgoing.Select(r => r.Edge with { Provenance = null! }));
        Assert.All(after.Outgoing, r => Assert.Equal(after.Index.Provenance.AnalysisRunId, r.Edge.Provenance.AnalysisRunId));
    }

    // ---- Criterion 2: honest gaps ----------------------------------------------------------------

    [Fact]
    public void AmbiguousTargetShowsNoIncomingLinkButCountsNameMatches()
    {
        var snapshot = _f2.Prepare("f1-edge-cases");
        var duplicate = _f2.Explore(snapshot, Members.Edge("QCLSRC", "DUPPGM", SourceType.CL));

        Assert.Empty(duplicate.Incoming);
        Assert.True(duplicate.UnresolvedNameMatches >= 1);
        Assert.Contains(duplicate.Gaps, g => g.Code == "unresolved-name-matches");
        Assert.Contains(duplicate.Gaps, g => g.Code == "index-partial");
    }

    [Fact]
    public void CrossLibraryTargetIsNeverResolvedByName()
    {
        var source = Path.Combine(_f2.Env.Root, "cross");
        Directory.CreateDirectory(Path.Combine(source, "A"));
        Directory.CreateDirectory(Path.Combine(source, "B"));
        File.WriteAllText(Path.Combine(source, "A", "CALLER.clle"), "PGM\n  CALL PGM(TARGET)\nENDPGM\n");
        File.WriteAllText(Path.Combine(source, "B", "TARGET.clle"), "PGM\nENDPGM\n");
        TestEnvironment.WriteManifest(source, """
            { "formatVersion": 1, "members": [
              { "path": "A/CALLER.clle", "library": "LIBA", "sourceFile": "QCLSRC", "member": "CALLER", "sourceType": "CL", "ccsid": 1208 },
              { "path": "B/TARGET.clle", "library": "LIBB", "sourceFile": "QCLSRC", "member": "TARGET", "sourceType": "CL", "ccsid": 1208 } ] }
            """);
        var snapshot = _f2.PrepareExport(source);

        var caller = _f2.Explore(snapshot, Members.Of("LIBA", "QCLSRC", "CALLER", SourceType.CL));
        Assert.Equal(UnresolvedReason.TargetInOtherLibrary, Assert.Single(caller.Outgoing).Edge.Reason);
        var target = _f2.Explore(snapshot, Members.Of("LIBB", "QCLSRC", "TARGET", SourceType.CL));
        Assert.Empty(target.Incoming);
        Assert.Equal(1, target.UnresolvedNameMatches);
    }

    [Fact]
    public void EmptyMemberHasNoDefinitionAndSaysSo()
    {
        var source = _f2.Env.CreateExport(("QSRC/EMPTY.clle", "EMPTY", "CL", Array.Empty<byte>()), ("QSRC/CALLER.clle", "CALLER", "CL", "PGM\n  CALL PGM(EMPTY)\nENDPGM\n"u8.ToArray()));
        var snapshot = _f2.PrepareExport(source);
        var empty = _f2.Explore(snapshot, Members.Of("TESTLIB", "QSRC", "EMPTY", SourceType.CL));

        Assert.Null(empty.Definition);
        Assert.Equal(CoverageStatus.Unsupported, empty.Coverage);
        Assert.DoesNotContain(empty.Evidence, e => e.Reference.Member == empty.Identity);
        Assert.Contains(empty.Gaps, g => g.Code == "no-definition");
        Assert.Contains(empty.Gaps, g => g.Code == "member-unsupported");
        Assert.Equal(DossierCompleteness.Partial, empty.Completeness);

        // The literal call still resolves (F1 rule), with no definition evidence to show.
        var incoming = Assert.Single(empty.Incoming);
        Assert.Null(incoming.Edge.DefinitionEvidence);
        Assert.Null(incoming.DefinitionEvidenceId);
    }

    [Theory]
    [InlineData("valid-export", "DEMOLIB", "QRPGLESRC", "ORDINQ", SourceType.RPG, "member-partial")]
    [InlineData("f1-edge-cases", "EDGELIB", "QSQLSRC", "MALFORM", SourceType.SQL, null)]
    [InlineData("f1-edge-cases", "EDGELIB", "QRPGLESRC", "EDGERPG", SourceType.RPG, "member-partial")]
    public void PartialUnsupportedAndMalformedMembersAreLabeledPartial(string fixture, string library, string file, string member, SourceType type, string? gap)
    {
        var snapshot = _f2.Prepare(fixture);
        var dossier = _f2.Explore(snapshot, Members.Of(library, file, member, type));

        Assert.NotEqual(CoverageStatus.Complete, dossier.Coverage);
        Assert.Equal(DossierCompleteness.Partial, dossier.Completeness);
        Assert.Contains(dossier.Gaps, g => g.Code is "member-partial" or "member-unsupported");
        if (gap is not null)
        {
            Assert.Contains(dossier.Gaps, g => g.Code == gap);
        }

        Assert.NotEmpty(dossier.DiagnosticTotals);
        AssertCitationsResolve(dossier);
    }

    [Fact]
    public void CompleteMeansF1CompleteAndNothingOmitted()
    {
        var snapshot = _f2.Prepare("f1-complete");
        var dossier = _f2.Explore(snapshot, F2Members.CustMas);
        Assert.Equal(DossierCompleteness.Complete, dossier.Completeness);
        Assert.Equal(3, dossier.IncomingCounts.Total);
        Assert.Equal("resolved-by-f1-record-rule", Assert.Single(dossier.Outgoing).Basis);
        Assert.DoesNotContain(dossier.Gaps, g => g.Code is "member-partial" or "evidence-omitted" or "outgoing-truncated");
    }

    [Fact]
    public void EdgeBoundsTruncateWithExplicitCounts()
    {
        var calls = string.Join("\n", Enumerable.Range(0, 45).Select(i => $"  CALL PGM(TARGET{i % 3})"));
        var callers = Enumerable.Range(0, 42).Select(i => ($"QSRC/C{i:D2}.clle", $"C{i:D2}", "CL", Encoding.UTF8.GetBytes("PGM\n  CALL PGM(TARGET0)\nENDPGM\n")));
        var targets = Enumerable.Range(0, 3).Select(i => ($"QSRC/TARGET{i}.clle", $"TARGET{i}", "CL", "PGM\nENDPGM\n"u8.ToArray()));
        var source = _f2.Env.CreateExport([("QSRC/MANY.clle", "MANY", "CL", Encoding.UTF8.GetBytes($"PGM\n{calls}\nENDPGM\n")), .. callers, .. targets]);
        var snapshot = _f2.PrepareExport(source);

        var many = _f2.Explore(snapshot, Members.Of("TESTLIB", "QSRC", "MANY", SourceType.CL));
        Assert.Equal((45, 40, 5), (many.OutgoingCounts.Total, many.OutgoingCounts.Included, many.OutgoingCounts.Omitted));
        Assert.Equal(40, many.Outgoing.Count);
        Assert.Equal(Enumerable.Range(2, 40), many.Outgoing.Select(r => r.Edge.ReferenceEvidence.LineNumber)); // stable line order
        Assert.Contains(many.Gaps, g => g.Code == "outgoing-truncated" && g.Text.Contains("40 of 45", StringComparison.Ordinal));
        Assert.Equal(DossierCompleteness.Partial, many.Completeness);

        var target = _f2.Explore(snapshot, Members.Of("TESTLIB", "QSRC", "TARGET0", SourceType.CL));
        Assert.Equal(42 + 15, target.IncomingCounts.Total);
        Assert.Equal(40, target.Incoming.Count);
        Assert.Contains(target.Gaps, g => g.Code == "incoming-truncated");
        Assert.True(target.Evidence.Count <= ExplorerLimits.MaxEvidenceLines);
    }

    [Fact]
    public void LineAndByteBoundsOmitWholeLinesAndCountThem()
    {
        var longLines = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"/* {new string('x', 1000)} {i} */"));
        var manyLines = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"/* line {i} */"));
        var source = _f2.Env.CreateExport(
            ("QSRC/WIDE.clle", "WIDE", "CL", Encoding.UTF8.GetBytes($"PGM\n{longLines}\nENDPGM\n")),
            ("QSRC/TALL.clle", "TALL", "CL", Encoding.UTF8.GetBytes($"PGM\n{manyLines}\nENDPGM\n")));
        var snapshot = _f2.PrepareExport(source);

        var wide = _f2.Explore(snapshot, Members.Of("TESTLIB", "QSRC", "WIDE", SourceType.CL));
        Assert.True(wide.EvidenceSelection.IncludedBytes <= ExplorerLimits.MaxEvidenceBytes);
        Assert.Equal(wide.Evidence.Sum(e => Encoding.UTF8.GetByteCount(e.Text)), wide.EvidenceSelection.IncludedBytes);
        Assert.Equal(32, wide.EvidenceSelection.CandidateLines);
        Assert.Equal(32 - wide.Evidence.Count, wide.EvidenceSelection.OmittedLines);
        Assert.True(wide.EvidenceSelection.OmittedLines > 0);
        Assert.Contains(wide.Evidence, e => e.Purposes.Contains(EvidencePurpose.MemberDefinition)); // priority line kept
        Assert.Contains(wide.Gaps, g => g.Code == "evidence-omitted");

        var tall = _f2.Explore(snapshot, Members.Of("TESTLIB", "QSRC", "TALL", SourceType.CL));
        Assert.Equal(ExplorerLimits.MaxEvidenceLines, tall.Evidence.Count);
        Assert.Equal(102 - 64, tall.EvidenceSelection.OmittedLines);
        Assert.Equal(DossierCompleteness.Partial, tall.Completeness);
        AssertCitationsResolve(tall);
    }

    [Fact]
    public void MemberSkippedByIndexLimitsHasNoFactsAndSaysSo()
    {
        var big = Encoding.UTF8.GetBytes("PGM\n" + string.Concat(Enumerable.Repeat("/* padding */\n", 20)) + "ENDPGM\n");
        var source = _f2.Env.CreateExport(("QSRC/BIG.clle", "BIG", "CL", big), ("QSRC/SMALL.clle", "SMALL", "CL", "PGM\n  CALL PGM(BIG)\nENDPGM\n"u8.ToArray()));
        var snapshot = _f2.Env.Import("acme", source).Value.Snapshot.SnapshotId;
        Assert.True(_f2.Index.Service(IndexLimits.Default with { MaxMemberBytes = 100 }).Build(Acme, snapshot).Succeeded);

        var dossier = _f2.Explore(snapshot, Members.Of("TESTLIB", "QSRC", "BIG", SourceType.CL));
        Assert.Contains(dossier.Gaps, g => g.Code == "member-not-parsed");
        Assert.False(dossier.Rederivation.SelectedMemberReparsed);
        Assert.DoesNotContain(dossier.Evidence, e => e.Reference.Member.Member == "BIG");
        Assert.Null(dossier.Definition);
        Assert.Single(dossier.Incoming);
    }

    // ---- Criterion 2: integrity and authorization -------------------------------------------

    [Fact]
    public void WrongWorkspaceUnknownMemberAndMissingIndexAreTypedFailures()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Code(_f2.Explorer().Explore(TestEnvironment.Workspace("other"), snapshot, F2Members.OrdJob)));
        Assert.Equal(DiagnosticCode.MemberNotFound, Code(_f2.Explorer().Explore(Acme, snapshot, F2Members.CustJob)));

        var unindexed = _f2.Index.Import("acme", "f1-complete").SnapshotId;
        Assert.Equal(DiagnosticCode.IndexNotFound, Code(_f2.Explorer().Explore(Acme, unindexed, F2Members.CustJob)));
    }

    [Fact]
    public void CorruptLatestIndexFailsWithoutFallingBackToAnOlderRun()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        Assert.True(_f2.Explorer().Explore(Acme, snapshot, F2Members.OrdJob).Succeeded);
        var latest = _f2.Index.BuildIndex("acme", snapshot);
        var facts = Path.Combine(_f2.Index.RunDirectory("acme", latest), "facts.json");
        Access.MakeWritable(facts);
        File.WriteAllText(facts, File.ReadAllText(facts).Replace("ORDPOST", "ORDPOZT", StringComparison.Ordinal));

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_f2.Explorer().Explore(Acme, snapshot, F2Members.OrdJob)));
    }

    [Fact]
    public void AlteredSnapshotByteFailsBeforeDisplay()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        var stored = Path.Combine(_f2.Env.SnapshotsDirectory("acme"), snapshot.Value, "members", "000001.src");
        Access.MakeWritable(stored);
        var bytes = File.ReadAllBytes(stored);
        bytes[^3] ^= 0x01;
        File.WriteAllBytes(stored, bytes);

        Assert.Equal(DiagnosticCode.IntegrityFailure, Code(_f2.Explorer().Explore(Acme, snapshot, F2Members.OrdPost)));
        Assert.Equal(DiagnosticCode.IntegrityFailure, Code(_f2.Explorer().Explore(Acme, snapshot, F2Members.OrdJob))); // ORDPOST is a cited target
    }

    /// <summary>
    /// An index rewritten with consistent hashes and a valid F0 reference to the wrong line passes
    /// F1's citation check; F2 re-derives the displayed facts from source and rejects it.
    /// </summary>
    [Theory]
    [InlineData("edge-line")]
    [InlineData("edge-target")]
    [InlineData("definition-line")]
    [InlineData("coverage")]
    public void RehashedIndexWithWrongButValidLineIsCaughtByRederivation(string change)
    {
        var snapshot = _f2.Prepare("f2-explorer");
        var directory = IndexTampering.RunDirectory(_f2.Index, "acme", snapshot);
        var job = _f2.Explore(snapshot, F2Members.OrdJob);
        var lineFive = job.Evidence.Single(e => e.Reference.LineNumber == 5).Reference;
        IndexTampering.EditJson(Path.Combine(directory, "facts.json"), facts =>
        {
            var edge = facts["edges"]!.AsArray().First(e => (string?)e!["target"] == "ORDPOST")!;
            var definition = facts["artifacts"]!.AsArray().First(a => (string?)a!["kind"] == "member" && (string?)a["name"] == "ORDJOB")!;
            var valid = JsonNode.Parse(JsonSerializer.Serialize(new { line = lineFive.LineNumber, byteOffset = lineFive.ByteOffset, byteLength = lineFive.ByteLength, lineSha256 = lineFive.LineSha256 }))!;
            switch (change)
            {
                case "edge-line":
                    foreach (var field in EvidenceFields)
                    {
                        edge["evidence"]![field] = valid[field]!.DeepClone();
                    }

                    break;
                case "edge-target":
                    edge["target"] = "ORDHDR";
                    edge["targetName"] = "ORDHDR";
                    break;
                case "definition-line":
                    foreach (var field in EvidenceFields)
                    {
                        definition["evidence"]![field] = valid[field]!.DeepClone();
                    }

                    break;
                default:
                    var member = facts["members"]!.AsArray().First(m => (string?)m!["member"] == "ORDJOB")!;
                    member["coverage"] = "partial";
                    break;
            }
        });
        IndexTampering.RehashRun(directory);

        if (change is "edge-line" or "definition-line")
        {
            // F1's citation check alone accepts a valid reference to the wrong line.
            Assert.True(_f2.Index.Service().Dependencies(Acme, snapshot, F2Members.OrdJob).Succeeded);
        }

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_f2.Explorer().Explore(Acme, snapshot, F2Members.OrdJob)));
    }

    // ---- Privacy -------------------------------------------------------------------------------

    [Fact]
    public void ExploreLogsCarryIdsAndCountsOnly()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        _f2.Explore(snapshot, F2Members.OrdPost);
        _f2.Explorer().Explore(Acme, snapshot, Members.Of("F2LIB", "QCLSRC", "NOPE", SourceType.CL));

        var events = _f2.Log.Events.Where(e => e.Name.StartsWith("explore.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["explore.completed", "explore.rejected"], events.Select(e => e.Name));
        var text = string.Join("\n", events.Select(e => string.Join(" ", e.Fields.Select(f => $"{f.Key}={f.Value}"))));
        Canaries.AssertAbsent(text);
        foreach (var name in new[] { "ORDPOST", "ORDJOB", "ORDHDR", "F2LIB", "NOPE", "chain", "IGNORE" })
        {
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
