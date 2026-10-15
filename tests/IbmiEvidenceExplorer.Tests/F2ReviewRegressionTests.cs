using System.Diagnostics;
using System.Globalization;
using System.Text;
using IbmiEvidenceExplorer.Cli;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure.Ollama;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// Regressions for Codex's F2 review findings: (1) forged limit skips, (2) body-read timeouts and
/// the explanation deadline, (3) uncited summary and gap prose, (4) bounded re-derivation, and the
/// D-017 item 3 model-identity checks.
/// Runs in <see cref="SerialHttpTests"/> because several tests assert wall-clock deadlines.
/// </summary>
[Collection(nameof(SerialHttpTests))]
public sealed class F2ReviewRegressionTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");
    private static readonly LocalChatRequest Chat = new(ExplorerEnvironment.Model, "SYSTEM", "USER", ExplorerEnvironment.SampleSchema, 64);

    private readonly ExplorerEnvironment _f2 = new();

    public void Dispose() => _f2.Dispose();

    private static DiagnosticCode Code<T>(OperationResult<T> result) => Assert.Single(result.Diagnostics).Code;

    // ---- Finding 1: a forged limit skip cannot bypass re-derivation ----------------------------

    private static readonly MemberIdentity Alone = Members.Of("TESTLIB", "QSRC", "ALONE", SourceType.CL);

    private SnapshotId PrepareAlone() => _f2.PrepareExport(_f2.Env.CreateExport(
        ("QSRC/ALONE.clle", "ALONE", "CL", "PGM\n  DCL VAR(&X) TYPE(*DEC) LEN(1 0)\nENDPGM\n"u8.ToArray()),
        ("QSRC/OTHER.clle", "OTHER", "CL", "PGM\nENDPGM\n"u8.ToArray())));

    private static IndexedMember Skip(IndexedMember member, ParseDiagnosticCode code, CoverageStatus coverage = CoverageStatus.Unsupported) =>
        member with
        {
            Coverage = coverage,
            Diagnostics = [new IndexDiagnostic(code, "Member was not indexed.", null)],
            DiagnosticTotals = new Dictionary<ParseDiagnosticCode, int> { [code] = 1 },
            SuppressedDiagnostics = 0,
        };

    [Theory]
    [InlineData("codex-complete-coverage")]
    [InlineData("member-limit-not-reached")]
    [InlineData("member-not-too-large")]
    [InlineData("total-bytes-not-reached")]
    [InlineData("items-not-reached")]
    [InlineData("skip-keeps-facts")]
    [InlineData("admitted-beyond-member-limit")]
    public void ForgedOrIncoherentLimitSkipIsAnIntegrityFailure(string variant)
    {
        var snapshot = PrepareAlone();
        var genuine = _f2.Explore(snapshot, Alone);
        Assert.Equal(CoverageStatus.Complete, genuine.Coverage);

        IReadOnlyList<IndexedMember> Members(IReadOnlyList<IndexedMember> all, Func<IndexedMember, IndexedMember> change) =>
            all.Select(m => m.Member.Identity == Alone ? change(m) : m).ToList();
        IReadOnlyList<IndexedArtifact> WithoutAloneFacts(IReadOnlyList<IndexedArtifact> all) => all.Where(a => a.Member != Alone).ToList();

        switch (variant)
        {
            case "codex-complete-coverage":
                // Codex's forgery: a limit diagnostic on a member still claiming complete coverage, definition removed.
                IndexTampering.Republish(_f2.Index, "acme", snapshot, m => Members(m, x => Skip(x, ParseDiagnosticCode.MemberLimitExceeded, CoverageStatus.Complete)), WithoutAloneFacts);
                break;
            case "member-limit-not-reached":
                IndexTampering.Republish(_f2.Index, "acme", snapshot, m => Members(m, x => Skip(x, ParseDiagnosticCode.MemberLimitExceeded)), WithoutAloneFacts);
                break;
            case "member-not-too-large":
                IndexTampering.Republish(_f2.Index, "acme", snapshot, m => Members(m, x => Skip(x, ParseDiagnosticCode.MemberTooLarge)), WithoutAloneFacts);
                break;
            case "total-bytes-not-reached":
                IndexTampering.Republish(_f2.Index, "acme", snapshot, m => Members(m, x => Skip(x, ParseDiagnosticCode.SourceLimitExceeded)), WithoutAloneFacts);
                break;
            case "items-not-reached":
                IndexTampering.Republish(_f2.Index, "acme", snapshot, m => Members(m, x => Skip(x, ParseDiagnosticCode.ItemLimitExceeded)), WithoutAloneFacts);
                break;
            case "skip-keeps-facts":
                // A skip consistent with the limits (after lowering them) that still carries a definition and facts.
                IndexTampering.Republish(_f2.Index, "acme", snapshot, m => Members(m, x => Skip(x, ParseDiagnosticCode.MemberTooLarge)),
                    limits: IndexLimits.Default with { MaxMemberBytes = 20 });
                break;
            default:
                // Lowered member limit, but members past it still stored as admitted.
                IndexTampering.Republish(_f2.Index, "acme", snapshot, limits: IndexLimits.Default with { MaxMembers = 1 });
                break;
        }

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_f2.Explorer().Explore(Acme, snapshot, Alone)));
    }

    [Theory]
    [InlineData("member-bytes")]
    [InlineData("items")]
    [InlineData("members")]
    public void GenuineLimitSkipsAreAcceptedAndStayVisiblyIncomplete(string limit)
    {
        var source = _f2.Env.CreateExport(
            ("QSRC/FIRST.clle", "FIRST", "CL", "PGM\n  CALL PGM(ALONE)\nENDPGM\n"u8.ToArray()),
            ("QSRC/ALONE.clle", "ALONE", "CL", Encoding.UTF8.GetBytes("PGM\n" + string.Concat(Enumerable.Repeat("  DCL VAR(&X) TYPE(*DEC) LEN(1 0)\n", 5)) + "ENDPGM\n")));
        var snapshot = _f2.Env.Import("acme", source).Value.Snapshot.SnapshotId;
        var limits = limit switch
        {
            "member-bytes" => IndexLimits.Default with { MaxMemberBytes = 100 },
            "items" => IndexLimits.Default with { MaxItems = 4 },
            _ => IndexLimits.Default with { MaxMembers = 1 },
        };
        Assert.True(_f2.Index.Service(limits).Build(Acme, snapshot).Succeeded);

        var dossier = _f2.Explore(snapshot, Alone);
        Assert.Equal(CoverageStatus.Unsupported, dossier.Coverage);
        Assert.Equal(DossierCompleteness.Partial, dossier.Completeness);
        Assert.Contains(dossier.Gaps, g => g.Code == "member-not-parsed");
        Assert.False(dossier.Rederivation.SelectedMemberReparsed);
        Assert.Null(dossier.Definition);
    }

    // ---- Finding 2: the body read honors the deadline -----------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StallAfterHeadersTimesOutOnTime(bool chunked)
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, FakeOllamaServer.ChatEnvelope("{}"), ChunkedBody: chunked, StallAfterBytes: 1));
        using var client = ExplorerEnvironment.Client(server, TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        var result = client.Chat(Chat);
        watch.Stop();

        Assert.Equal(DiagnosticCode.LocalAiTimeout, Code(result));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCancellationDuringBodyIsCancelledNotTimeout(bool chunked)
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, FakeOllamaServer.ChatEnvelope("{}"), ChunkedBody: chunked, StallAfterBytes: 1));
        using var client = ExplorerEnvironment.Client(server, TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        Assert.Equal(DiagnosticCode.ExplainCancelled, Code(client.Chat(Chat, cancel.Token)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void OneDeadlineBoundsTheWholeTwoCallExplanation()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = new FakeOllamaServer(r => r.Target == "/api/tags"
            ? new FakeResponse(200, FakeOllamaServer.Tags(ExplorerEnvironment.Model), Delay: TimeSpan.FromMilliseconds(1000))
            : new FakeResponse(200, FakeOllamaServer.ChatEnvelope(Replies.Faithful(r)), Delay: TimeSpan.FromMilliseconds(1000)));
        using var client = ExplorerEnvironment.Client(server, TimeSpan.FromSeconds(30)); // the per-call backstop is not what fires
        var request = ExplorerEnvironment.Request(snapshot, F2Members.OrdJob) with { Timeout = TimeSpan.FromMilliseconds(1700) };
        var watch = Stopwatch.StartNew();
        var result = _f2.Explanations(client).Explain(request);
        watch.Stop();

        // Each call alone is under the deadline; together they are not.
        Assert.Equal(DiagnosticCode.LocalAiTimeout, Code(result));
        Assert.Single(server.Chats);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void ExplanationTimeoutMustBeBounded(int seconds)
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = FakeOllamaServer.Answering(Replies.Faithful);
        using var client = ExplorerEnvironment.Client(server);
        var request = ExplorerEnvironment.Request(snapshot, F2Members.OrdJob) with { Timeout = TimeSpan.FromSeconds(seconds) };
        Assert.Equal(DiagnosticCode.InvalidArgument, Code(_f2.Explanations(client).Explain(request)));
        Assert.Equal(0, server.Connections);
    }

    // ---- Finding 3: no uncited prose is presented as an explanation ---------------------------

    [Theory]
    [InlineData("summary")]
    [InlineData("gaps")]
    public void FalseSummaryOrGapsBesideAValidUnknownAreRejected(string field)
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = FakeOllamaServer.Answering(r => Replies.Build(r, "answered",
            [("unknown", "What ORDJOB does with the order is not in the packet.", [])],
            o => o[field] = field == "summary" ? "ORDJOB deletes every PAYROLL record nightly." : new System.Text.Json.Nodes.JsonArray("It also purges AUDITLOG.")));
        using var client = ExplorerEnvironment.Client(server);

        var result = _f2.Explanations(client).Explain(ExplorerEnvironment.Request(snapshot, F2Members.OrdJob));
        Assert.Equal(DiagnosticCode.LocalAiInvalidOutput, Code(result));
    }

    [Fact]
    public void UnknownOnlyReplyIsShownAsExplainingNothing()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = FakeOllamaServer.Answering(r => Replies.For(r, ("unknown", "ORDJOB deletes PAYROLL; the packet does not show it.", [])));
        var output = new StringWriter();
        var exit = CliApp.Run(["explain", "--workspace", "acme", "--snapshot", snapshot.Value, "--library", "F2LIB", "--source-file", "QCLSRC", "--member", "ORDJOB",
            "--type", "CL", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service", "--ollama-port", server.Port.ToString(CultureInfo.InvariantCulture)],
            output, new StringWriter(), name => name == CliApp.StorageEnvironmentVariable ? _f2.Env.StorageRoot : null);

        Assert.Equal(CliApp.Ok, exit);
        var text = output.ToString();
        var findings = text.IndexOf("Findings (", StringComparison.Ordinal);
        var unknowns = text.IndexOf("Unknowns stated by the model (unverified, uncited; not findings):", StringComparison.Ordinal);
        Assert.True(findings > 0 && unknowns > findings);
        Assert.Contains("  none", text[findings..unknowns], StringComparison.Ordinal);
        Assert.DoesNotContain("PAYROLL", text[..unknowns], StringComparison.Ordinal); // model text appears only under unverified unknowns
        Assert.Contains("explains nothing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary", text, StringComparison.Ordinal);

        using var client = ExplorerEnvironment.Client(server);
        var draft = _f2.Explanations(client).Explain(ExplorerEnvironment.Request(snapshot, F2Members.OrdJob)).Value.Draft;
        Assert.Empty(draft.Findings);
        Assert.Single(draft.Unknowns);
    }

    [Fact]
    public void PromptAndSchemaHaveNoUncitedProseFields()
    {
        Assert.DoesNotContain("summary", ExplorerEnvironment.SampleSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gaps\"", ExplorerEnvironment.SampleSchema, StringComparison.Ordinal);
        Assert.Equal("ibmi-explorer-explanation/3", ExplanationFormats.OutputSchemaVersion);
        Assert.Equal("ibmi-explorer-f2-prompt/3", ExplanationFormats.PromptTemplateVersion);
    }

    // ---- Finding 4: bounded re-derivation for high fan-out -------------------------------------

    private (SnapshotId Snapshot, MemberIdentity Hub) PrepareFanOut()
    {
        var calls = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"  CALL PGM(T{i:D3})"));
        var targets = Enumerable.Range(0, 100).Select(i => ($"QSRC/T{i:D3}.clle", $"T{i:D3}", "CL", "PGM\nENDPGM\n"u8.ToArray()));
        var callers = Enumerable.Range(0, 45).Select(i => ($"QSRC/C{i:D3}.clle", $"C{i:D3}", "CL", "PGM\n  CALL PGM(HUB)\nENDPGM\n"u8.ToArray()));
        var source = _f2.Env.CreateExport([("QSRC/HUB.clle", "HUB", "CL", Encoding.UTF8.GetBytes($"PGM\n{calls}\nENDPGM\n")), .. targets, .. callers]);
        return (_f2.PrepareExport(source), Members.Of("TESTLIB", "QSRC", "HUB", SourceType.CL));
    }

    [Fact]
    public void HighFanOutReadsAtMostTheDisplayedMembers()
    {
        var (snapshot, hub) = PrepareFanOut();
        var observing = new ObservingSnapshotStore(_f2.Env.Store);
        var result = _f2.Explorer(observing).Explore(Acme, snapshot, hub);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        var dossier = result.Value;

        // Selected member + 40 displayed targets + 40 displayed callers, each read once.
        Assert.Equal(observing.Reads.Count, observing.Reads.Distinct().Count());
        Assert.Equal(ExplorerLimits.MaxRederivedMembers, observing.Reads.Count);
        Assert.Equal(81, ExplorerLimits.MaxRederivedMembers);
        Assert.DoesNotContain(observing.Reads, m => m.Member is "T040" or "T099" or "C040" or "C044");

        var r = dossier.Rederivation;
        Assert.Equal((40, 60, 40, 5, 81), (r.OutgoingEdgesRederived, r.OutgoingEdgesWithoutTargetDefinition, r.IncomingEdgesRederived, r.IncomingEdgesNotRederived, r.MembersReparsed));
        Assert.Contains(dossier.Gaps, g => g.Code == "not-rederived" && g.Text.Contains("60 omitted outgoing", StringComparison.Ordinal));
        Assert.Contains(dossier.Gaps, g => g.Code == "not-rederived" && g.Text.Contains("5 omitted incoming", StringComparison.Ordinal));
        Assert.Equal(DossierCompleteness.Partial, dossier.Completeness);
    }

    [Fact]
    public void OmittedOutgoingEdgesAreStillCheckedForReferenceTargetAndResolution()
    {
        var (snapshot, hub) = PrepareFanOut();
        IndexTampering.Republish(_f2.Index, "acme", snapshot, edges: all => all.Select(e =>
            e.SourceMember == hub && e.Target == "T077" ? e with { Resolution = ResolutionStatus.Unresolved, ResolvedMember = null, DefinitionEvidence = null, Reason = UnresolvedReason.TargetNotInSnapshot, CandidateCount = 0 } : e).ToList());

        Assert.Equal(DiagnosticCode.IndexIntegrityFailure, Code(_f2.Explorer().Explore(Acme, snapshot, hub)));
    }

    [Fact]
    public void OmittedOutgoingTargetDefinitionsAreReportedUnverifiedNotChecked()
    {
        // Documented limit: an omitted edge's target-definition citation is not read; the gap says so.
        var (snapshot, hub) = PrepareFanOut();
        var dossier = _f2.Explore(snapshot, hub);
        Assert.DoesNotContain(dossier.Outgoing, r => r.Edge.Target == "T077");
        Assert.Contains(dossier.Gaps, g => g.Text.Contains("target definitions were not read or verified", StringComparison.Ordinal));
    }

    // ---- D-017 item 3: aliases, contradictory cloud metadata, and the answering model -------

    [Theory]
    [InlineData("""{"models":[{"name":"local-test:1b","model":"big:cloud","digest":"aa"}]}""", DiagnosticCode.LocalAiModelNotLocal)]
    [InlineData("""{"models":[{"name":"local-test:1b","model":"qwen-x-cloud","digest":"aa"}]}""", DiagnosticCode.LocalAiModelNotLocal)]
    [InlineData("""{"models":[{"name":"a:1","model":"local-test:1b"},{"name":"b:1","model":"local-test:1b"}]}""", DiagnosticCode.LocalAiModelNotFound)]
    public void ContradictoryOrAmbiguousListingsAreRefused(string tags, DiagnosticCode expected)
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, tags));
        using var client = ExplorerEnvironment.Client(server);
        Assert.Equal(expected, Code(client.FindLocalModel(ExplorerEnvironment.Model)));
    }

    [Fact]
    public void AListedAliasIsRecordedAndAcceptedAsTheAnsweringModel()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = new FakeOllamaServer(r => r.Target == "/api/tags"
            ? new FakeResponse(200, """{"models":[{"name":"local-test:1b","model":"local-test-model-id:1b","digest":"abc123"}]}""")
            : new FakeResponse(200, FakeOllamaServer.ChatEnvelope(Replies.Faithful(r), "local-test-model-id:1b")));
        using var client = ExplorerEnvironment.Client(server);
        var result = _f2.Explanations(client).Explain(ExplorerEnvironment.Request(snapshot, F2Members.OrdJob));

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        var p = result.Value.Draft.Provenance;
        Assert.Equal(("local-test:1b", "local-test:1b", "local-test-model-id:1b"), (p.ModelRequested, p.ModelListed, p.ModelReported));
    }

    [Theory]
    [InlineData("other-model:7b", DiagnosticCode.LocalAiModelMismatch)]
    [InlineData(null, DiagnosticCode.LocalAiModelMismatch)]
    [InlineData("local-test:1b-cloud", DiagnosticCode.LocalAiModelNotLocal)]
    public void AReplyFromADifferentOrCloudModelYieldsNoDraft(string? reported, DiagnosticCode expected)
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = new FakeOllamaServer(r =>
        {
            if (r.Target == "/api/tags")
            {
                return new FakeResponse(200, FakeOllamaServer.Tags(ExplorerEnvironment.Model));
            }

            var envelope = System.Text.Json.Nodes.JsonNode.Parse(FakeOllamaServer.ChatEnvelope(Replies.Faithful(r)))!.AsObject();
            envelope.Remove("model");
            if (reported is not null)
            {
                envelope["model"] = reported;
            }

            return new FakeResponse(200, envelope.ToJsonString());
        });
        using var client = ExplorerEnvironment.Client(server);
        Assert.Equal(expected, Code(_f2.Explanations(client).Explain(ExplorerEnvironment.Request(snapshot, F2Members.OrdJob))));
    }

    [Fact]
    public void UntaggedRequestMatchesItsLatestTag()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        using var server = FakeOllamaServer.Answering(Replies.Faithful, model: "plain:latest");
        using var client = ExplorerEnvironment.Client(server);
        var result = _f2.Explanations(client).Explain(ExplorerEnvironment.Request(snapshot, F2Members.OrdJob, model: "plain"));
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        Assert.Equal(("plain", "plain:latest", "plain:latest"), (result.Value.Draft.Provenance.ModelRequested, result.Value.Draft.Provenance.ModelListed, result.Value.Draft.Provenance.ModelReported));
        Assert.True(ModelNames.Equivalent("plain", "plain:latest"));
        Assert.False(ModelNames.Equivalent("plain:7b", "plain:latest"));
    }
}
