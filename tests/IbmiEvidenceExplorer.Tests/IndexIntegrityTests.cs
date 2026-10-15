using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1 criteria 5 and 6 and the index resource limits.</summary>
public sealed class IndexIntegrityTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");
    private static readonly WorkspaceId Other = TestEnvironment.Workspace("acme-2");

    private readonly IndexEnvironment _index = new();

    public void Dispose() => _index.Dispose();

    private static DiagnosticCode Code<T>(OperationResult<T> result) => Assert.Single(result.Diagnostics).Code;

    private (SnapshotSummary Snapshot, IndexSummary Index) IndexValid(string workspace = "acme")
    {
        var snapshot = _index.Import(workspace, "valid-export");
        return (snapshot, _index.BuildIndex(workspace, snapshot.SnapshotId));
    }

    private static void Tamper(string file, Func<string, string> change)
    {
        Access.MakeWritable(file);
        File.WriteAllText(file, change(File.ReadAllText(file)), new UTF8Encoding(false));
    }

    // ---- Criterion 5: isolation -----------------------------------------------------------------

    [Fact]
    public void SameNamesInTwoWorkspacesReturnOnlyTheSelectedData()
    {
        var a = _index.Env.CreateExport(("QSRC/ORDERS.sql", "ORDERS", "SQL", "CREATE VIEW VIEWA AS SELECT X FROM ORDERS;"u8.ToArray()));
        var b = _index.Env.CreateExport(("QSRC/ORDERS.sql", "ORDERS", "SQL", "CREATE VIEW VIEWB AS SELECT X FROM ORDERS;"u8.ToArray()));
        var snapA = _index.Env.Import("acme", a).Value.Snapshot.SnapshotId;
        var snapB = _index.Env.Import("acme-2", b).Value.Snapshot.SnapshotId;
        _index.BuildIndex("acme", snapA);
        _index.BuildIndex("acme-2", snapB);
        var service = _index.Service();

        Assert.Single(service.Search(Acme, snapA, "VIEWA").Value.Hits);
        Assert.Empty(service.Search(Acme, snapA, "VIEWB").Value.Hits);
        Assert.Single(service.Search(Other, snapB, "VIEWB").Value.Hits);
        Assert.All(service.Search(Acme, snapA, "ORDERS").Value.Hits, h => Assert.Equal(snapA, h.Evidence!.SnapshotId));

        // Another workspace's snapshot ID is simply not found; nothing about it is revealed.
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Code(service.Search(Other, snapA, "VIEWA")));
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Code(service.Build(Other, snapA)));
        var member = Members.Of("TESTLIB", "QSRC", "ORDERS", SourceType.SQL);
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Code(service.Dependencies(Other, snapA, member)));
        Assert.Equal("VIEWB", Assert.Single(service.Dependencies(Other, snapB, member).Value.Edges).SourceName);
    }

    [Fact]
    public void IndexCopiedIntoAnotherWorkspaceIsNotServed()
    {
        var (_, indexA) = IndexValid("acme");
        var (snapB, _) = IndexValid("acme-2");
        var copy = Path.Combine(_index.IndexesDirectory("acme-2"), snapB.SnapshotId.Value,
            FileSystemIndexStore.ParserDirectoryName(IndexFormats.ParserVersion), "run-" + new string('f', 32));
        CopyDirectory(_index.RunDirectory("acme", indexA), copy);

        var result = _index.Service().Search(Other, snapB.SnapshotId, "ORDHDR");
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(result)); // never falls back, never serves A's data
    }

    // ---- Criterion 5: tampering, missing storage, permissions ----------------------------------

    [Theory]
    [InlineData("facts.json", "\"ORDHDR\"", "\"ORDHDX\"")]
    [InlineData("facts.json", "\"line\": 5,", "\"line\": 6,")]
    [InlineData("index.json", "\"partial\"", "\"complete\"")]
    [InlineData("index.json", "\"workspaceId\": \"acme\"", "\"workspaceId\": \"acme-2\"")]
    [InlineData("complete.json", "\"indexSha256\": \"", "\"indexSha256\": \"0")]
    [InlineData("complete.json", "\"schemaVersion\": 2,", "\"schemaVersion\": 2, \"extra\": 1,")]
    public void TamperedIndexFilesAreRejected(string file, string find, string replace)
    {
        var (snapshot, index) = IndexValid();
        var path = Path.Combine(_index.RunDirectory("acme", index), file);
        Assert.Contains(find, File.ReadAllText(path), StringComparison.Ordinal);
        Tamper(path, text => text.Replace(find, replace, StringComparison.Ordinal));

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.OrdInq)));
    }

    [Fact]
    public void ConsistentlyRehashedHeaderCannotClaimCompleteness()
    {
        var (snapshot, index) = IndexValid();
        var directory = _index.RunDirectory("acme", index);
        Tamper(Path.Combine(directory, "index.json"), text => text.Replace("\"status\": \"partial\"", "\"status\": \"complete\"", StringComparison.Ordinal));
        var newHash = Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "index.json")));
        Tamper(Path.Combine(directory, "complete.json"), text => System.Text.RegularExpressions.Regex.Replace(text, "\"indexSha256\": \"[0-9a-f]+\"", $"\"indexSha256\": \"{newHash}\""));

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
    }

    // An index rewritten with consistent internal hashes passes the file checks; these tests show
    // that every returned citation must still equal F0's reference for its line, and that malformed
    // documents are rejected structurally rather than crashing.

    [Theory]
    [InlineData("artifact", "line")]
    [InlineData("artifact", "byteOffset")]
    [InlineData("artifact", "byteLength")]
    [InlineData("artifact", "lineSha256")]
    [InlineData("edge", "line")]
    [InlineData("edge", "lineSha256")]
    [InlineData("definition", "line")]
    [InlineData("definition", "byteOffset")]
    public void RehashedIndexCitingAnInconsistentLineIsRejected(string citation, string field)
    {
        var snapshot = _index.Import("acme", "f1-complete");
        var index = _index.BuildIndex("acme", snapshot.SnapshotId);
        var directory = _index.RunDirectory("acme", index);
        EditJson(Path.Combine(directory, "facts.json"), facts =>
        {
            // CMID (line 4) and CMNAME (line 5) are adjacent DDS fields; the CL call cites line 4; CUSTINQ's definition is line 1 and its dcl-f line 5.
            var artifacts = facts["artifacts"]!.AsArray();
            var edge = facts["edges"]!.AsArray().First(e => (string?)e!["target"] == "CUSTINQ")!;
            var (target, donor) = citation switch
            {
                "artifact" => (artifacts.First(a => (string?)a!["name"] == "CMID")!["evidence"]!, artifacts.First(a => (string?)a!["name"] == "CMNAME")!["evidence"]!),
                "edge" => (edge["evidence"]!, edge["definitionEvidence"]!),
                _ => (edge["definitionEvidence"]!, artifacts.First(a => (string?)a!["kind"] == "declared-file")!["evidence"]!),
            };
            target[field] = field == "byteLength" ? JsonValue.Create((int)target[field]! + 1) : donor[field]!.DeepClone();
        });
        RehashRun(directory);

        var service = _index.Service();
        var result = citation == "artifact"
            ? Code(service.Search(Acme, snapshot.SnapshotId, "CMID"))
            : Code(service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("DEMOLIB", "QCLSRC", "CUSTJOB", SourceType.CL)));
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, result);
        Assert.Single(service.Search(Acme, snapshot.SnapshotId, "CMNAME").Value.Hits); // an untouched citation is still served
    }

    [Theory]
    [InlineData("facts.json", "members", false)]
    [InlineData("facts.json", "members", true)]
    [InlineData("facts.json", "artifacts", false)]
    [InlineData("facts.json", "edges", false)]
    [InlineData("facts.json", "edges", true)]
    [InlineData("facts.json", "members.0", false)]
    [InlineData("facts.json", "members.0.library", false)]
    [InlineData("facts.json", "members.0.sha256", false)]
    [InlineData("facts.json", "members.0.diagnostics", false)]
    [InlineData("facts.json", "members.@diagnostics.diagnostics.0", false)]
    [InlineData("facts.json", "members.@diagnostics.diagnostics.0.message", false)]
    [InlineData("facts.json", "members.@diagnostics.diagnostics.@evidence.evidence.lineSha256", false)]
    [InlineData("facts.json", "artifacts.0", false)]
    [InlineData("facts.json", "artifacts.0.name", false)]
    [InlineData("facts.json", "artifacts.0.name", true)]
    [InlineData("facts.json", "artifacts.0.evidence", false)]
    [InlineData("facts.json", "artifacts.0.evidence", true)]
    [InlineData("facts.json", "artifacts.0.evidence.lineSha256", false)]
    [InlineData("facts.json", "edges.0", false)]
    [InlineData("facts.json", "edges.0.sourceName", false)]
    [InlineData("facts.json", "edges.0.target", false)]
    [InlineData("facts.json", "edges.0.evidence", false)]
    [InlineData("facts.json", "edges.@definitionEvidence.definitionEvidence.lineSha256", false)]
    [InlineData("index.json", "limits", false)]
    [InlineData("index.json", "counts", false)]
    [InlineData("index.json", "workspaceId", false)]
    [InlineData("index.json", "factsSha256", false)]
    [InlineData("complete.json", "indexSha256", false)]
    [InlineData("complete.json", "analysisRunId", false)]
    public void RehashedIndexWithNullOrMissingRequiredDataIsATypedIntegrityFailure(string file, string path, bool remove)
    {
        var (snapshot, index) = IndexValid();
        var directory = _index.RunDirectory("acme", index);
        EditJson(Path.Combine(directory, file), root =>
        {
            var segments = path.Split('.');
            var parent = segments[..^1].Aggregate(root, Step);
            var last = segments[^1];
            if (parent is JsonArray array)
            {
                array[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = null;
            }
            else if (remove)
            {
                Assert.True(parent.AsObject().Remove(last));
            }
            else
            {
                Assert.True(parent.AsObject().ContainsKey(last));
                parent[last] = null;
            }
        });
        RehashRun(directory);

        // "@name" picks the first array element whose property is present and non-empty.
        static JsonNode Step(JsonNode node, string segment) => segment[0] == '@'
            ? node.AsArray().First(e => e![segment[1..]] is { } value && (value is not JsonArray a || a.Count > 0))!
            : node is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]! : node[segment]!;

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.OrdBatch)));
    }

    private static void EditJson(string file, Action<JsonNode> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(file))!;
        edit(root);
        Tamper(file, _ => root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Recomputes facts.json -> index.json -> complete.json hashes so only the content check can catch a change.</summary>
    private static void RehashRun(string directory)
    {
        var facts = Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "facts.json")));
        Tamper(Path.Combine(directory, "index.json"), text => Regex.Replace(text, "\"factsSha256\": \"[0-9a-f]+\"", $"\"factsSha256\": \"{facts}\""));
        var metadata = Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "index.json")));
        Tamper(Path.Combine(directory, "complete.json"), text => Regex.Replace(text, "\"indexSha256\": \"[0-9a-f]+\"", $"\"indexSha256\": \"{metadata}\""));
    }

    [Fact]
    public void UnsupportedSchemaVersionIsTyped()
    {
        var (snapshot, index) = IndexValid();
        Tamper(Path.Combine(_index.RunDirectory("acme", index), "complete.json"),
            text => text.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal));
        Assert.Equal(DiagnosticCode.IndexSchemaUnsupported, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
    }

    [Fact]
    public void TamperedSnapshotSourceIsRejectedByIndexAndQueries()
    {
        var (snapshot, _) = IndexValid();
        var member = Path.Combine(_index.Env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value, "members", "000002.src");
        Tamper(member, text => text.Replace("OHSTAT", "OHSTAX", StringComparison.Ordinal));

        Assert.Equal(DiagnosticCode.IntegrityFailure, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "OHSTAT")));
        Assert.Equal(DiagnosticCode.IntegrityFailure, Code(_index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.OrdInq))); // cites ORDHDR
        Assert.True(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDBATCH").Succeeded); // untouched members stay queryable

        var runs = Directory.GetDirectories(Path.GetDirectoryName(_index.RunDirectory("acme", _index.Load(snapshot).Summary))!);
        Assert.Equal(DiagnosticCode.IntegrityFailure, Code(_index.Service().Build(Acme, snapshot.SnapshotId)));
        Assert.Equal(runs, Directory.GetDirectories(Path.GetDirectoryName(runs[0])!)); // nothing new published
    }

    [Fact]
    public void MissingIndexMissingStorageAndUnknownMemberAreTyped()
    {
        var snapshot = _index.Import("acme", "valid-export");
        Assert.Equal(DiagnosticCode.IndexNotFound, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
        _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Equal(DiagnosticCode.MemberNotFound,
            Code(_index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.Of("DEMOLIB", "QCLSRC", "NOPE", SourceType.CL))));
        Assert.Equal(DiagnosticCode.InvalidQuery, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "two words")));
        Assert.Equal(DiagnosticCode.InvalidQuery, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "")));

        var missing = new SourceIndexService(new FileSystemSnapshotStore(Path.Combine(_index.Env.Root, "nowhere")),
            new FileSystemIndexStore(Path.Combine(_index.Env.Root, "nowhere")));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Code(missing.Search(Acme, snapshot.SnapshotId, "ORDHDR")));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Code(missing.Build(Acme, snapshot.SnapshotId)));
    }

    [SkippableFact]
    public void UnreadableIndexOrSnapshotStorageIsTyped()
    {
        var (snapshot, _) = IndexValid();
        using (Access.DenyRead(_index.IndexesDirectory("acme")))
        {
            Assert.Equal(DiagnosticCode.StorageUnavailable, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
            Assert.Equal(DiagnosticCode.StorageUnavailable, Code(_index.Service().Build(Acme, snapshot.SnapshotId)));
        }

        var member = Path.Combine(_index.Env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value, "members", "000000.src");
        using (Access.DenyRead(member))
        {
            Assert.Equal(DiagnosticCode.StorageUnavailable, Code(_index.Service().Build(Acme, snapshot.SnapshotId)));
        }

        Assert.True(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR").Succeeded);
    }

    // ---- Criterion 6: interruption, cancellation, versions ------------------------------------

    [Theory]
    [InlineData(IndexPublishStage.FactsWritten)]
    [InlineData(IndexPublishStage.MetadataWritten)]
    [InlineData(IndexPublishStage.MarkerWritten)]
    public void StorageFailureAtAnyPublishStageLeavesNothingAndRetrySucceeds(IndexPublishStage failAt)
    {
        var snapshot = _index.Import("acme", "valid-export");
        _index.IndexStore.FaultInjector = stage =>
        {
            if (stage == failAt)
            {
                throw new IOException("simulated disk failure");
            }
        };

        Assert.Equal(DiagnosticCode.PublishFailed, Code(_index.Service().Build(Acme, snapshot.SnapshotId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_index.IndexStagingDirectory("acme")));
        Assert.Equal(DiagnosticCode.IndexNotFound, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));

        _index.IndexStore.FaultInjector = null;
        _index.BuildIndex("acme", snapshot.SnapshotId);
        Assert.Single(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR").Value.Hits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(4)]
    public void CancellationPublishesNothing(int cancelAfterReads)
    {
        var snapshot = _index.Import("acme", "valid-export");
        using var cancellation = new CancellationTokenSource();
        if (cancelAfterReads < 0)
        {
            _index.IndexStore.FaultInjector = stage => cancellation.Cancel(); // during publish
        }

        var reads = 0;
        var store = new ObservingSnapshotStore(_index.Env.Store, _ =>
        {
            if (++reads == cancelAfterReads)
            {
                cancellation.Cancel();
            }
        });

        var result = _index.Service(snapshots: store).Build(Acme, snapshot.SnapshotId, cancellation.Token);
        Assert.Equal(DiagnosticCode.IndexCancelled, Code(result));
        var staging = _index.IndexStagingDirectory("acme");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
        Assert.False(Directory.Exists(Path.Combine(_index.IndexesDirectory("acme"), snapshot.SnapshotId.Value))
            && Directory.EnumerateDirectories(Path.Combine(_index.IndexesDirectory("acme"), snapshot.SnapshotId.Value), "run-*", SearchOption.AllDirectories).Any());

        _index.IndexStore.FaultInjector = null;
        _index.BuildIndex("acme", snapshot.SnapshotId);
    }

    [Fact]
    public void AbandonedStagingAndUnmarkedRunsAreNeverServed()
    {
        var (snapshot, index) = IndexValid();
        var run = _index.RunDirectory("acme", index);
        var abandoned = Path.Combine(_index.IndexStagingDirectory("acme"), snapshot.SnapshotId.Value + ".run-abandoned");
        CopyDirectory(run, abandoned);

        // A newer run directory without a completion marker is rejected, never silently skipped.
        var unmarked = Path.Combine(Path.GetDirectoryName(run)!, "run-" + new string('f', 32));
        CopyDirectory(run, unmarked);
        File.SetAttributes(Path.Combine(unmarked, "complete.json"), FileAttributes.Normal);
        File.Delete(Path.Combine(unmarked, "complete.json"));
        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));

        TestSupportDelete(unmarked);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(run)!, "not-a-run"));
        Assert.Single(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR").Value.Hits);
        Assert.True(Directory.Exists(abandoned)); // no automatic deletion
    }

    [Fact]
    public void DifferentParserVersionNeverReusesAnotherVersionsIndex()
    {
        var (snapshot, _) = IndexValid();
        var newer = _index.Service(parserVersion: "ibmi-explorer-f1-parser/2-test");
        Assert.Equal(DiagnosticCode.IndexNotFound, Code(newer.Search(Acme, snapshot.SnapshotId, "ORDHDR")));

        var built = newer.Build(Acme, snapshot.SnapshotId);
        Assert.Equal("ibmi-explorer-f1-parser/2-test", built.Value.Index.Provenance.ParserVersion);
        Assert.Equal("ibmi-explorer-f1-parser/2-test", Assert.Single(newer.Search(Acme, snapshot.SnapshotId, "ORDHDR").Value.Hits).Provenance.ParserVersion);
        Assert.Equal(IndexFormats.ParserVersion, Assert.Single(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR").Value.Hits).Provenance.ParserVersion);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_index.IndexesDirectory("acme"), snapshot.SnapshotId.Value)).Length);
    }

    // ---- Resource limits -----------------------------------------------------------------------

    [Fact]
    public void OversizedMembersAreSkippedFromMetadataWithoutBeingRead()
    {
        var snapshot = _index.Import("acme", "valid-export");
        var store = new ObservingSnapshotStore(_index.Env.Store);
        var limits = IndexLimits.Default with { MaxMemberBytes = 280 };
        var built = _index.Service(limits, store).Build(Acme, snapshot.SnapshotId).Value;

        var large = snapshot.Members.Where(m => m.ByteLength > 280).Select(m => m.Identity).ToList();
        Assert.NotEmpty(large);
        Assert.DoesNotContain(store.Reads, large.Contains);
        Assert.Equal(IndexStatus.Partial, built.Index.Status);
        Assert.All(built.Members.Where(m => large.Contains(m.Member.Identity)), m =>
        {
            Assert.Equal(CoverageStatus.Unsupported, m.Coverage);
            Assert.Equal(ParseDiagnosticCode.MemberTooLarge, Assert.Single(m.Diagnostics).Code);
        });
        Assert.Empty(_index.Service(limits).Search(Acme, snapshot.SnapshotId, large[0].Member).Value.Hits);
    }

    [Fact]
    public void TotalBytesMemberCountAndItemLimitsSkipMembersExplicitly()
    {
        var snapshot = _index.Import("acme", "f1-complete");
        var bytes = snapshot.Members[0].ByteLength + snapshot.Members[1].ByteLength;

        ParseDiagnosticCode[] Skipped(IndexLimits limits)
        {
            var built = _index.Service(limits).Build(Acme, snapshot.SnapshotId).Value;
            Assert.Equal(IndexStatus.Partial, built.Index.Status);
            return built.Members.Where(m => m.Coverage == CoverageStatus.Unsupported).Select(m => Assert.Single(m.Diagnostics).Code).ToArray();
        }

        Assert.Equal([ParseDiagnosticCode.SourceLimitExceeded, ParseDiagnosticCode.SourceLimitExceeded], Skipped(IndexLimits.Default with { MaxTotalBytes = bytes }));
        Assert.Equal([ParseDiagnosticCode.MemberLimitExceeded], Skipped(IndexLimits.Default with { MaxMembers = 3 }));
        Assert.Equal([ParseDiagnosticCode.ItemLimitExceeded, ParseDiagnosticCode.ItemLimitExceeded, ParseDiagnosticCode.ItemLimitExceeded],
            Skipped(IndexLimits.Default with { MaxItems = 4 }));
    }

    [Fact]
    public void QueryResultsAreTruncatedExplicitly()
    {
        var members = Enumerable.Range(1, 5)
            .Select(i => ($"QSRC/F{i}.pf", $"F{i}", "DDS", Encoding.UTF8.GetBytes(Parse.Dds('R', "REC") + "\n" + Parse.Dds(' ', "SHARED", "1", 'A') + "\n")))
            .ToArray();
        var snapshot = _index.Env.Import("acme", _index.Env.CreateExport(members)).Value.Snapshot;
        var limits = IndexLimits.Default with { MaxQueryResults = 3 };
        Assert.True(_index.Service(limits).Build(Acme, snapshot.SnapshotId).Succeeded);

        var result = _index.Service(limits).Search(Acme, snapshot.SnapshotId, "SHARED").Value;
        Assert.Equal((5, true, 3), (result.TotalMatches, result.Truncated, result.Hits.Count));
        Assert.Equal(["F1", "F2", "F3"], result.Hits.Select(h => h.Member.Member)); // deterministic order
    }

    // ---- Logging and Windows privacy -----------------------------------------------------------

    [Fact]
    public void RoutineLogsCarryNoSourceTextOrQueries()
    {
        var snapshot = _index.Import("acme", "f1-edge-cases");
        _index.BuildIndex("acme", snapshot.SnapshotId);
        _index.Service().Search(Acme, snapshot.SnapshotId, "EFLD1");
        _index.Service().Dependencies(Acme, snapshot.SnapshotId, Members.Edge("QCLSRC", "CALLS", SourceType.CL));

        var log = _index.Env.Log.AllText();
        Canaries.AssertAbsent(log);
        foreach (var sourceText in new[] { "EFLD1", "NOSUCHPGM", "PGMNAME", "INSTRING", "Synthetic", "EDGER", "outside the F1" })
        {
            Assert.DoesNotContain(sourceText, log, StringComparison.Ordinal);
        }

        Assert.Contains(_index.Env.Log.Events, e => e.Name == "index.published" && e.Fields["status"] == "partial");
        Assert.Contains(_index.Env.Log.Events, e => e.Name == "index.searched");
    }

    [SkippableFact]
    public void WindowsIndexStorageIsPrivateAndReadOnly()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows DACL test");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (_, index) = IndexValid();
        var run = _index.RunDirectory("acme", index);
        foreach (var directory in new[] { _index.IndexesDirectory("acme"), _index.IndexStagingDirectory("acme"), Path.GetDirectoryName(Path.GetDirectoryName(run))!,
            Path.GetDirectoryName(run)!, run })
        {
            StoragePrivacy.AssertOnlyCurrentUser(new DirectoryInfo(directory).GetAccessControl(), directory);
        }

        foreach (var file in Directory.GetFiles(run))
        {
            StoragePrivacy.AssertOnlyCurrentUser(new FileInfo(file).GetAccessControl(), file);
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly), file);
        }
    }

    [SkippableFact]
    public void WindowsNonPrivateIndexDirectoryIsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows DACL test");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (snapshot, _) = IndexValid();
        var indexes = new DirectoryInfo(_index.IndexesDirectory("acme"));
        var security = indexes.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        indexes.SetAccessControl(security);

        Assert.Equal(DiagnosticCode.StorageUnavailable, Code(_index.Service().Search(Acme, snapshot.SnapshotId, "ORDHDR")));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Code(_index.Service().Build(Acme, snapshot.SnapshotId)));
    }

    /// <summary>Plants a copy with a private DACL (as the store creates), so the integrity checks, not the privacy check, are exercised.</summary>
    private static void CopyDirectory(string source, string target)
    {
        PrivateStorage.CreateDirectory(target, verifyExisting: true);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, destination);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
    }

    private static void TestSupportDelete(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
