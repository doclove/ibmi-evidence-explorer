using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure.Ollama;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// F2.1 local AI citation reliability: the packet-specific reply schema
/// (<c>ibmi-explorer-explanation/3</c>) that ties each claim label to the evidence IDs it may cite, the
/// fixed prompt (<c>ibmi-explorer-f2-prompt/3</c>) with source-free examples, and validation that still
/// rejects every rule violation whether or not a runtime enforced the schema. Fake endpoint only.
/// </summary>
public sealed class CitationReliabilityTests : IDisposable
{
    /// <summary>
    /// The JSON Schema keywords the reply schema may use: those Ollama 0.35.0 was observed to enforce
    /// during decoding with synthetic packets (2026-10-14), plus structural ones. <c>if</c>/<c>then</c>
    /// and <c>uniqueItems</c> were observed to be ignored and must not be relied on.
    /// </summary>
    private static readonly HashSet<string> EnforcedKeywords = ["type", "additionalProperties", "required", "properties", "enum", "items", "anyOf", "minItems", "maxItems", "maxLength"];

    private readonly ExplorerEnvironment _f2 = new();
    private readonly SnapshotId _snapshot;
    private readonly ContextPacket _packet;

    public CitationReliabilityTests()
    {
        _snapshot = _f2.Prepare("f2-explorer");
        _packet = ContextPacket.From(_f2.Explore(_snapshot, F2Members.OrdJob));
    }

    public void Dispose() => _f2.Dispose();

    private static DiagnosticCode Code<T>(OperationResult<T> result) => Assert.Single(result.Diagnostics).Code;

    private static JsonObject Branch(JsonNode schema, string label) =>
        schema["properties"]!["claims"]!["items"]!["anyOf"]!.AsArray().Select(b => b!.AsObject())
            .Single(b => (string?)b["properties"]!["label"]!["enum"]![0] == label);

    private static string[] Enum(JsonNode? node) => node is null ? [] : node.AsArray().Select(v => (string)v!).ToArray();

    private string[] FactIds => _packet.Citations.Values.Where(c => c.Kind == CitationKind.Fact).Select(c => c.Id).OrderBy(Number).ToArray();

    private string[] LineIds => _packet.Dossier.Evidence.Select(e => e.Id).ToArray();

    private static int Number(string id) => int.Parse(id.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);

    // ---- Request schema --------------------------------------------------------------------------

    [Fact]
    public void SchemaTiesEachLabelToItsPermittedEvidence()
    {
        var schema = JsonNode.Parse(_packet.OutputSchemaJson)!;
        var properties = schema["properties"]!;
        Assert.Equal([ExplanationFormats.OutputSchemaVersion], Enum(properties["schemaVersion"]!["enum"]));
        Assert.Equal(["answered", "declined"], Enum(properties["status"]!["enum"]));
        Assert.Equal([_snapshot.Value], Enum(properties["snapshotId"]!["enum"]));
        Assert.Equal([_packet.MemberKey], Enum(properties["member"]!["enum"]));
        Assert.Equal(ExplanationFormats.MaxClaims, (int)properties["claims"]!["maxItems"]!);

        var branches = properties["claims"]!["items"]!["anyOf"]!.AsArray();
        Assert.Equal(["observed", "inference", "unknown"], branches.Select(b => (string)b!["properties"]!["label"]!["enum"]![0]!));
        Assert.Equal(["F1", "F2", "F3"], FactIds);
        foreach (var (label, ids, min) in new[] { ("observed", FactIds, 1), ("inference", FactIds.Concat(LineIds).ToArray(), 1), ("unknown", FactIds.Concat(LineIds).ToArray(), 0) })
        {
            var branch = Branch(schema, label);
            Assert.False((bool)branch["additionalProperties"]!);
            Assert.Equal(["text", "evidence", "label"], branch["properties"]!.AsObject().Select(p => p.Key)); // evidence is chosen before the label
            Assert.Equal(["text", "evidence", "label"], Enum(branch["required"]));
            Assert.Equal(ExplanationFormats.MaxClaimChars, (int)branch["properties"]!["text"]!["maxLength"]!);
            var evidence = branch["properties"]!["evidence"]!;
            Assert.Equal(ids, Enum(evidence["items"]!["enum"]));
            Assert.Equal(min, (int?)evidence["minItems"] ?? 0);
            Assert.Equal(ExplanationFormats.MaxEvidencePerClaim, (int)evidence["maxItems"]!);
        }

        Assert.DoesNotContain(Enum(Branch(schema, "observed")["properties"]!["evidence"]!["items"]!["enum"]), id => id.StartsWith('S'));
    }

    [Fact]
    public void SchemaUsesOnlyKeywordsTheRuntimeWasObservedToEnforce()
    {
        var used = new HashSet<string>();
        Collect(JsonNode.Parse(_packet.OutputSchemaJson)!, used, inProperties: false);
        Assert.Subset(EnforcedKeywords, used);
        Assert.DoesNotContain("if", used);
        Assert.DoesNotContain("uniqueItems", used);
        Assert.DoesNotContain("pattern", used);
    }

    private static void Collect(JsonNode node, HashSet<string> keywords, bool inProperties)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (!inProperties)
                {
                    keywords.Add(key);
                }

                if (value is not null)
                {
                    Collect(value, keywords, inProperties: !inProperties && key == "properties");
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.OfType<JsonNode>())
            {
                Collect(item, keywords, inProperties: false);
            }
        }
    }

    [Fact]
    public void SchemaIsDeterministicAndCarriesNoSourceText()
    {
        var again = ContextPacket.From(_f2.Explore(_snapshot, F2Members.OrdJob));
        Assert.Equal(_packet.OutputSchemaJson, again.OutputSchemaJson);
        Canaries.AssertAbsent(_packet.OutputSchemaJson);
        Assert.DoesNotContain(_packet.Dossier.Evidence, e => e.Text.Trim().Length > 3 && _packet.OutputSchemaJson.Contains(e.Text.Trim(), StringComparison.Ordinal));
        Assert.All(_packet.OutputSchemaJson, c => Assert.True(c is >= ' ' and < (char)0x7F));
        Assert.DoesNotContain('<', _packet.OutputSchemaJson);
    }

    [Fact]
    public void ABranchWithNoPermittedIdsIsLeftOut()
    {
        var snapshot = SnapshotId.New();
        var linesOnly = JsonNode.Parse(ExplanationFormats.OutputSchemaFor(snapshot, "L/F/M (CL)", [], ["S1", "S2"]))!;
        Assert.Equal(["inference", "unknown"], linesOnly["properties"]!["claims"]!["items"]!["anyOf"]!.AsArray().Select(b => (string)b!["properties"]!["label"]!["enum"]![0]!));

        var nothing = JsonNode.Parse(ExplanationFormats.OutputSchemaFor(snapshot, "L/F/M (CL)", [], []))!;
        var only = Assert.Single(nothing["properties"]!["claims"]!["items"]!["anyOf"]!.AsArray())!;
        Assert.Equal("unknown", (string)only["properties"]!["label"]!["enum"]![0]!);
        Assert.Equal(0, (int)only["properties"]!["evidence"]!["maxItems"]!);
        Assert.Null(only["properties"]!["evidence"]!["items"]);
    }

    [Fact]
    public void LargestPacketScheduleStaysWithinTheRequestBounds()
    {
        // Upper bound on IDs: 1 definition + 40 + 40 relationships, 64 source lines.
        var facts = Enumerable.Range(1, 1 + (2 * ExplorerLimits.MaxEdgesPerDirection)).Select(i => $"F{i}").ToArray();
        var lines = Enumerable.Range(1 + facts.Length, ExplorerLimits.MaxEvidenceLines).Select(i => $"S{i}").ToArray();
        var schema = ExplanationFormats.OutputSchemaFor(SnapshotId.New(), "ABCDEFGHIJ/ABCDEFGHIJ/ABCDEFGHIJ (RPG)", facts, lines);
        Assert.InRange(Encoding.UTF8.GetByteCount(schema), 1, 5 * 1024);

        // The fan-out fixture fills both edge limits; its whole request must fit the HTTP bound.
        var calls = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"  CALL PGM(T{i:D3})"));
        var targets = Enumerable.Range(0, 60).Select(i => ($"QSRC/T{i:D3}.clle", $"T{i:D3}", "CL", "PGM\nENDPGM\n"u8.ToArray()));
        var callers = Enumerable.Range(0, 45).Select(i => ($"QSRC/C{i:D3}.clle", $"C{i:D3}", "CL", "PGM\n  CALL PGM(HUB)\nENDPGM\n"u8.ToArray()));
        var source = _f2.Env.CreateExport([("QSRC/HUB.clle", "HUB", "CL", Encoding.UTF8.GetBytes($"PGM\n{calls}\nENDPGM\n")), .. targets, .. callers]);
        var hub = ContextPacket.From(_f2.Explore(_f2.PrepareExport(source), Members.Of("TESTLIB", "QSRC", "HUB", SourceType.CL)));
        Assert.Equal(81, hub.Citations.Values.Count(c => c.Kind == CitationKind.Fact));
        var prompt = hub.Prompt();
        Assert.True(prompt.Bytes <= ExplanationFormats.MaxPromptBytes);
        var body = OllamaChatModel.ChatBody(new LocalChatRequest(ExplorerEnvironment.Model, prompt.SystemMessage, prompt.UserMessage, prompt.OutputSchemaJson, ExplanationFormats.MaxOutputTokens));
        Assert.True(body.Length <= OllamaOptions.Default.MaxRequestBytes, $"{body.Length} bytes");
    }

    // ---- Prompt ----------------------------------------------------------------------------------

    [Fact]
    public void SystemMessageIsFixedAndItsExamplesAreSourceFreeAndFollowTheRules()
    {
        var system = _packet.Prompt().SystemMessage;
        var other = ContextPacket.From(_f2.Explore(_snapshot, F2Members.OrdHdr)).Prompt().SystemMessage;
        Assert.Equal(system, other); // no packet data in the higher-priority instructions
        Assert.Contains(ExplanationFormats.PromptTemplateVersion, system, StringComparison.Ordinal);
        Assert.Equal("ibmi-explorer-f2-prompt/3", ExplanationFormats.PromptTemplateVersion);
        Assert.DoesNotContain(_snapshot.Value, system, StringComparison.Ordinal);
        foreach (var name in new[] { "ORDJOB", "ORDPOST", "ORDHDR", "F2LIB", "NOSUCHPGM", Canaries.Prefix })
        {
            Assert.DoesNotContain(name, system, StringComparison.Ordinal);
        }

        var examples = system.Split('\n').Select(l => l.Trim()).Select(l => l.StartsWith("Invalid: ", StringComparison.Ordinal) ? l[9..l.IndexOf('}', StringComparison.Ordinal)] + "}" : l)
            .Where(l => l.StartsWith("{\"text\"", StringComparison.Ordinal)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(4, examples.Count);
        var (observed, inference, unknown, invalid) = (examples[0], examples[1], examples[2], examples[3]);
        Assert.Equal("observed", (string)observed["label"]!);
        Assert.Equal(["F2"], Enum(observed["evidence"]));
        Assert.Equal("inference", (string)inference["label"]!);
        Assert.Contains(Enum(inference["evidence"]), id => id.StartsWith('S'));
        Assert.Equal(("unknown", 0), ((string)unknown["label"]!, unknown["evidence"]!.AsArray().Count));
        Assert.Equal("observed", (string)invalid["label"]!);
        Assert.Equal(["S4"], Enum(invalid["evidence"]));
        Assert.Contains("must be labeled \"inference\"", system, StringComparison.Ordinal);
        Assert.All(examples, e => Assert.Equal(["text", "evidence", "label"], e.AsObject().Select(p => p.Key)));
    }

    [Fact]
    public void UserMessageQuotesTheSchemaOnceBeforeTheDataBlock()
    {
        var prompt = _packet.Prompt();
        Assert.Equal(_packet.OutputSchemaJson, prompt.OutputSchemaJson);
        var schemaAt = prompt.UserMessage.IndexOf(_packet.OutputSchemaJson, StringComparison.Ordinal);
        Assert.True(schemaAt > 0);
        Assert.Equal(schemaAt, prompt.UserMessage.LastIndexOf(_packet.OutputSchemaJson, StringComparison.Ordinal));
        Assert.True(schemaAt < prompt.UserMessage.IndexOf(ExplanationFormats.ContextBegin, StringComparison.Ordinal));
        Assert.EndsWith(ExplanationFormats.ContextEnd, prompt.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChatRequestFormatIsThePacketSchema()
    {
        using var server = FakeOllamaServer.Answering(Replies.Faithful);
        using var client = ExplorerEnvironment.Client(server);
        var result = _f2.Explanations(client).Explain(ExplorerEnvironment.Request(_snapshot, F2Members.OrdJob));
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));

        using var body = JsonDocument.Parse(Assert.Single(server.Chats).Body);
        Assert.Equal(JsonNode.Parse(_packet.OutputSchemaJson)!.ToJsonString(), JsonNode.Parse(body.RootElement.GetProperty("format").GetRawText())!.ToJsonString());
        Assert.Contains(_packet.OutputSchemaJson, FakeOllamaServer.Messages(Assert.Single(server.Chats)).User, StringComparison.Ordinal);
        Assert.Equal(("ibmi-explorer-f2-prompt/3", "ibmi-explorer-explanation/3"), (result.Value.Draft.Provenance.PromptTemplateVersion, result.Value.Draft.Provenance.OutputSchemaVersion));
    }

    // ---- Replies through the fake endpoint --------------------------------------------------------

    private OperationResult<ExplanationOutcome> Explain(Func<RecordedRequest, string> reply, MemberIdentity? member = null, SnapshotId? snapshot = null)
    {
        using var server = FakeOllamaServer.Answering(reply);
        using var client = ExplorerEnvironment.Client(server);
        return _f2.Explanations(client).Explain(ExplorerEnvironment.Request(snapshot ?? _snapshot, member ?? F2Members.OrdJob));
    }

    /// <summary>A reply in the schema's property order (text, evidence, label).</summary>
    private static string Ordered(RecordedRequest request, params (string Label, string Text, string[] Evidence)[] claims)
    {
        var packet = Replies.Packet(request);
        return new JsonObject
        {
            ["schemaVersion"] = ExplanationFormats.OutputSchemaVersion,
            ["status"] = "answered",
            ["snapshotId"] = (string?)packet["snapshotId"],
            ["member"] = (string?)packet["member"],
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)new JsonObject
            {
                ["text"] = c.Text,
                ["evidence"] = new JsonArray(c.Evidence.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
                ["label"] = c.Label,
            }).ToArray()),
        }.ToJsonString();
    }

    [Fact]
    public void ValidLabeledRepliesProduceADraft()
    {
        var result = Explain(r => Ordered(r,
            ("observed", "Calls the resolved target recorded as F2.", ["F2"]),
            ("inference", "The call appears to pass one parameter.", ["S6", "F2"]),
            ("unknown", "The unresolved target's definition is not in the packet.", [])));

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        var draft = result.Value.Draft;
        Assert.Equal([ClaimLabel.Observed, ClaimLabel.Inference], draft.Findings.Select(c => c.Label));
        Assert.Equal([CitationKind.Fact], draft.Findings[0].Evidence.Select(e => e.Kind));
        Assert.Equal([CitationKind.SourceLine, CitationKind.Fact], draft.Findings[1].Evidence.Select(e => e.Kind));
        Assert.Single(draft.Unknowns);
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("F2,S6")]
    [InlineData("S6,F2")]
    public void ObservedCitingASourceLineIsRejectedNotRelabeled(string evidence)
    {
        var text = "FIXTURE-CANARY-REPLY observed claim";
        var result = Explain(r => Ordered(r, ("inference", "A valid inference.", ["S6"]), ("observed", text, evidence.Split(','))));

        Assert.False(result.Succeeded);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCode.LocalAiCitationInvalid, diagnostic.Code);
        Assert.Contains("Claim 2 is labeled observed but cites a source line", diagnostic.Message, StringComparison.Ordinal);
        Canaries.AssertAbsent(diagnostic.Message);
        Canaries.AssertAbsent(_f2.Log.AllText());
    }

    [Theory]
    [InlineData("observed", "F99")] // beyond the packet's facts
    [InlineData("inference", "S999")] // beyond the packet's lines
    [InlineData("observed", "F01")] // right form, not a packet ID
    [InlineData("observed", "f1")]
    [InlineData("inference", "S1 ")]
    [InlineData("inference", "X1")]
    public void FabricatedIdsAreRejected(string label, string id) =>
        Assert.Equal(DiagnosticCode.LocalAiCitationInvalid, Code(Explain(r => Ordered(r, ("observed", "Calls F2.", ["F2"]), (label, "Invented citation.", [id])))));

    [Fact]
    public void AnIdFromAnotherPacketIsRejected()
    {
        // ORDHDR's packet has F4; ORDJOB's has only F1-F3.
        Assert.Contains("F4", ContextPacket.From(_f2.Explore(_snapshot, F2Members.OrdHdr)).Citations.Keys);
        Assert.Equal(DiagnosticCode.LocalAiCitationInvalid, Code(Explain(r => Ordered(r, ("observed", "Uses a file.", ["F4"])))));
    }

    [Fact]
    public void PartialCoverageLimitsCitationsToTheDisplayedFacts()
    {
        // A member with no statements has no definition or edges: the schema offers no observed branch,
        // and an observed claim (any citation) is still rejected by the validator.
        var source = _f2.Env.CreateExport([("QSRC/NOTES.clle", "NOTES", "CL", "/* Synthetic comment only. */\n/* Second comment. */\n"u8.ToArray())]);
        var snapshot = _f2.PrepareExport(source);
        var member = Members.Of("TESTLIB", "QSRC", "NOTES", SourceType.CL);
        var packet = ContextPacket.From(_f2.Explore(snapshot, member));
        Assert.Equal(DossierCompleteness.Partial, packet.Dossier.Completeness);
        Assert.DoesNotContain(packet.Citations.Values, c => c.Kind == CitationKind.Fact);
        Assert.Equal(["inference", "unknown"], JsonNode.Parse(packet.OutputSchemaJson)!["properties"]!["claims"]!["items"]!["anyOf"]!.AsArray()
            .Select(b => (string)b!["properties"]!["label"]!["enum"]![0]!));

        Assert.Equal(DiagnosticCode.LocalAiCitationInvalid, Code(Explain(r => Ordered(r, ("observed", "Has a definition.", ["F1"])), member, snapshot)));
        Assert.Equal(DiagnosticCode.LocalAiCitationInvalid, Code(Explain(r => Ordered(r, ("observed", "Line one.", ["S1"])), member, snapshot)));

        var accepted = Explain(r => Ordered(r, ("inference", "The member appears to contain only comments.", ["S1"]), ("unknown", "No statements were parsed.", [])), member, snapshot);
        Assert.True(accepted.Succeeded, string.Join("; ", accepted.Diagnostics));
        Assert.Equal(DossierCompleteness.Partial, accepted.Value.Draft.Completeness);
        Assert.Contains(accepted.Value.Draft.DossierGaps, g => g.Code is "member-unsupported" or "no-definition");
    }

    [Theory]
    [InlineData("declined-empty", DiagnosticCode.LocalAiRefused)]
    [InlineData("declined-with-claims", DiagnosticCode.LocalAiRefused)]
    [InlineData("prose-refusal", DiagnosticCode.LocalAiInvalidOutput)]
    [InlineData("previous-schema", DiagnosticCode.LocalAiInvalidOutput)]
    public void RefusalsAndOldSchemaRepliesShowNoDraft(string variant, DiagnosticCode expected)
    {
        var result = Explain(r => variant switch
        {
            "declined-empty" => Replies.Build(r, "declined", []),
            "declined-with-claims" => Replies.Build(r, "declined", [("observed", "Calls F2.", ["F2"])]),
            "prose-refusal" => "I'm sorry, I can't help with that.",
            _ => Replies.Build(r, "answered", [("observed", "Calls F2.", ["F2"])], o => o["schemaVersion"] = "ibmi-explorer-explanation/2"),
        });
        Assert.Equal(expected, Code(result));
    }
}
