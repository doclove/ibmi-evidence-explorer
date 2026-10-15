using System.Text.Json;
using System.Text.Json.Nodes;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F2 items 4-5: the bounded packet, the fixed prompt, and strict validation of model replies.</summary>
public sealed class ExplanationValidatorTests : IDisposable
{
    private readonly ExplorerEnvironment _f2 = new();
    private readonly ContextPacket _packet;

    public ExplanationValidatorTests()
    {
        var snapshot = _f2.Prepare("f2-explorer");
        _packet = ContextPacket.From(_f2.Explore(snapshot, F2Members.OrdJob));
    }

    public void Dispose() => _f2.Dispose();

    private JsonObject Reply(params (string Label, string Text, string[] Evidence)[] claims) => new()
    {
        ["schemaVersion"] = ExplanationFormats.OutputSchemaVersion,
        ["status"] = "answered",
        ["snapshotId"] = _packet.Dossier.SnapshotId.Value,
        ["member"] = _packet.MemberKey,
        ["claims"] = new JsonArray(claims.Select(c => (JsonNode)new JsonObject
        {
            ["label"] = c.Label,
            ["text"] = c.Text,
            ["evidence"] = new JsonArray(c.Evidence.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
        }).ToArray()),
    };

    private OperationResult<ParsedExplanation> Validate(JsonNode reply) => ExplanationValidator.Validate(reply.ToJsonString(), _packet);

    private static DiagnosticCode Code<T>(OperationResult<T> result) => Assert.Single(result.Diagnostics).Code;

    // ---- Packet ---------------------------------------------------------------------------------

    [Fact]
    public void PacketIsDeterministicAndCarriesOnlySelectedEvidence()
    {
        var again = ContextPacket.From(_f2.Explore(_packet.Dossier.SnapshotId, F2Members.OrdJob));
        Assert.Equal(_packet.Json, again.Json);
        Assert.Equal(_packet.Sha256, again.Sha256);
        Assert.Equal(_packet.Prompt(), again.Prompt());
        Assert.DoesNotContain('\r', _packet.Json); // identical bytes and hash on every platform
        Assert.DoesNotContain('\r', _packet.Prompt().SystemMessage + _packet.Prompt().UserMessage);

        using var document = JsonDocument.Parse(_packet.Json);
        var root = document.RootElement;
        Assert.Equal(ExplanationFormats.PacketFormat, root.GetProperty("packetFormat").GetString());
        Assert.Equal(_packet.Dossier.Evidence.Select(e => e.Id), root.GetProperty("sourceLines").EnumerateArray().Select(l => l.GetProperty("id").GetString()));
        Assert.Equal(_packet.Dossier.Evidence.Select(e => e.Text), root.GetProperty("sourceLines").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
        Assert.Equal(["F1", "F2", "F3"], root.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("id").GetString()));
        Assert.Equal("partial", root.GetProperty("completeness").GetString());
        Assert.Contains(root.GetProperty("gaps").EnumerateArray(), g => g.GetString()!.Contains("target-not-in-snapshot", StringComparison.Ordinal));
        Assert.True(_packet.SourceBytes <= ExplorerLimits.MaxEvidenceBytes && _packet.SourceLines <= ExplorerLimits.MaxEvidenceLines);
    }

    [Fact]
    public void AdversarialCommentsStayQuotedInsideTheDataBlock()
    {
        var prompt = _packet.Prompt();

        // The fixture's comment contains the literal end delimiter; the packet escapes '<' and '>'.
        Assert.Equal(1, Count(prompt.UserMessage, ExplanationFormats.ContextBegin));
        Assert.Equal(1, Count(prompt.UserMessage, ExplanationFormats.ContextEnd));
        Assert.EndsWith(ExplanationFormats.ContextEnd, prompt.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("IGNORE ALL PREVIOUS", prompt.SystemMessage, StringComparison.Ordinal);
        Assert.Contains("IGNORE ALL PREVIOUS INSTRUCTIONS", prompt.UserMessage, StringComparison.Ordinal);
        Assert.StartsWith("You are the IBM i Evidence Explorer local explanation assistant", prompt.SystemMessage, StringComparison.Ordinal);
        Assert.All(prompt.UserMessage.Split('\n'), line => Assert.DoesNotContain(line, c => char.IsControl(c)));
        Assert.All(prompt.UserMessage, c => Assert.True(c < 0x80, "packet text is ASCII-escaped"));
        Assert.True(prompt.Bytes <= ExplanationFormats.MaxPromptBytes);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ---- Validation: accepted ------------------------------------------------------------------

    [Fact]
    public void ValidReplyMapsCitationsToPacketEvidence()
    {
        var result = Validate(Reply(
            ("observed", "Calls ORDPOST.", ["F2"]),
            ("inference", "Line 2 suggests a prompt-injection attempt in a comment.", ["S2", "F1"]),
            ("unknown", "The runtime library list is not in the packet.", [])));

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        var claims = result.Value.Claims;
        Assert.Equal([ClaimLabel.Observed, ClaimLabel.Inference, ClaimLabel.Unknown], claims.Select(c => c.Label));
        var observed = Assert.Single(claims[0].Evidence);
        Assert.Equal(CitationKind.Fact, observed.Kind);
        Assert.Equal(_packet.Dossier.Outgoing[0].Edge.ReferenceEvidence, observed.References[0]);
        Assert.Equal(_packet.Dossier.Evidence.Single(e => e.Id == "S2").Reference, claims[1].Evidence[0].References.Single());
    }

    // ---- Validation: rejected ------------------------------------------------------------------

    public static TheoryData<string, DiagnosticCode> Rejections => new()
    {
        { "invented-id", DiagnosticCode.LocalAiCitationInvalid },
        { "invented-line-id", DiagnosticCode.LocalAiCitationInvalid },
        { "observed-cites-line", DiagnosticCode.LocalAiCitationInvalid },
        { "uncited-observed", DiagnosticCode.LocalAiCitationInvalid },
        { "uncited-inference", DiagnosticCode.LocalAiCitationInvalid },
        { "wrong-snapshot", DiagnosticCode.LocalAiIdentityMismatch },
        { "wrong-member", DiagnosticCode.LocalAiIdentityMismatch },
        { "summary-field", DiagnosticCode.LocalAiInvalidOutput },
        { "long-claim", DiagnosticCode.LocalAiInvalidOutput },
        { "gaps-field", DiagnosticCode.LocalAiInvalidOutput },
        { "too-many-claims", DiagnosticCode.LocalAiInvalidOutput },
        { "too-many-ids", DiagnosticCode.LocalAiInvalidOutput },
        { "repeated-id", DiagnosticCode.LocalAiInvalidOutput },
        { "no-claims", DiagnosticCode.LocalAiInvalidOutput },
        { "bad-label", DiagnosticCode.LocalAiInvalidOutput },
        { "extra-property", DiagnosticCode.LocalAiInvalidOutput },
        { "extra-claim-property", DiagnosticCode.LocalAiInvalidOutput },
        { "missing-claims", DiagnosticCode.LocalAiInvalidOutput },
        { "wrong-schema", DiagnosticCode.LocalAiInvalidOutput },
        { "numeric-evidence", DiagnosticCode.LocalAiInvalidOutput },
        { "declined", DiagnosticCode.LocalAiRefused },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void InvalidRepliesAreRejectedWhole(string variant, DiagnosticCode expected)
    {
        var reply = Reply(("observed", "Calls ORDPOST.", ["F2"]));
        var claim = reply["claims"]![0]!.AsObject();
        switch (variant)
        {
            case "invented-id": claim["evidence"] = new JsonArray("F99"); break;
            case "invented-line-id": reply["claims"]!.AsArray().Add(Claim("inference", "x", "S999")); break;
            case "observed-cites-line": claim["evidence"] = new JsonArray("S1"); break;
            case "uncited-observed": claim["evidence"] = new JsonArray(); break;
            case "uncited-inference": reply["claims"]!.AsArray().Add(Claim("inference", "x")); break;
            case "wrong-snapshot": reply["snapshotId"] = "snap-" + new string('0', 32); break;
            case "wrong-member": reply["member"] = "F2LIB/QRPGLESRC/ORDPOST (RPG)"; break;
            case "summary-field": reply["summary"] = "It deletes PAYROLL."; break;
            case "long-claim": claim["text"] = new string('c', ExplanationFormats.MaxClaimChars + 1); break;
            case "gaps-field": reply["gaps"] = new JsonArray("It also writes AUDITLOG."); break;
            case "too-many-claims":
                for (var i = 0; i < ExplanationFormats.MaxClaims; i++)
                {
                    reply["claims"]!.AsArray().Add(Claim("unknown", "x"));
                }

                break;
            case "too-many-ids": claim["evidence"] = new JsonArray(Enumerable.Range(0, 9).Select(_ => (JsonNode)"F1").ToArray()); break;
            case "repeated-id": claim["evidence"] = new JsonArray("F1", "F1"); break;
            case "no-claims": reply["claims"] = new JsonArray(); break;
            case "bad-label": claim["label"] = "proposal"; break;
            case "extra-property": reply["command"] = "rm -rf /"; break;
            case "extra-claim-property": claim["confidence"] = 1; break;
            case "missing-claims": reply.Remove("claims"); break;
            case "wrong-schema": reply["schemaVersion"] = "ibmi-explorer-explanation/1"; break;
            case "numeric-evidence": claim["evidence"] = new JsonArray(2); break;
            case "declined": reply["status"] = "declined"; reply["claims"] = new JsonArray(); break;
        }

        Assert.Equal(expected, Code(Validate(reply)));
    }

    private static JsonObject Claim(string label, string text, params string[] evidence) =>
        new() { ["label"] = label, ["text"] = text, ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()) };

    [Theory]
    [InlineData("not json at all")]
    [InlineData("I'm sorry, I can't help with that.")]
    [InlineData("{\"schemaVersion\":")]
    [InlineData("[]")]
    [InlineData("{} {}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("// comment\n{}")]
    public void MalformedJsonAndRefusalProseAreInvalidOutput(string content) =>
        Assert.Equal(DiagnosticCode.LocalAiInvalidOutput, Code(ExplanationValidator.Validate(content, _packet)));

    [Fact]
    public void DuplicatePropertiesCannotSmuggleASecondIdentity()
    {
        var text = Reply(("observed", "Calls ORDPOST.", ["F2"])).ToJsonString();
        var duplicated = text.Replace("\"status\":", "\"snapshotId\":\"snap-00000000000000000000000000000000\",\"status\":", StringComparison.Ordinal);
        Assert.Equal(DiagnosticCode.LocalAiInvalidOutput, Code(ExplanationValidator.Validate(duplicated, _packet)));
    }

    [Fact]
    public void OversizedContentIsRejectedBeforeParsing()
    {
        var content = "{\"x\":\"" + new string('a', ExplanationValidator.MaxContentChars) + "\"}";
        Assert.Equal(DiagnosticCode.LocalAiInvalidOutput, Code(ExplanationValidator.Validate(content, _packet)));
    }

    [Fact]
    public void DiagnosticsNeverQuoteTheReply()
    {
        var reply = Reply(("observed", "FIXTURE-CANARY-REPLY secret text", ["F99"]));
        var result = Validate(reply);
        Canaries.AssertAbsent(string.Join("\n", result.Diagnostics));
        Assert.Equal(DiagnosticCode.LocalAiCitationInvalid, Code(result));
    }
}
