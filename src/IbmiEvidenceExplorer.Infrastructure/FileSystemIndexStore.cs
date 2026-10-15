using System.Text.Json;
using System.Text.RegularExpressions;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>
/// Stores F1 indexes beside, never inside, the F0 snapshots:
/// <c>{root}/workspaces/{workspace}/indexes/{snapshot}/{parser}/{index-run}/</c>. A build is staged
/// under <c>{workspace}/index-staging</c>, re-verified, marked complete, made read-only, and
/// published with one directory rename, so an interrupted build is never visible. Each index run
/// is a separate directory; queries use the run with the greatest (time-ordered) run ID and never
/// fall back to an older run when that one fails validation. No source text is stored.
/// </summary>
public sealed partial class FileSystemIndexStore : IIndexRepository
{
    internal const string IndexesDirectory = "indexes";
    internal const string StagingDirectory = "index-staging";
    internal const string FactsFile = "facts.json";
    internal const string MetadataFile = "index.json";
    internal const string MarkerFile = "complete.json";

    private const long MaxFactsBytes = 512 * ImportLimits.MiB;
    private const long MaxMetadataBytes = 1 * ImportLimits.MiB;
    private const long MaxMarkerBytes = 64 * 1024;

    private readonly string _root;
    private readonly IOperationLog _log;
    private readonly TimeProvider _time;

    public FileSystemIndexStore(string storageRoot, IOperationLog? log = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _root = Path.GetFullPath(storageRoot);
        _log = log ?? NullOperationLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Test hook: invoked at each publish stage so tests can simulate failure or interruption.</summary>
    internal Action<IndexPublishStage>? FaultInjector { get; set; }

    /// <summary>Directory name for a parser version: lowercase, with anything outside [a-z0-9.-] replaced by '-'.</summary>
    internal static string ParserDirectoryName(string parserVersion) => UnsafeSegment().Replace(parserVersion.ToLowerInvariant(), "-");

    public OperationResult<IndexSummary> Publish(SourceIndex index, SnapshotSummary snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(snapshot);
        var provenance = index.Provenance;
        if (provenance.WorkspaceId != snapshot.WorkspaceId || provenance.SnapshotId != snapshot.SnapshotId)
        {
            throw new ArgumentException("Index and snapshot must belong to the same workspace and snapshot.", nameof(index));
        }

        var workspace = provenance.WorkspaceId;
        if (PrepareDirectories(workspace, provenance) is { } storageProblem)
        {
            return Fail<IndexSummary>(storageProblem);
        }

        var staging = Path.Combine(StagingPath(workspace), $"{provenance.SnapshotId.Value}.{provenance.AnalysisRunId}");
        var published = false;
        try
        {
            PrivateStorage.CreateDirectory(staging, verifyExisting: true);
            var factsBytes = JsonSerializer.SerializeToUtf8Bytes(ToFacts(index, snapshot), IndexJson.Options);
            WriteDurably(Path.Combine(staging, FactsFile), factsBytes);
            FaultInjector?.Invoke(IndexPublishStage.FactsWritten);
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = new IndexMetadataDocument
            {
                SchemaVersion = IndexFormats.IndexSchema,
                IndexFormatVersion = provenance.IndexFormatVersion,
                ParserVersion = provenance.ParserVersion,
                WorkspaceId = workspace.Value,
                SnapshotId = provenance.SnapshotId.Value,
                SnapshotAnalysisRunId = index.SnapshotAnalysisRunId,
                AnalysisRunId = provenance.AnalysisRunId,
                CreatedUtc = index.CreatedUtc,
                Status = index.Status,
                Limits = new IndexLimitsDocument
                {
                    MaxMemberBytes = index.Limits.MaxMemberBytes,
                    MaxTotalBytes = index.Limits.MaxTotalBytes,
                    MaxMembers = index.Limits.MaxMembers,
                    MaxItems = index.Limits.MaxItems,
                    MaxQueryResults = index.Limits.MaxQueryResults,
                },
                Counts = index.Counts,
                FactsSha256 = Utf8Text.Sha256Hex(factsBytes),
            };
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, IndexJson.Options);
            WriteDurably(Path.Combine(staging, MetadataFile), metadataBytes);
            FaultInjector?.Invoke(IndexPublishStage.MetadataWritten);
            cancellationToken.ThrowIfCancellationRequested();

            // Verify what was written before declaring it complete.
            if (ReadBounded(Path.Combine(staging, FactsFile), MaxFactsBytes) is not { } storedFacts || Utf8Text.Sha256Hex(storedFacts) != metadata.FactsSha256
                || ReadBounded(Path.Combine(staging, MetadataFile), MaxMetadataBytes) is not { } storedMetadata
                || Utf8Text.Sha256Hex(storedMetadata) != Utf8Text.Sha256Hex(metadataBytes))
            {
                return Fail<IndexSummary>(new Diagnostic(DiagnosticCode.PublishFailed, "Staged index files did not verify; no index was published."));
            }

            var marker = new IndexMarkerDocument
            {
                SchemaVersion = IndexFormats.IndexSchema,
                WorkspaceId = workspace.Value,
                SnapshotId = provenance.SnapshotId.Value,
                AnalysisRunId = provenance.AnalysisRunId,
                ParserVersion = provenance.ParserVersion,
                IndexSha256 = Utf8Text.Sha256Hex(metadataBytes),
                CompletedUtc = _time.GetUtcNow(),
            };
            WriteDurably(Path.Combine(staging, MarkerFile), JsonSerializer.SerializeToUtf8Bytes(marker, IndexJson.Options));
            FaultInjector?.Invoke(IndexPublishStage.MarkerWritten);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in Directory.EnumerateFiles(staging))
            {
                PrivateStorage.MakeReadOnly(file);
            }

            Directory.Move(staging, Path.Combine(ParserPath(workspace, provenance.SnapshotId, provenance.ParserVersion), provenance.AnalysisRunId));
            published = true;
            return OperationResult<IndexSummary>.Success(index.Summary);
        }
        catch (OperationCanceledException)
        {
            return Fail<IndexSummary>(new Diagnostic(DiagnosticCode.IndexCancelled, "Indexing was cancelled; no index was published."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail<IndexSummary>(new Diagnostic(DiagnosticCode.PublishFailed,
                $"Index could not be staged or published ({ex.GetType().Name}); no index was published."));
        }
        finally
        {
            if (!published)
            {
                RemoveStaging(staging, workspace, provenance.AnalysisRunId);
            }
        }
    }

    public OperationResult<SourceIndex> LoadLatest(SnapshotSummary snapshot, string parserVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        var workspace = snapshot.WorkspaceId;
        try
        {
            if (CheckStorage(workspace, snapshot.SnapshotId, parserVersion) is { } storageProblem)
            {
                return Fail<SourceIndex>(storageProblem);
            }

            var parserPath = ParserPath(workspace, snapshot.SnapshotId, parserVersion);
            var latest = Directory.Exists(parserPath)
                ? Directory.EnumerateFileSystemEntries(parserPath).Select(Path.GetFileName).Where(n => RunPattern().IsMatch(n!)).Max(StringComparer.Ordinal)
                : null;
            if (latest is null)
            {
                return Fail<SourceIndex>(new Diagnostic(DiagnosticCode.IndexNotFound,
                    $"No published index for snapshot {snapshot.SnapshotId} with parser version {parserVersion}; run 'index' first."));
            }

            return LoadRun(snapshot, parserVersion, Path.Combine(parserPath, latest), latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail<SourceIndex>(StorageUnavailable(ex));
        }
    }

    private static OperationResult<SourceIndex> LoadRun(SnapshotSummary snapshot, string parserVersion, string directory, string runId)
    {
        var integrity = new Diagnostic(DiagnosticCode.IndexIntegrityFailure,
            $"Index run {runId} for snapshot {snapshot.SnapshotId} is incomplete or failed verification; it was not used.");
        if (new DirectoryInfo(directory).LinkTarget is not null || !Directory.Exists(directory))
        {
            return Fail<SourceIndex>(integrity);
        }

        if (!PrivateStorage.IsPrivateOrMissing(directory))
        {
            return Fail<SourceIndex>(UnsafeStorage());
        }

        var markerBytes = ReadFile(directory, MarkerFile, MaxMarkerBytes);
        var metadataBytes = ReadFile(directory, MetadataFile, MaxMetadataBytes);
        var factsBytes = ReadFile(directory, FactsFile, MaxFactsBytes);
        if (markerBytes is null || metadataBytes is null || factsBytes is null)
        {
            return Fail<SourceIndex>(integrity);
        }

        IndexMarkerDocument? marker;
        IndexMetadataDocument? metadata;
        IndexFactsDocument? facts;
        try
        {
            marker = JsonSerializer.Deserialize<IndexMarkerDocument>(markerBytes, IndexJson.Options);
            metadata = JsonSerializer.Deserialize<IndexMetadataDocument>(metadataBytes, IndexJson.Options);
            if (!IndexDocumentShape.IsValid(marker) || !IndexDocumentShape.IsValid(metadata))
            {
                return Fail<SourceIndex>(integrity);
            }

            if (marker.SchemaVersion != IndexFormats.IndexSchema || metadata.SchemaVersion != IndexFormats.IndexSchema
                || metadata.IndexFormatVersion != IndexFormats.IndexFormat)
            {
                return Fail<SourceIndex>(new Diagnostic(DiagnosticCode.IndexSchemaUnsupported,
                    $"Index run {runId} uses an unsupported schema or index format; re-run 'index'."));
            }

            facts = JsonSerializer.Deserialize<IndexFactsDocument>(factsBytes, IndexJson.Options);
        }
        catch (JsonException)
        {
            return Fail<SourceIndex>(integrity);
        }

        var workspace = snapshot.WorkspaceId.Value;
        if (!IndexDocumentShape.IsValid(facts) || facts.SchemaVersion != IndexFormats.IndexSchema
            || marker.WorkspaceId != workspace || metadata.WorkspaceId != workspace
            || marker.SnapshotId != snapshot.SnapshotId.Value || metadata.SnapshotId != snapshot.SnapshotId.Value
            || marker.AnalysisRunId != runId || metadata.AnalysisRunId != runId
            || marker.ParserVersion != parserVersion || metadata.ParserVersion != parserVersion
            || marker.IndexSha256 != Utf8Text.Sha256Hex(metadataBytes)
            || metadata.FactsSha256 != Utf8Text.Sha256Hex(factsBytes))
        {
            return Fail<SourceIndex>(integrity);
        }

        // The index must describe exactly the members (identity and bytes) of the F0 snapshot it names.
        if (metadata.SnapshotAnalysisRunId != snapshot.AnalysisRunId || facts.Members.Count != snapshot.Members.Count
            || facts.Members.Where((m, i) => !SameMember(m, snapshot.Members[i])).Any())
        {
            return Fail<SourceIndex>(new Diagnostic(DiagnosticCode.IndexSnapshotMismatch,
                $"Index run {runId} does not match the members or hashes of snapshot {snapshot.SnapshotId}; re-run 'index'."));
        }

        var provenance = new IndexProvenance(snapshot.WorkspaceId, snapshot.SnapshotId, runId, parserVersion, metadata.IndexFormatVersion);
        var limits = new IndexLimits(metadata.Limits.MaxMemberBytes, metadata.Limits.MaxTotalBytes, metadata.Limits.MaxMembers,
            metadata.Limits.MaxItems, metadata.Limits.MaxQueryResults);
        var index = limits.IsWithinBounds ? FromFacts(facts, snapshot, provenance, metadata.CreatedUtc, limits) : null;

        // Stored status and counts are re-derived, so a tampered header cannot claim completeness.
        if (index is null || index.Counts != metadata.Counts || index.Status != metadata.Status)
        {
            return Fail<SourceIndex>(integrity);
        }

        return OperationResult<SourceIndex>.Success(index);
    }

    private static bool SameMember(IndexMemberDocument stored, SnapshotMember member) =>
        stored.Ordinal == member.Ordinal && stored.Library == member.Identity.Library && stored.SourceFile == member.Identity.SourceFile
        && stored.Member == member.Identity.Member && stored.SourceType == member.Identity.SourceType && stored.Sha256 == member.Sha256;

    // ---- Document mapping ---------------------------------------------------------------------

    private static IndexFactsDocument ToFacts(SourceIndex index, SnapshotSummary snapshot)
    {
        var ordinals = snapshot.Members.ToDictionary(m => m.Identity, m => m.Ordinal);
        LineDocument? Line(EvidenceReference? reference) => reference is null ? null : new LineDocument
        {
            Member = ordinals[reference.Member],
            Line = reference.LineNumber,
            ByteOffset = reference.ByteOffset,
            ByteLength = reference.ByteLength,
            LineSha256 = reference.LineSha256,
        };

        return new IndexFactsDocument
        {
            SchemaVersion = IndexFormats.IndexSchema,
            Members = index.Members.Select(m => new IndexMemberDocument
            {
                Ordinal = m.Member.Ordinal,
                Library = m.Member.Identity.Library,
                SourceFile = m.Member.Identity.SourceFile,
                Member = m.Member.Identity.Member,
                SourceType = m.Member.Identity.SourceType,
                Sha256 = m.Member.Sha256,
                Coverage = m.Coverage,
                Diagnostics = m.Diagnostics.Select(d => new DiagnosticDocument { Code = d.Code, Message = d.Message, Evidence = Line(d.Evidence) }).ToList(),
                DiagnosticTotals = new SortedDictionary<string, int>(m.DiagnosticTotals.ToDictionary(t => Labels.Of(t.Key), t => t.Value), StringComparer.Ordinal),
                SuppressedDiagnostics = m.SuppressedDiagnostics,
            }).ToList(),
            SyntaxGaps = new SyntaxGapsDocument
            {
                Groups = index.SyntaxGaps.Groups.Select(g => new SyntaxGapGroupDocument
                {
                    Kind = g.Kind,
                    Key = g.Key,
                    Occurrences = g.Occurrences,
                    Members = g.Members,
                    Examples = g.Examples.Select(e => Line(e)!).ToList(),
                }).ToList(),
                OmittedGroups = index.SyntaxGaps.OmittedGroups,
                OmittedOccurrences = index.SyntaxGaps.OmittedOccurrences,
            },
            Artifacts = index.Artifacts.Select(a => new ArtifactDocument
            {
                Kind = a.Kind,
                Name = a.Name,
                Qualifier = a.Qualifier,
                Member = ordinals[a.Member],
                Evidence = Line(a.Evidence)!,
            }).ToList(),
            Edges = index.Edges.Select(e => new EdgeDocument
            {
                Kind = e.Kind,
                Member = ordinals[e.SourceMember],
                SourceKind = e.SourceKind,
                SourceName = e.SourceName,
                Target = e.Target,
                TargetLibrary = e.TargetLibrary,
                TargetName = e.TargetName,
                Evidence = Line(e.ReferenceEvidence)!,
                Resolution = e.Resolution,
                ResolvedMember = e.ResolvedMember is null ? null : ordinals[e.ResolvedMember],
                DefinitionEvidence = Line(e.DefinitionEvidence),
                Reason = e.Reason,
                CandidateCount = e.CandidateCount,
                AssumedCallerLibrary = e.AssumedCallerLibrary,
            }).ToList(),
        };
    }

    /// <summary>Rebuilds the domain index; null if any stored value is out of range for the snapshot.</summary>
    private static SourceIndex? FromFacts(IndexFactsDocument facts, SnapshotSummary snapshot, IndexProvenance provenance, DateTimeOffset created, IndexLimits limits)
    {
        var members = snapshot.Members;
        SnapshotMember? Member(int ordinal) => ordinal >= 0 && ordinal < members.Count ? members[ordinal] : null;

        // The F0 reference shape for that line; null if out of range. Whether it matches F0's line
        // table (number, offset, length, and hash together) is checked against the member bytes by
        // SourceIndexService before any query returns the citation.
        EvidenceReference? Evidence(LineDocument line)
        {
            var member = Member(line.Member);
            return member is null || line.Line < 1 || line.Line > member.LineCount || line.ByteOffset < 0 || line.ByteLength < 0
                || line.ByteOffset + line.ByteLength > member.ByteLength || !Sha256Pattern().IsMatch(line.LineSha256)
                ? null
                : new EvidenceReference(snapshot.SnapshotId, member.Identity, line.Line, member.Sha256, line.LineSha256,
                    line.ByteOffset, line.ByteLength, snapshot.ImportFormatVersion, snapshot.ParserVersion, snapshot.AnalysisRunId);
        }

        var indexed = new List<IndexedMember>(facts.Members.Count);
        for (var i = 0; i < facts.Members.Count; i++)
        {
            var stored = facts.Members[i];
            var diagnostics = new List<IndexDiagnostic>(stored.Diagnostics.Count);
            foreach (var d in stored.Diagnostics)
            {
                var evidence = d.Evidence is null ? null : Evidence(d.Evidence);
                if (!Enum.IsDefined(d.Code) || (d.Evidence is not null && evidence?.Member != members[i].Identity))
                {
                    return null;
                }

                diagnostics.Add(new(d.Code, d.Message, evidence));
            }

            if (!Enum.IsDefined(stored.Coverage) || Totals(stored, diagnostics) is not { } totals)
            {
                return null;
            }

            indexed.Add(new(members[i], stored.Coverage, diagnostics, totals, stored.SuppressedDiagnostics));
        }

        // Index counts and summaries add totals across members as int: every such sum is at most
        // this one, so a stored set whose grand total does not fit is rejected, not overflowed.
        if (indexed.Sum(m => m.DiagnosticTotals.Values.Sum(v => (long)v)) > int.MaxValue)
        {
            return null;
        }

        var artifacts = new List<IndexedArtifact>(facts.Artifacts.Count);
        foreach (var a in facts.Artifacts)
        {
            var member = Member(a.Member);
            var evidence = Evidence(a.Evidence);
            if (member is null || !Enum.IsDefined(a.Kind) || a.Name.Length == 0 || evidence is null || evidence.Member != member.Identity)
            {
                return null;
            }

            artifacts.Add(new(a.Kind, a.Name, a.Qualifier, member.Identity, evidence, provenance));
        }

        var edges = new List<DependencyEdge>(facts.Edges.Count);
        foreach (var e in facts.Edges)
        {
            var member = Member(e.Member);
            var evidence = Evidence(e.Evidence);
            var target = e.ResolvedMember is { } ordinal ? Member(ordinal) : null;
            var definition = e.DefinitionEvidence is null ? null : Evidence(e.DefinitionEvidence);
            var resolved = e.Resolution == ResolutionStatus.Resolved;
            if (member is null || evidence?.Member != member.Identity || !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.SourceKind)
                || !Enum.IsDefined(e.Resolution) || (e.Reason is { } reason && !Enum.IsDefined(reason))
                || resolved != (target is not null) || resolved == (e.Reason is not null) || (e.ResolvedMember is not null && target is null)
                || (e.DefinitionEvidence is not null && (definition is null || definition.Member != target?.Identity)))
            {
                return null;
            }

            edges.Add(new(e.Kind, member.Identity, e.SourceKind, e.SourceName, e.Target, e.TargetLibrary, e.TargetName, evidence, e.Resolution,
                target?.Identity, definition, e.Reason, e.CandidateCount, e.AssumedCallerLibrary, provenance));
        }

        var groups = new List<SyntaxGapGroup>(facts.SyntaxGaps.Groups.Count);
        foreach (var g in facts.SyntaxGaps.Groups)
        {
            var examples = g.Examples.Select(Evidence).ToList();
            if (!Enum.IsDefined(g.Kind) || !GapKeyPattern().IsMatch(g.Key) || g.Members < 1 || g.Occurrences < g.Members
                || examples.Count is 0 or > SyntaxGapReport.MaxExamples || examples.Count > g.Members || examples.Any(e => e is null))
            {
                return null;
            }

            groups.Add(new(g.Kind, g.Key, g.Occurrences, g.Members, examples!));
        }

        if (facts.SyntaxGaps.OmittedGroups < 0 || facts.SyntaxGaps.OmittedOccurrences < facts.SyntaxGaps.OmittedGroups
            || groups.GroupBy(g => g.Kind).Any(k => k.Count() > SyntaxGapReport.MaxGroupsPerKind || k.Select(g => g.Key).Distinct(StringComparer.Ordinal).Count() != k.Count()))
        {
            return null;
        }

        return new SourceIndex(provenance, snapshot.AnalysisRunId, created, limits, indexed, artifacts, edges,
            new SyntaxGapReport(groups, facts.SyntaxGaps.OmittedGroups, facts.SyntaxGaps.OmittedOccurrences));
    }

    private static readonly Dictionary<string, ParseDiagnosticCode> CodesByLabel =
        Enum.GetValues<ParseDiagnosticCode>().ToDictionary(c => Labels.Of(c), StringComparer.Ordinal);

    /// <summary>
    /// Reads per-code totals and checks them against the stored sample: every total is positive and
    /// at least the sampled count of its code, the truncation marker appears exactly when diagnostics
    /// were suppressed, and <c>sum(totals) = sample + suppressed</c>, summed as long so extreme stored
    /// values cannot overflow. Null when inconsistent.
    /// </summary>
    private static Dictionary<ParseDiagnosticCode, int>? Totals(IndexMemberDocument stored, List<IndexDiagnostic> sample)
    {
        var totals = new Dictionary<ParseDiagnosticCode, int>();
        foreach (var (label, count) in stored.DiagnosticTotals)
        {
            if (!CodesByLabel.TryGetValue(label, out var code) || code == ParseDiagnosticCode.DiagnosticsTruncated || count < 1)
            {
                return null;
            }

            totals[code] = count;
        }

        var markers = sample.Count(d => d.Code == ParseDiagnosticCode.DiagnosticsTruncated);
        var shown = sample.Where(d => d.Code != ParseDiagnosticCode.DiagnosticsTruncated).ToList();
        var consistent = stored.SuppressedDiagnostics >= 0
            && markers == (stored.SuppressedDiagnostics > 0 ? 1 : 0)
            && (markers == 0 || sample[^1].Code == ParseDiagnosticCode.DiagnosticsTruncated)
            && shown.Count <= Core.Parsing.SourceParser.MaxDiagnosticsPerMember
            && (stored.SuppressedDiagnostics == 0 || shown.Count == Core.Parsing.SourceParser.MaxDiagnosticsPerMember)
            && shown.GroupBy(d => d.Code).All(g => totals.GetValueOrDefault(g.Key) >= g.Count())
            && totals.Values.Sum(v => (long)v) == shown.Count + (long)stored.SuppressedDiagnostics;
        return consistent ? totals : null;
    }

    // ---- Storage layout -----------------------------------------------------------------------

    private string WorkspacePath(WorkspaceId workspaceId) => Path.Combine(_root, FileSystemSnapshotStore.WorkspacesDirectory, workspaceId.Value);

    private string IndexesPath(WorkspaceId workspaceId) => Path.Combine(WorkspacePath(workspaceId), IndexesDirectory);

    private string StagingPath(WorkspaceId workspaceId) => Path.Combine(WorkspacePath(workspaceId), StagingDirectory);

    private string ParserPath(WorkspaceId workspaceId, SnapshotId snapshotId, string parserVersion) =>
        Path.Combine(IndexesPath(workspaceId), snapshotId.Value, ParserDirectoryName(parserVersion));

    private string[] IndexDirectories(WorkspaceId workspaceId, SnapshotId snapshotId, string parserVersion) =>
    [
        Path.Combine(_root, FileSystemSnapshotStore.WorkspacesDirectory),
        WorkspacePath(workspaceId),
        IndexesPath(workspaceId),
        Path.Combine(IndexesPath(workspaceId), snapshotId.Value),
        ParserPath(workspaceId, snapshotId, parserVersion),
    ];

    /// <summary>Read side: the root must exist and no directory on the index path may be a link, a file, or non-private.</summary>
    private Diagnostic? CheckStorage(WorkspaceId workspaceId, SnapshotId snapshotId, string parserVersion)
    {
        if (!Directory.Exists(_root))
        {
            return new Diagnostic(DiagnosticCode.StorageUnavailable, "Storage root does not exist or is not a directory.");
        }

        foreach (var path in IndexDirectories(workspaceId, snapshotId, parserVersion))
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null || (!info.Exists && File.Exists(path)))
            {
                return new Diagnostic(DiagnosticCode.StorageUnavailable, "Index storage contains an unexpected link or file.");
            }

            if (!PrivateStorage.IsPrivateOrMissing(path))
            {
                return UnsafeStorage();
            }

            if (info.Exists)
            {
                // An unreadable directory must be reported as such, not as a missing index.
                using var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                probe.MoveNext();
            }
        }

        return null;
    }

    private Diagnostic? PrepareDirectories(WorkspaceId workspaceId, IndexProvenance provenance)
    {
        try
        {
            foreach (var path in IndexDirectories(workspaceId, provenance.SnapshotId, provenance.ParserVersion).Append(StagingPath(workspaceId)))
            {
                if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path))
                {
                    return new Diagnostic(DiagnosticCode.StorageUnavailable, "Index storage contains an unexpected link or file.");
                }

                PrivateStorage.CreateDirectory(path, verifyExisting: true);
            }

            return CheckStorage(workspaceId, provenance.SnapshotId, provenance.ParserVersion);
        }
        catch (UnsafeStorageException)
        {
            return UnsafeStorage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageUnavailable(ex);
        }
    }

    private static byte[]? ReadFile(string directory, string name, long maxBytes)
    {
        var path = Path.Combine(directory, name);
        return new FileInfo(path).LinkTarget is null && File.Exists(path) ? ReadBounded(path, maxBytes) : null;
    }

    private static byte[]? ReadBounded(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void WriteDurably(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void RemoveStaging(string staging, WorkspaceId workspaceId, string runId)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                if (OperatingSystem.IsWindows())
                {
                    foreach (var file in Directory.EnumerateFiles(staging))
                    {
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(new OperationLogEvent(_time.GetUtcNow(), OperationLogLevel.Warning, "index.staging_cleanup_failed",
                new Dictionary<string, string?> { ["workspaceId"] = workspaceId.Value, ["analysisRunId"] = runId, ["exception"] = ex.GetType().Name }));
        }
    }

    private static Diagnostic UnsafeStorage() =>
        new(DiagnosticCode.StorageUnavailable, "Index storage is not private to the current user; refusing to use it.");

    private static Diagnostic StorageUnavailable(Exception ex) =>
        new(DiagnosticCode.StorageUnavailable, $"Index storage is not accessible ({ex.GetType().Name}).");

    private static OperationResult<T> Fail<T>(params Diagnostic[] diagnostics) => OperationResult<T>.Failure(diagnostics);

    [GeneratedRegex("^run-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("[^a-z0-9.-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeSegment();

    /// <summary>An upper-case name (CL verb or DDS keyword) or the folded marker for over-long names.</summary>
    [GeneratedRegex("^(?:[A-Z$#@_][A-Z0-9$#@_.]{0,29}|\\(long-name\\))$", RegexOptions.CultureInvariant)]
    private static partial Regex GapKeyPattern();
}

public enum IndexPublishStage
{
    FactsWritten,
    MetadataWritten,
    MarkerWritten,
}
