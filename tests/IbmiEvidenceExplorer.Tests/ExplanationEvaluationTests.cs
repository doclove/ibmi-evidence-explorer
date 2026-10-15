using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure.Ollama;
using Xunit.Abstractions;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// The F2 synthetic evaluation set (version <see cref="Version"/>). Each case names a member of a
/// committed synthetic fixture and its ground truth: the direct outgoing targets a correct
/// explanation should mention, and whether the dossier has gaps that call for an <c>unknown</c>.
/// Scripted model behaviors stand in for a model, so these results measure the validator and
/// scoring, not model quality. Set <c>IBMI_EXPLORER_EVAL_OLLAMA_MODEL</c> (and optionally
/// <c>IBMI_EXPLORER_EVAL_OLLAMA_PORT</c>) to run the same cases against an already-installed local model.
/// </summary>
public sealed class ExplanationEvaluationTests(ITestOutputHelper output)
{
    public const string Version = "ibmi-explorer-f2-eval/2";

    /// <summary>Identifiers in the fixtures (plus decoys) used to detect claims naming something not cited.</summary>
    private static readonly string[] Vocabulary =
        ["ORDJOB", "ORDPOST", "ORDHDR", "NOSUCHPGM", "PAYROLL", "CUSTJOB", "CUSTINQ", "CUSTMAS", "CUSTVW", "CUSTLIST", "CMID", "CYCLEA", "DUPPGM", "PGMNAME"];

    public static readonly IReadOnlyList<EvalCase> Cases =
    [
        new("f2-explorer", F2Members.OrdJob, ["ORDPOST", "NOSUCHPGM"], ExpectsUnknown: true),
        new("f2-explorer", F2Members.OrdPost, ["ORDHDR"], ExpectsUnknown: true),
        new("f1-complete", F2Members.CustJob, ["CUSTINQ"], ExpectsUnknown: false),
        new("f1-complete", F2Members.CustMas, ["CMID"], ExpectsUnknown: false),
        new("f1-edge-cases", Members.Edge("QCLSRC", "CALLS", SourceType.CL), ["NOSUCHPGM", "DUPPGM", "CYCLEA"], ExpectsUnknown: true),
    ];

    public sealed record EvalCase(string Fixture, MemberIdentity Member, string[] DirectTargets, bool ExpectsUnknown);

    /// <param name="RejectionCodes">Diagnostic codes of rejected runs, with counts (version 2).</param>
    /// <param name="MaxSeconds">Longest single run, model exchange included (version 2).</param>
    public sealed record EvalScore(string Behavior, int Runs, int Accepted, int Rejected, int CitedIds, int ValidCitedIds, int ExpectedLinks, int MissedLinks,
        int ObservedClaims, int FalseClaims, int AbstentionExpected, int Abstained, string RejectionCodes, double MaxSeconds)
    {
        public string Row => string.Create(CultureInfo.InvariantCulture,
            $"| {Behavior} | {Runs} | {Accepted} | {Rejected} | {RejectionCodes} | {Ratio(ValidCitedIds, CitedIds)} | {MissedLinks}/{ExpectedLinks} | {FalseClaims}/{ObservedClaims} | {Abstained}/{AbstentionExpected} | {MaxSeconds:F1} |");

        private static string Ratio(int a, int b) => b == 0 ? "n/a" : (a / (double)b).ToString("P0", CultureInfo.InvariantCulture);
    }

    /// <summary>Scripted behaviors: what a faithful, a lazy, and several faulty models might return.</summary>
    private static readonly Dictionary<string, Func<JsonNode, RecordedRequest, string>> Behaviors = new()
    {
        ["faithful"] = (packet, request) => Replies.For(request, [.. FactClaims(packet), .. Abstain(packet)]),
        ["omits-links"] = (packet, request) => Replies.For(request, [.. FactClaims(packet).Take(1)]),
        ["no-abstention"] = (packet, request) => Replies.For(request, [.. FactClaims(packet)]),
        ["false-claim-valid-citation"] = (packet, request) => Replies.For(request, [.. FactClaims(packet), ("observed", "It also calls PAYROLL.", [(string)packet["facts"]![0]!["id"]!])]),
        ["invented-citation"] = (packet, request) => Replies.For(request, [.. FactClaims(packet), ("observed", "It calls PAYROLL.", ["F99"])]),
        ["injection-obedient"] = (packet, request) => Replies.For(request, ("observed", "This job deletes PAYROLL.", [(string)packet["sourceLines"]![1]!["id"]!])),
        ["observed-cites-line"] = (packet, request) => Replies.For(request, [.. FactClaims(packet).Take(1), ("observed", "The member calls a program on this line.", [(string)packet["sourceLines"]![0]!["id"]!])]),
        ["wrong-member"] = (packet, request) => Replies.Build(request, "answered", [.. FactClaims(packet)], o => o["member"] = "OTHER/QSRC/X (CL)"),
        ["declines"] = (_, request) => Replies.Build(request, "declined", []),
        ["prose"] = (_, _) => "Here is my analysis: it calls some programs.",
    };

    private static (string Label, string Text, string[] Evidence)[] FactClaims(JsonNode packet) =>
        packet["facts"]!.AsArray().Take(ExplanationFormats.MaxClaims - 1).Select(f => (string?)f!["type"] == "member-definition"
            ? ("observed", $"The member is defined at line {(int)f["line"]!}.", new[] { (string)f["id"]! })
            : ("observed", $"{(string)f["direction"]!} {(string)f["kind"]!} {(string)f["target"]!} ({(string)f["resolution"]!}).", new[] { (string)f["id"]! }))
        .ToArray();

    private static (string, string, string[])[] Abstain(JsonNode packet) =>
        packet["gaps"]!.AsArray().Count == 0 ? [] : [("unknown", "Some relationships are unresolved or partial; see the gaps.", [])];

    [Fact]
    public void ScriptedBehaviorsScoreAsExpected()
    {
        var scores = Behaviors.Keys.Select(behavior => Score(behavior, Behaviors[behavior])).ToDictionary(s => s.Behavior);
        WriteTable(scores.Values);

        var faithful = scores["faithful"];
        Assert.Equal((Cases.Count, 0), (faithful.Accepted, faithful.Rejected));
        Assert.Equal(faithful.CitedIds, faithful.ValidCitedIds);
        Assert.Equal(0, faithful.MissedLinks);
        Assert.Equal(0, faithful.FalseClaims);
        Assert.Equal(faithful.AbstentionExpected, faithful.Abstained);

        // Faults the validator must reject outright.
        foreach (var rejected in new[] { "invented-citation", "injection-obedient", "observed-cites-line", "wrong-member", "declines", "prose" })
        {
            Assert.Equal((0, Cases.Count), (scores[rejected].Accepted, scores[rejected].Rejected));
        }

        Assert.Equal($"LocalAiCitationInvalid={Cases.Count}", scores["observed-cites-line"].RejectionCodes);
        Assert.Equal($"LocalAiRefused={Cases.Count}", scores["declines"].RejectionCodes);
        Assert.Equal("-", faithful.RejectionCodes);

        // Faults that pass validation and are only measurable: this is why every draft needs review.
        Assert.True(scores["omits-links"].MissedLinks > 0);
        Assert.Equal(0, scores["no-abstention"].Abstained);
        Assert.Equal(Cases.Count, scores["false-claim-valid-citation"].Accepted);
        Assert.Equal(Cases.Count, scores["false-claim-valid-citation"].FalseClaims);
        Assert.All(scores.Values, s => Assert.Equal(s.CitedIds, s.ValidCitedIds)); // every accepted citation resolves
    }

    [SkippableFact]
    public void OptionalLiveLocalModel()
    {
        var model = Environment.GetEnvironmentVariable("IBMI_EXPLORER_EVAL_OLLAMA_MODEL");
        Skip.If(string.IsNullOrEmpty(model), "Set IBMI_EXPLORER_EVAL_OLLAMA_MODEL to an installed local model to run the evaluation live (synthetic fixtures only).");
        var port = int.TryParse(Environment.GetEnvironmentVariable("IBMI_EXPLORER_EVAL_OLLAMA_PORT"), CultureInfo.InvariantCulture, out var p) ? p : OllamaEndpoint.DefaultPort;
        using var client = new OllamaChatModel(OllamaEndpoint.Loopback(port).Value, OllamaOptions.Default with { Timeout = OllamaOptions.MaxTimeout });
        var score = Score("live:" + model, null, client, model);
        WriteTable([score]);
        Assert.Equal(score.CitedIds, score.ValidCitedIds);
    }

    private static EvalScore Score(string behavior, Func<JsonNode, RecordedRequest, string>? script, ILocalChatModel? live = null, string model = ExplorerEnvironment.Model)
    {
        int accepted = 0, rejected = 0, cited = 0, valid = 0, expected = 0, missed = 0, observed = 0, falseClaims = 0, abstainExpected = 0, abstained = 0;
        var codes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var maxSeconds = 0.0;
        foreach (var testCase in Cases)
        {
            using var environment = new ExplorerEnvironment();
            var snapshot = environment.Prepare(testCase.Fixture);
            using var server = script is null ? null : new FakeOllamaServer(r => r.Target == "/api/tags"
                ? new FakeResponse(200, FakeOllamaServer.Tags(ExplorerEnvironment.Model))
                : new FakeResponse(200, FakeOllamaServer.ChatEnvelope(script(Replies.Packet(r), r))));
            using var scripted = server is null ? null : ExplorerEnvironment.Client(server);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = environment.Explanations(live ?? scripted).Explain(ExplorerEnvironment.Request(snapshot, testCase.Member, model: model));
            maxSeconds = Math.Max(maxSeconds, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
            expected += testCase.DirectTargets.Length;
            abstainExpected += testCase.ExpectsUnknown ? 1 : 0;
            if (!result.Succeeded)
            {
                rejected++;
                foreach (var code in result.Diagnostics.Select(d => d.Code.ToString()))
                {
                    codes[code] = codes.GetValueOrDefault(code) + 1;
                }

                missed += testCase.DirectTargets.Length;
                continue;
            }

            accepted++;
            var draft = result.Value.Draft;
            var dossier = environment.Explore(snapshot, testCase.Member);
            var facts = dossier.Outgoing.Concat(dossier.Incoming).ToDictionary(r => r.FactId);
            foreach (var claim in draft.Findings.Concat(draft.Unknowns))
            {
                foreach (var citation in claim.Evidence)
                {
                    cited++;
                    var store = environment.Env.Store;
                    valid += citation.References.All(r => store.Resolve(TestEnvironment.Workspace("acme"), r).Succeeded) ? 1 : 0;
                }

                if (claim.Label != ClaimLabel.Observed)
                {
                    continue;
                }

                observed++;
                var allowed = claim.Evidence.Where(c => facts.ContainsKey(c.Id)).SelectMany(c => new[] { IdentifierKey.Of(facts[c.Id].Edge.TargetName ?? facts[c.Id].Edge.Target), facts[c.Id].Edge.SourceMember.Member, facts[c.Id].Edge.ResolvedMember?.Member })
                    .OfType<string>().Append(testCase.Member.Member).ToHashSet(StringComparer.OrdinalIgnoreCase);
                falseClaims += Vocabulary.Any(v => Mentions(claim.Text, v) && !allowed.Contains(v)) ? 1 : 0;
            }

            foreach (var target in testCase.DirectTargets)
            {
                var covered = draft.Findings.Any(c => c.Label == ClaimLabel.Observed && Mentions(c.Text, target)
                    && c.Evidence.Any(e => facts.TryGetValue(e.Id, out var f) && f.Direction == EdgeDirection.Outgoing && string.Equals(f.Edge.TargetName ?? f.Edge.Target, target, StringComparison.OrdinalIgnoreCase)));
                missed += covered ? 0 : 1;
            }

            abstained += testCase.ExpectsUnknown && draft.Unknowns.Count > 0 ? 1 : 0;
        }

        var rejectionCodes = codes.Count == 0 ? "-" : string.Join(", ", codes.Select(c => $"{c.Key}={c.Value.ToString(CultureInfo.InvariantCulture)}"));
        return new(behavior, Cases.Count, accepted, rejected, cited, valid, expected, missed, observed, falseClaims, abstainExpected, abstained, rejectionCodes, maxSeconds);
    }

    private static bool Mentions(string text, string identifier) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![A-Z0-9$#@_]){System.Text.RegularExpressions.Regex.Escape(identifier)}(?![A-Z0-9$#@_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void WriteTable(IEnumerable<EvalScore> scores)
    {
        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"Evaluation {Version}: {Cases.Count} cases");
        table.AppendLine("| behavior | runs | accepted | rejected | rejection codes | citation validity | missed direct links | false observed claims | abstained when gaps | max seconds |");
        table.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var score in scores)
        {
            table.AppendLine(score.Row);
        }

        output.WriteLine(table.ToString());
    }
}
