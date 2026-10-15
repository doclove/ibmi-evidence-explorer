using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>A <see cref="TestEnvironment"/> plus the F1 index store and service over the same storage root.</summary>
public sealed class IndexEnvironment : IDisposable
{
    public IndexEnvironment()
    {
        Env = new TestEnvironment();
        IndexStore = new FileSystemIndexStore(Env.StorageRoot, Env.Log);
    }

    public TestEnvironment Env { get; }

    public FileSystemIndexStore IndexStore { get; }

    public SourceIndexService Service(IndexLimits? limits = null, ISnapshotStore? snapshots = null, string? parserVersion = null) =>
        new(snapshots ?? Env.Store, IndexStore, Env.Log, limits: limits) { ParserVersion = parserVersion ?? IndexFormats.ParserVersion };

    public SnapshotSummary Import(string workspace, string fixture) => Env.Import(workspace, Env.CopyFixture(fixture)).Value.Snapshot;

    public IndexSummary BuildIndex(string workspace, SnapshotId snapshot)
    {
        var result = Service().Build(TestEnvironment.Workspace(workspace), snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        return result.Value.Index;
    }

    public SourceIndex Load(SnapshotSummary snapshot) => IndexStore.LoadLatest(snapshot, IndexFormats.ParserVersion).Value;

    public string IndexesDirectory(string workspace) => Path.Combine(Env.WorkspaceDirectory(workspace), "indexes");

    public string IndexStagingDirectory(string workspace) => Path.Combine(Env.WorkspaceDirectory(workspace), "index-staging");

    public string RunDirectory(string workspace, IndexSummary index) =>
        Path.Combine(IndexesDirectory(workspace), index.Provenance.SnapshotId.Value,
            FileSystemIndexStore.ParserDirectoryName(index.Provenance.ParserVersion), index.Provenance.AnalysisRunId);

    public void Dispose() => Env.Dispose();
}

public static class Members
{
    public static MemberIdentity Of(string library, string sourceFile, string member, SourceType type) => new(library, sourceFile, member, type);

    public static readonly MemberIdentity OrdInq = Of("DEMOLIB", "QRPGLESRC", "ORDINQ", SourceType.RPG);
    public static readonly MemberIdentity OrdBatch = Of("DEMOLIB", "QCLSRC", "ORDBATCH", SourceType.CL);
    public static readonly MemberIdentity OrdHdr = Of("DEMOLIB", "QDDSSRC", "ORDHDR", SourceType.DDS);
    public static readonly MemberIdentity OrdView = Of("DEMOLIB", "QSQLSRC", "ORDVIEW", SourceType.SQL);

    public static MemberIdentity Edge(string sourceFile, string member, SourceType type) => Of("EDGELIB", sourceFile, member, type);
}

/// <summary>Decorates a snapshot store to observe or interfere with member reads.</summary>
public sealed class ObservingSnapshotStore(ISnapshotStore inner, Action<MemberIdentity>? beforeRead = null) : ISnapshotStore
{
    public List<MemberIdentity> Reads { get; } = [];

    public OperationResult<ImportOutcome> Import(ImportRequest request, CancellationToken cancellationToken = default) => inner.Import(request, cancellationToken);

    public OperationResult<SnapshotListing> List(WorkspaceId workspaceId) => inner.List(workspaceId);

    public OperationResult<MemberInspection> Inspect(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member, LineRange? lines = null) =>
        inner.Inspect(workspaceId, snapshotId, member, lines);

    public OperationResult<ResolvedEvidence> Resolve(WorkspaceId workspaceId, EvidenceReference reference) => inner.Resolve(workspaceId, reference);

    public OperationResult<ISnapshotReader> OpenSnapshot(WorkspaceId workspaceId, SnapshotId snapshotId)
    {
        var opened = inner.OpenSnapshot(workspaceId, snapshotId);
        return opened.Succeeded ? OperationResult<ISnapshotReader>.Success(new Reader(opened.Value, member =>
        {
            Reads.Add(member);
            beforeRead?.Invoke(member);
        })) : opened;
    }

    private sealed class Reader(ISnapshotReader inner, Action<MemberIdentity> onRead) : ISnapshotReader
    {
        public SnapshotSummary Snapshot => inner.Snapshot;

        public OperationResult<MemberInspection> ReadMember(MemberIdentity member)
        {
            onRead(member);
            return inner.ReadMember(member);
        }

        public OperationResult<SnapshotMember> VerifyMember(MemberIdentity member) => inner.VerifyMember(member);
    }
}

public static class Parse
{
    public static ParsedMember Lines(SourceType type, params string[] lines) => SourceParser.Parse(type, lines);

    /// <summary>Places text at 1-based columns of a fixed-format RPG line with the given specification type in position 6.</summary>
    public static string Rpg(char spec, params (int Column, string Text)[] fields)
    {
        var line = new char[100];
        Array.Fill(line, ' ');
        line[5] = spec;
        foreach (var (column, text) in fields)
        {
            text.CopyTo(0, line, column - 1, text.Length);
        }

        return new string(line).TrimEnd();
    }

    /// <summary>F-spec: name 7, type 17, designation 18, format 22, record address type 34, device 36, keywords 44.</summary>
    public static string RpgF(string name, string type = "IF", char format = 'E', char addressType = 'K', string device = "DISK", string keywords = "") =>
        Rpg('F', (7, name), (17, type), (22, format.ToString()), (34, addressType.ToString()), (36, device), (44, keywords));

    /// <summary>C-spec: factor 1 at 12, operation 26, factor 2 36, result 50, resulting indicators 71.</summary>
    public static string RpgC(string operation, string factor1 = "", string factor2 = "", string result = "", string indicators = "") =>
        Rpg('C', (12, factor1), (26, operation), (36, factor2), (50, result), (71, indicators));

    /// <summary>Builds a fixed-format DDS line: name type at position 17, name at 19, length 30-34, type 35, decimals 36-37, keywords 45.</summary>
    public static string Dds(char nameType, string name, string length = "", char type = ' ', string decimals = "", string keywords = "") =>
        ("     A" + new string(' ', 10) + nameType + " " + name.PadRight(10) + " " + length.PadLeft(5) + type + decimals.PadLeft(2) + new string(' ', 7) + keywords).TrimEnd();
}
