using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Infrastructure.Ollama;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>An <see cref="IndexEnvironment"/> with the F2 explorer and explanation services over the same storage.</summary>
public sealed class ExplorerEnvironment : IDisposable
{
    public const string Model = "local-test:1b";

    /// <summary>A reply schema for a small synthetic packet, for transport tests that need a valid <c>format</c>.</summary>
    public static readonly string SampleSchema =
        ExplanationFormats.OutputSchemaFor(SnapshotId.New(), "F2LIB/QCLSRC/ORDJOB (CL)", ["F1", "F2"], ["S1", "S2"]);

    public ExplorerEnvironment() => Index = new IndexEnvironment();

    public IndexEnvironment Index { get; }

    public TestEnvironment Env => Index.Env;

    public CapturingLog Log => Env.Log;

    public ExplorerService Explorer(ISnapshotStore? snapshots = null) => new(snapshots ?? Env.Store, Index.IndexStore, Log);

    public ExplanationService Explanations(ILocalChatModel? model) => new(Explorer(), Env.Store, model, Log);

    /// <summary>Imports and indexes a committed fixture; returns its snapshot ID.</summary>
    public SnapshotId Prepare(string fixture, string workspace = "acme")
    {
        var snapshot = Index.Import(workspace, fixture);
        Index.BuildIndex(workspace, snapshot.SnapshotId);
        return snapshot.SnapshotId;
    }

    /// <summary>Imports and indexes a generated export (library TESTLIB, source file QSRC).</summary>
    public SnapshotId PrepareExport(string source, string workspace = "acme")
    {
        var imported = Env.Import(workspace, source);
        Assert.True(imported.Succeeded, string.Join("; ", imported.Diagnostics));
        Index.BuildIndex(workspace, imported.Value.Snapshot.SnapshotId);
        return imported.Value.Snapshot.SnapshotId;
    }

    public MemberDossier Explore(SnapshotId snapshot, MemberIdentity member, string workspace = "acme")
    {
        var result = Explorer().Explore(TestEnvironment.Workspace(workspace), snapshot, member);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        return result.Value;
    }

    public static OllamaChatModel Client(FakeOllamaServer server, TimeSpan? timeout = null) =>
        new(OllamaEndpoint.Loopback(server.Port).Value, OllamaOptions.Default with { Timeout = timeout ?? TimeSpan.FromSeconds(30) });

    public static ExplainRequest Request(SnapshotId snapshot, MemberIdentity member, bool acknowledged = true, string workspace = "acme", string model = Model) =>
        new(TestEnvironment.Workspace(workspace), snapshot, member, model, acknowledged);

    public void Dispose() => Index.Dispose();
}

public static class F2Members
{
    public static readonly MemberIdentity OrdJob = Members.Of("F2LIB", "QCLSRC", "ORDJOB", SourceType.CL);
    public static readonly MemberIdentity OrdPost = Members.Of("F2LIB", "QRPGLESRC", "ORDPOST", SourceType.RPG);
    public static readonly MemberIdentity OrdHdr = Members.Of("F2LIB", "QDDSSRC", "ORDHDR", SourceType.DDS);
    public static readonly MemberIdentity CustJob = Members.Of("DEMOLIB", "QCLSRC", "CUSTJOB", SourceType.CL);
    public static readonly MemberIdentity CustInq = Members.Of("DEMOLIB", "QRPGLESRC", "CUSTINQ", SourceType.RPG);
    public static readonly MemberIdentity CustMas = Members.Of("DEMOLIB", "QDDSSRC", "CUSTMAS", SourceType.DDS);
}

/// <summary>Builds model replies from the packet a fake server actually received.</summary>
public static partial class Replies
{
    /// <summary>The packet JSON between the context delimiters of a recorded chat request.</summary>
    public static JsonNode Packet(RecordedRequest request)
    {
        var (_, user) = FakeOllamaServer.Messages(request);
        var start = user.IndexOf(ExplanationFormats.ContextBegin, StringComparison.Ordinal) + ExplanationFormats.ContextBegin.Length;
        var end = user.LastIndexOf(ExplanationFormats.ContextEnd, StringComparison.Ordinal);
        return JsonNode.Parse(user[start..end])!;
    }

    /// <summary>A reply echoing the packet identity, with the given claims (label, text, evidence IDs).</summary>
    public static string For(RecordedRequest request, params (string Label, string Text, string[] Evidence)[] claims) =>
        Build(request, "answered", claims);

    public static string Build(RecordedRequest request, string status, (string Label, string Text, string[] Evidence)[] claims, Action<JsonObject>? edit = null)
    {
        var packet = Packet(request);
        var reply = new JsonObject
        {
            ["schemaVersion"] = ExplanationFormats.OutputSchemaVersion,
            ["status"] = status,
            ["snapshotId"] = (string?)packet["snapshotId"],
            ["member"] = (string?)packet["member"],
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)new JsonObject
            {
                ["label"] = c.Label,
                ["text"] = c.Text,
                ["evidence"] = new JsonArray(c.Evidence.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
            }).ToArray()),
        };
        edit?.Invoke(reply);
        return reply.ToJsonString();
    }

    /// <summary>A well-formed reply: an observed claim for every fact and one inference over the first source line.</summary>
    public static string Faithful(RecordedRequest request)
    {
        var packet = Packet(request);
        var claims = packet["facts"]!.AsArray().Take(ExplanationFormats.MaxClaims - 1)
            .Select(f => ("observed", $"Indexed fact {(string?)f!["id"]}.", new[] { (string)f["id"]! }))
            .Append(("inference", "The first line appears to be a descriptive comment.", new[] { (string)packet["sourceLines"]![0]!["id"]! }))
            .ToArray();
        return For(request, claims);
    }

    [GeneratedRegex("\"(F|S)[0-9]+\"", RegexOptions.CultureInvariant)]
    public static partial Regex CitationId();
}

/// <summary>Edits published index files while keeping their internal hashes consistent.</summary>
public static class IndexTampering
{
    /// <summary>
    /// Publishes a forged copy of the latest index as a new, newer run through the real store, so
    /// every file hash, count, and marker is internally consistent; only the content is false.
    /// </summary>
    public static void Republish(IndexEnvironment index, string workspace, SnapshotId snapshotId,
        Func<IReadOnlyList<IndexedMember>, IReadOnlyList<IndexedMember>>? members = null,
        Func<IReadOnlyList<IndexedArtifact>, IReadOnlyList<IndexedArtifact>>? artifacts = null,
        Func<IReadOnlyList<DependencyEdge>, IReadOnlyList<DependencyEdge>>? edges = null,
        IndexLimits? limits = null)
    {
        var snapshot = index.Env.Store.List(TestEnvironment.Workspace(workspace)).Value.Snapshots.Single(s => s.SnapshotId == snapshotId);
        var latest = index.IndexStore.LoadLatest(snapshot, IndexFormats.ParserVersion).Value;
        var provenance = latest.Provenance with { AnalysisRunId = AnalysisRunId.New() };
        var forged = new SourceIndex(provenance, latest.SnapshotAnalysisRunId, latest.CreatedUtc, limits ?? latest.Limits,
            (members ?? (m => m))(latest.Members),
            (artifacts ?? (a => a))(latest.Artifacts).Select(a => a with { Provenance = provenance }).ToList(),
            (edges ?? (e => e))(latest.Edges).Select(e => e with { Provenance = provenance }).ToList(),
            latest.SyntaxGaps);
        var published = index.IndexStore.Publish(forged, snapshot);
        Assert.True(published.Succeeded, string.Join("; ", published.Diagnostics));
        Assert.Equal(provenance.AnalysisRunId, index.IndexStore.LoadLatest(snapshot, IndexFormats.ParserVersion).Value.Provenance.AnalysisRunId);
    }

    public static void EditJson(string file, Action<JsonNode> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(file))!;
        edit(root);
        Access.MakeWritable(file);
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void RehashRun(string directory)
    {
        var facts = Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "facts.json")));
        Replace(Path.Combine(directory, "index.json"), "\"factsSha256\": \"[0-9a-f]+\"", $"\"factsSha256\": \"{facts}\"");
        var metadata = Utf8Text.Sha256Hex(File.ReadAllBytes(Path.Combine(directory, "index.json")));
        Replace(Path.Combine(directory, "complete.json"), "\"indexSha256\": \"[0-9a-f]+\"", $"\"indexSha256\": \"{metadata}\"");
    }

    private static void Replace(string file, string pattern, string replacement)
    {
        Access.MakeWritable(file);
        File.WriteAllText(file, Regex.Replace(File.ReadAllText(file), pattern, replacement));
    }

    public static string RunDirectory(IndexEnvironment index, string workspace, SnapshotId snapshot) =>
        index.RunDirectory(workspace, index.IndexStore.LoadLatest(index.Env.Store.List(TestEnvironment.Workspace(workspace)).Value.Snapshots
            .Single(s => s.SnapshotId == snapshot), IndexFormats.ParserVersion).Value.Summary);
}
