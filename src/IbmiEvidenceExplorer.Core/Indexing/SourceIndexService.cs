using System.Globalization;
using System.Text.RegularExpressions;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Core.Indexing;

/// <summary>
/// F1 orchestration: reads one verified F0 snapshot through <see cref="ISnapshotStore"/>, parses
/// each member, builds the deterministic graph, and publishes through <see cref="IIndexRepository"/>.
/// Queries load the latest index for the snapshot and parser version, validated against the F0
/// snapshot, and re-verify the stored bytes of every member they cite. Logs carry identifiers and
/// counts only; never source text, identifiers taken from source, or the search query.
/// </summary>
public sealed partial class SourceIndexService
{
    private static readonly ArtifactKind[] SearchableKinds =
        [ArtifactKind.Member, ArtifactKind.View, ArtifactKind.Record, ArtifactKind.Field, ArtifactKind.Variable];

    private readonly ISnapshotStore _snapshots;
    private readonly IIndexRepository _repository;
    private readonly IOperationLog _log;
    private readonly TimeProvider _time;
    private readonly IndexLimits _limits;

    public SourceIndexService(ISnapshotStore snapshots, IIndexRepository repository, IOperationLog? log = null, TimeProvider? time = null, IndexLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(repository);
        _snapshots = snapshots;
        _repository = repository;
        _log = log ?? NullOperationLog.Instance;
        _time = time ?? TimeProvider.System;
        _limits = limits ?? IndexLimits.Default;
    }

    /// <summary>Test hook: a different parser version must never reuse another version's index.</summary>
    internal string ParserVersion { get; init; } = IndexFormats.ParserVersion;

    public OperationResult<IndexBuildOutcome> Build(WorkspaceId workspaceId, SnapshotId snapshotId, CancellationToken cancellationToken = default)
    {
        RequireWorkspace(workspaceId);
        var runId = AnalysisRunId.New();
        Log(OperationLogLevel.Information, "index.started", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
            ("analysisRunId", runId), ("parserVersion", ParserVersion));

        OperationResult<IndexBuildOutcome> result;
        try
        {
            result = BuildCore(workspaceId, snapshotId, runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = OperationResult<IndexBuildOutcome>.Failure(new Diagnostic(DiagnosticCode.IndexCancelled, "Indexing was cancelled; no index was published."));
        }

        if (result.Succeeded)
        {
            var summary = result.Value.Index;
            var counts = summary.Counts;
            Log(OperationLogLevel.Information, "index.published", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
                ("analysisRunId", runId), ("status", Labels.Of(summary.Status)), ("memberCount", Text(counts.Members)),
                ("completeMembers", Text(counts.CompleteMembers)), ("partialMembers", Text(counts.PartialMembers)),
                ("unsupportedMembers", Text(counts.UnsupportedMembers)), ("artifactCount", Text(counts.Artifacts)),
                ("edgeCount", Text(counts.Edges)), ("unresolvedEdges", Text(counts.UnresolvedEdges)), ("diagnosticCount", Text(counts.Diagnostics)));
        }
        else
        {
            LogDiagnostics("index.rejected", workspaceId, snapshotId, runId, result.Diagnostics);
        }

        return result;
    }

    public OperationResult<SearchResult> Search(WorkspaceId workspaceId, SnapshotId snapshotId, string query)
    {
        RequireWorkspace(workspaceId);
        if (query is null || !QueryPattern().IsMatch(query))
        {
            return Denied<SearchResult>("index.search.denied", workspaceId, snapshotId, new Diagnostic(DiagnosticCode.InvalidQuery,
                "Query must be one identifier: 1-128 characters of A-Z a-z 0-9 $ # @ _ . with an optional leading &."));
        }

        var loaded = Load(workspaceId, snapshotId);
        if (!loaded.Succeeded)
        {
            return Denied<SearchResult>("index.search.denied", workspaceId, snapshotId, [.. loaded.Diagnostics]);
        }

        var (reader, index) = loaded.Value;
        var key = IdentifierKey.Of(query);
        var matches = index.Artifacts.Where(a => SearchableKinds.Contains(a.Kind) && a.Key == key).ToList();
        var coverage = index.Members.ToDictionary(m => m.Member.Identity, m => m.Coverage);
        var hits = matches.Take(_limits.MaxQueryResults)
            .Select(a => new SearchHit(a.Kind, a.Name, a.Qualifier, a.Member, coverage[a.Member], a.Evidence, a.Provenance))
            .ToList();

        if (VerifyCitations(reader, index, hits.Select(h => h.Member), hits.Select(h => h.Evidence)) is { } integrity)
        {
            return Denied<SearchResult>("index.search.denied", workspaceId, snapshotId, integrity);
        }

        var result = new SearchResult(index.Summary, query, matches.Count, matches.Count > hits.Count, hits);
        Log(OperationLogLevel.Information, "index.searched", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
            ("analysisRunId", index.Provenance.AnalysisRunId), ("resultCount", Text(hits.Count)), ("totalMatches", Text(matches.Count)),
            ("truncated", result.Truncated ? "true" : "false"));
        return OperationResult<SearchResult>.Success(result);
    }

    public OperationResult<DependencyResult> Dependencies(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member)
    {
        RequireWorkspace(workspaceId);
        ArgumentNullException.ThrowIfNull(member);
        var loaded = Load(workspaceId, snapshotId);
        if (!loaded.Succeeded)
        {
            return Denied<DependencyResult>("index.dependencies.denied", workspaceId, snapshotId, [.. loaded.Diagnostics]);
        }

        var (reader, index) = loaded.Value;
        var indexed = index.Members.FirstOrDefault(m => m.Member.Identity == member);
        if (indexed is null)
        {
            return Denied<DependencyResult>("index.dependencies.denied", workspaceId, snapshotId,
                new Diagnostic(DiagnosticCode.MemberNotFound, $"Snapshot has no member {member}."));
        }

        var all = index.Edges.Where(e => e.SourceMember == member).ToList();
        var edges = all.Take(_limits.MaxQueryResults).ToList();
        var cited = edges.Select(e => e.ResolvedMember).OfType<MemberIdentity>().Prepend(member);
        var citations = edges.Select(e => e.ReferenceEvidence).Concat(edges.Select(e => e.DefinitionEvidence).OfType<EvidenceReference>());
        if (VerifyCitations(reader, index, cited, citations) is { } integrity)
        {
            return Denied<DependencyResult>("index.dependencies.denied", workspaceId, snapshotId, integrity);
        }

        var result = new DependencyResult(index.Summary, member, indexed.Coverage, all.Count,
            all.Count(e => e.Resolution == ResolutionStatus.Unresolved), all.Count > edges.Count, edges);
        Log(OperationLogLevel.Information, "index.dependencies", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
            ("analysisRunId", index.Provenance.AnalysisRunId), ("member", member.ToString()), ("edgeCount", Text(edges.Count)),
            ("totalEdges", Text(all.Count)), ("truncated", result.Truncated ? "true" : "false"));
        return OperationResult<DependencyResult>.Success(result);
    }

    /// <summary>
    /// The private CL/DDS syntax-gap report of the latest index. Its keys are source-derived, so it is
    /// returned only on this explicit request and is never written to routine logs.
    /// </summary>
    public OperationResult<SyntaxGapResult> SyntaxGaps(WorkspaceId workspaceId, SnapshotId snapshotId)
    {
        RequireWorkspace(workspaceId);
        var loaded = Load(workspaceId, snapshotId);
        if (!loaded.Succeeded)
        {
            return Denied<SyntaxGapResult>("index.gaps.denied", workspaceId, snapshotId, [.. loaded.Diagnostics]);
        }

        var (reader, index) = loaded.Value;
        var examples = index.SyntaxGaps.Groups.SelectMany(g => g.Examples).ToList();
        if (VerifyCitations(reader, index, [], examples) is { } integrity)
        {
            return Denied<SyntaxGapResult>("index.gaps.denied", workspaceId, snapshotId, integrity);
        }

        Log(OperationLogLevel.Information, "index.gaps", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
            ("analysisRunId", index.Provenance.AnalysisRunId), ("groupCount", Text(index.SyntaxGaps.Groups.Count)),
            ("omittedGroups", Text(index.SyntaxGaps.OmittedGroups)));
        return OperationResult<SyntaxGapResult>.Success(new(index.Summary, index.SyntaxGaps));
    }

    private OperationResult<IndexBuildOutcome> BuildCore(WorkspaceId workspaceId, SnapshotId snapshotId, string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opened = _snapshots.OpenSnapshot(workspaceId, snapshotId);
        if (!opened.Succeeded)
        {
            return OperationResult<IndexBuildOutcome>.Failure([.. opened.Diagnostics]);
        }

        var reader = opened.Value;
        var snapshot = reader.Snapshot;
        var provenance = new IndexProvenance(workspaceId, snapshotId, runId, ParserVersion, IndexFormats.IndexFormat);
        var members = new List<MemberFacts>(snapshot.Members.Count);
        long admittedBytes = 0;
        var admittedItems = 0;
        foreach (var member in snapshot.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Limits are checked from F0 metadata before any member bytes are read.
            if (member.Ordinal >= _limits.MaxMembers)
            {
                members.Add(Skipped(member, ParseDiagnosticCode.MemberLimitExceeded,
                    $"Member was not indexed: the index member limit is {_limits.MaxMembers}."));
                continue;
            }

            if (member.ByteLength > _limits.MaxMemberBytes)
            {
                members.Add(Skipped(member, ParseDiagnosticCode.MemberTooLarge,
                    $"Member was not indexed: its {member.ByteLength} bytes exceed the per-member index limit of {_limits.MaxMemberBytes}."));
                continue;
            }

            if (admittedBytes + member.ByteLength > _limits.MaxTotalBytes)
            {
                members.Add(Skipped(member, ParseDiagnosticCode.SourceLimitExceeded,
                    $"Member was not indexed: it would exceed the total index source limit of {_limits.MaxTotalBytes} bytes."));
                continue;
            }

            var read = reader.ReadMember(member.Identity);
            if (!read.Succeeded)
            {
                return OperationResult<IndexBuildOutcome>.Failure([.. read.Diagnostics]);
            }

            var lines = read.Value.Lines;
            var parsed = SourceParser.Parse(member.Identity.SourceType, lines.Select(l => l.Text).ToList());
            var items = 1 + parsed.Artifacts.Count + parsed.References.Count;
            if (admittedItems + items > _limits.MaxItems)
            {
                members.Add(Skipped(member, ParseDiagnosticCode.ItemLimitExceeded,
                    $"Member was not indexed: its {items} facts and references would exceed the index limit of {_limits.MaxItems}."));
                continue;
            }

            admittedBytes += member.ByteLength;
            admittedItems += items;
            members.Add(Locate(member, parsed, lines));
        }

        var index = DependencyGraph.Build(provenance, snapshot, _time.GetUtcNow(), _limits, members);
        cancellationToken.ThrowIfCancellationRequested();
        var published = _repository.Publish(index, snapshot, cancellationToken);
        return published.Succeeded
            ? OperationResult<IndexBuildOutcome>.Success(new(published.Value, index.Members))
            : OperationResult<IndexBuildOutcome>.Failure([.. published.Diagnostics]);
    }

    internal static MemberFacts Skipped(SnapshotMember member, ParseDiagnosticCode code, string message) =>
        new(member, CoverageStatus.Unsupported, [new IndexDiagnostic(code, message, null)], new Dictionary<ParseDiagnosticCode, int> { [code] = 1 }, 0,
            null, [], [], [], Parsed: false);

    /// <summary>Attaches the unchanged F0 evidence reference for every cited line.</summary>
    internal static MemberFacts Locate(SnapshotMember member, ParsedMember parsed, IReadOnlyList<EvidenceLine> lines)
    {
        EvidenceReference At(int line) => lines[line - 1].Reference;
        return new MemberFacts(
            member,
            parsed.Coverage,
            parsed.Diagnostics.Select(d => new IndexDiagnostic(d.Code, d.Message, d.Line is { } line ? At(line) : null)).ToList(),
            parsed.DiagnosticTotals,
            parsed.SuppressedDiagnostics,
            parsed.DefinitionLine is { } definition ? At(definition) : null,
            parsed.Artifacts.Select(a => (a, At(a.Line))).ToList(),
            parsed.References.Select(r => (r, At(r.Line))).ToList(),
            parsed.Gaps.Select(g => (g, At(g.FirstLine))).ToList(),
            Parsed: true);
    }

    private OperationResult<(ISnapshotReader Reader, SourceIndex Index)> Load(WorkspaceId workspaceId, SnapshotId snapshotId)
    {
        var opened = _snapshots.OpenSnapshot(workspaceId, snapshotId);
        if (!opened.Succeeded)
        {
            return OperationResult<(ISnapshotReader, SourceIndex)>.Failure([.. opened.Diagnostics]);
        }

        var index = _repository.LoadLatest(opened.Value.Snapshot, ParserVersion);
        return index.Succeeded
            ? OperationResult<(ISnapshotReader, SourceIndex)>.Success((opened.Value, index.Value))
            : OperationResult<(ISnapshotReader, SourceIndex)>.Failure([.. index.Diagnostics]);
    }

    /// <summary>
    /// Re-hashes the stored bytes of each cited member so results never cite tampered source, and
    /// requires every returned citation to equal F0's own evidence reference for its line (line
    /// number, byte offset, byte length, and line hash as one unit). The index files are hashed only
    /// against each other, so an internally rehashed index could otherwise point at the wrong line.
    /// </summary>
    private static Diagnostic? VerifyCitations(ISnapshotReader reader, SourceIndex index, IEnumerable<MemberIdentity> members, IEnumerable<EvidenceReference> citations)
    {
        var byMember = citations.Distinct().GroupBy(c => c.Member).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var member in members.Concat(byMember.Keys).Distinct())
        {
            if (!byMember.TryGetValue(member, out var cited))
            {
                var verified = reader.VerifyMember(member);
                if (!verified.Succeeded)
                {
                    return verified.Diagnostics[0];
                }

                continue;
            }

            var read = reader.ReadMember(member);
            if (!read.Succeeded)
            {
                return read.Diagnostics[0];
            }

            var lines = read.Value.Lines;
            if (cited.Any(c => c.LineNumber < 1 || c.LineNumber > lines.Count || lines[c.LineNumber - 1].Reference != c))
            {
                return new Diagnostic(DiagnosticCode.IndexIntegrityFailure,
                    $"Index run {index.Provenance.AnalysisRunId} for snapshot {index.Provenance.SnapshotId} cites evidence that does not match the snapshot's lines; it was not used.");
            }
        }

        return null;
    }

    private static void RequireWorkspace(WorkspaceId workspaceId)
    {
        if (workspaceId.Value is null)
        {
            throw new ArgumentException("An explicit, validated workspace ID is required.", nameof(workspaceId));
        }
    }

    private OperationResult<T> Denied<T>(string eventName, WorkspaceId workspaceId, SnapshotId snapshotId, params Diagnostic[] diagnostics)
    {
        LogDiagnostics(eventName, workspaceId, snapshotId, null, diagnostics);
        return OperationResult<T>.Failure(diagnostics);
    }

    private void LogDiagnostics(string eventName, WorkspaceId workspaceId, SnapshotId snapshotId, string? runId, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Log(OperationLogLevel.Warning, eventName, ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
                ("analysisRunId", runId), ("code", diagnostic.Code.ToString()), ("message", diagnostic.Message));
        }
    }

    private void Log(OperationLogLevel level, string name, params (string Key, string? Value)[] fields) =>
        _log.Write(new OperationLogEvent(_time.GetUtcNow(), level, name, fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value)));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^&?[A-Za-z0-9$#@_.]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryPattern();
}
