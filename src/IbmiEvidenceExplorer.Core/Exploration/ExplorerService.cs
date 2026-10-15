using System.Diagnostics;
using System.Globalization;
using System.Text;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Core.Exploration;

/// <summary>
/// F2 dossier construction. Reads the latest validated F1 index for the snapshot (never an older
/// run), re-derives the displayed facts from the F0 snapshot source, and selects bounded, verified
/// evidence. It has no AI, network, or presentation dependency. Logs carry IDs, counts, codes, and
/// elapsed time only; never source text, member names, or other source-derived identifiers.
/// </summary>
public sealed class ExplorerService
{
    private static readonly ParseDiagnosticCode[] LimitCodes =
        [ParseDiagnosticCode.MemberTooLarge, ParseDiagnosticCode.SourceLimitExceeded, ParseDiagnosticCode.MemberLimitExceeded, ParseDiagnosticCode.ItemLimitExceeded];

    private readonly ISnapshotStore _snapshots;
    private readonly IIndexRepository _repository;
    private readonly IOperationLog _log;
    private readonly TimeProvider _time;

    public ExplorerService(ISnapshotStore snapshots, IIndexRepository repository, IOperationLog? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(repository);
        _snapshots = snapshots;
        _repository = repository;
        _log = log ?? NullOperationLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Test hook, as for <see cref="SourceIndexService"/>: the parser version whose index is read.</summary>
    internal string ParserVersion { get; init; } = IndexFormats.ParserVersion;

    public OperationResult<MemberDossier> Explore(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member)
    {
        if (workspaceId.Value is null)
        {
            throw new ArgumentException("An explicit, validated workspace ID is required.", nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(member);
        var started = Stopwatch.GetTimestamp();
        var result = ExploreCore(workspaceId, snapshotId, member);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        if (result.Succeeded)
        {
            var d = result.Value;
            Log(OperationLogLevel.Information, "explore.completed", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
                ("indexRunId", d.Index.Provenance.AnalysisRunId), ("explorerVersion", ExplorerFormats.ExplorerVersion),
                ("completeness", Labels.Of(d.Completeness)), ("outgoingEdges", Text(d.OutgoingCounts.Total)), ("incomingEdges", Text(d.IncomingCounts.Total)),
                ("evidenceLines", Text(d.EvidenceSelection.IncludedLines)), ("omittedLines", Text(d.EvidenceSelection.OmittedLines)), ("elapsedMs", elapsed));
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                // Codes only: some F0/F1 diagnostic messages name the member, which is customer-derived.
                Log(OperationLogLevel.Warning, "explore.rejected", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
                    ("code", diagnostic.Code.ToString()), ("elapsedMs", elapsed));
            }
        }

        return result;
    }

    private OperationResult<MemberDossier> ExploreCore(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member)
    {
        var opened = _snapshots.OpenSnapshot(workspaceId, snapshotId);
        if (!opened.Succeeded)
        {
            return OperationResult<MemberDossier>.Failure([.. opened.Diagnostics]);
        }

        var reader = opened.Value;
        var loaded = _repository.LoadLatest(reader.Snapshot, ParserVersion);
        if (!loaded.Succeeded)
        {
            return OperationResult<MemberDossier>.Failure([.. loaded.Diagnostics]);
        }

        var index = loaded.Value;
        var indexedMembers = index.Members.ToDictionary(m => m.Member.Identity);
        if (!indexedMembers.TryGetValue(member, out var indexed))
        {
            return OperationResult<MemberDossier>.Failure(new Diagnostic(DiagnosticCode.MemberNotFound, "The snapshot has no member with that identity."));
        }

        var outgoingAll = index.Edges.Where(e => e.SourceMember == member).ToList();
        var incomingAll = index.Edges.Where(e => e.ResolvedMember == member && e.Kind != EdgeKind.KeysField).ToList();
        var outgoing = outgoingAll.Take(ExplorerLimits.MaxEdgesPerDirection).ToList();
        var incoming = incomingAll.Take(ExplorerLimits.MaxEdgesPerDirection).ToList();
        var definition = index.Artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.Member && a.Member == member);

        var reads = new Dictionary<MemberIdentity, MemberInspection>();
        OperationResult<MemberInspection> Read(MemberIdentity identity)
        {
            if (reads.TryGetValue(identity, out var cached))
            {
                return OperationResult<MemberInspection>.Success(cached);
            }

            var read = reader.ReadMember(identity);
            if (read.Succeeded)
            {
                reads[identity] = read.Value;
            }

            return read;
        }

        var admission = CheckAdmission(index, reader.Snapshot, indexedMembers, member, outgoing, incoming, Read);
        if (admission is not null)
        {
            return OperationResult<MemberDossier>.Failure([admission]);
        }

        var rederived = Rederive(index, reader.Snapshot, indexedMembers, indexed, definition, outgoingAll, outgoing, incoming, incomingAll.Count, Read);
        if (!rederived.Succeeded)
        {
            return OperationResult<MemberDossier>.Failure([.. rederived.Diagnostics]);
        }

        // Every displayed citation must equal F0's reference for its line (the re-derivation above
        // already compared them; this also covers the diagnostic sample of a member skipped by limits).
        var citations = new List<EvidenceReference>();
        if (definition is not null)
        {
            citations.Add(definition.Evidence);
        }

        citations.AddRange(outgoing.Concat(incoming).SelectMany(e => new[] { e.ReferenceEvidence, e.DefinitionEvidence }).OfType<EvidenceReference>());
        citations.AddRange(indexed.Diagnostics.Select(d => d.Evidence).OfType<EvidenceReference>());
        foreach (var citation in citations)
        {
            var cited = Read(citation.Member);
            if (!cited.Succeeded)
            {
                return OperationResult<MemberDossier>.Failure([.. cited.Diagnostics]);
            }

            if (citation.SnapshotId != snapshotId || Line(Read, citation) is null)
            {
                return IntegrityFailure(index);
            }
        }

        var selection = SelectEvidence(member, definition, outgoing, incoming, indexed, Read, reader.Snapshot, out var evidenceIds);
        if (!selection.Succeeded)
        {
            return OperationResult<MemberDossier>.Failure([.. selection.Diagnostics]);
        }

        var (evidence, selectionCounts) = selection.Value;
        var factNumber = 0;
        string NextFact() => "F" + (++factNumber).ToString(CultureInfo.InvariantCulture);
        string? EvidenceId(EvidenceReference? reference) => reference is not null && evidenceIds.TryGetValue(reference, out var id) ? id : null;

        var dossierDefinition = definition is null ? null : new DossierDefinition(NextFact(), definition, EvidenceId(definition.Evidence));
        var outgoingRelationships = outgoing.Select(e => new DossierRelationship(NextFact(), EdgeDirection.Outgoing, e, EvidenceId(e.ReferenceEvidence), EvidenceId(e.DefinitionEvidence))).ToList();
        var incomingRelationships = incoming.Select(e => new DossierRelationship(NextFact(), EdgeDirection.Incoming, e, EvidenceId(e.ReferenceEvidence), EvidenceId(e.DefinitionEvidence))).ToList();
        var outgoingCounts = Count(outgoingAll, outgoing.Count);
        var incomingCounts = Count(incomingAll, incoming.Count);
        var nameMatches = UnresolvedNameMatches(index, member);

        var summary = index.Summary;
        var completeness = SkipCode(indexed) is null && indexed.Coverage == CoverageStatus.Complete && summary.Status == IndexStatus.Complete && outgoingCounts.Unresolved == 0
            && outgoingCounts.Omitted == 0 && incomingCounts.Omitted == 0 && selectionCounts.OmittedLines == 0
            ? DossierCompleteness.Complete
            : DossierCompleteness.Partial;

        var dossier = new MemberDossier(
            ExplorerFormats.DossierFormat,
            ExplorerFormats.ExplorerVersion,
            summary,
            index.SnapshotAnalysisRunId,
            indexed.Member,
            indexed.Coverage,
            new SortedDictionary<string, int>(indexed.DiagnosticTotals.ToDictionary(t => Labels.Of(t.Key), t => t.Value), StringComparer.Ordinal),
            indexed.SuppressedDiagnostics,
            indexed.Diagnostics,
            dossierDefinition,
            outgoingRelationships,
            outgoingCounts,
            incomingRelationships,
            incomingCounts,
            nameMatches,
            evidence,
            selectionCounts,
            rederived.Value,
            completeness,
            Gaps(summary, indexed, definition, outgoingAll, outgoingCounts, incomingCounts, nameMatches, selectionCounts, rederived.Value),
            Notes);
        return OperationResult<MemberDossier>.Success(dossier);
    }

    /// <summary>Standing notes shown with every dossier and sent with every packet.</summary>
    internal static readonly IReadOnlyList<string> Notes =
    [
        "F1 'complete' coverage means every statement matched a supported form; it does not mean behavior is modeled.",
        "Incoming links are only references that F1 resolved to this member; dynamic calls, unsupported syntax, and callers outside the snapshot are not visible.",
        "Line numbers and text are those of the imported UTF-8 snapshot; for an F1.5 prepared export they are prepared lines, and the original archive record layout and CCSID are unconfirmed.",
    ];

    /// <summary>
    /// Requires every stored limit skip to be coherent with the index's admission rules
    /// (<see cref="SourceIndexService"/>: members in ordinal order; the member limit, then the
    /// per-member byte limit, then the total byte limit, then the fact/edge limit) and with the
    /// stored member state. Byte and member decisions are replayed from F0 metadata for every member;
    /// a fact/edge-limit claim is replayed by re-parsing only for the selected member and displayed
    /// callers and targets. A forged or inconsistent skip is an integrity failure, never a reason to
    /// skip comparison.
    /// </summary>
    private static Diagnostic? CheckAdmission(
        SourceIndex index,
        SnapshotSummary snapshot,
        Dictionary<MemberIdentity, IndexedMember> indexedMembers,
        MemberIdentity selected,
        List<DependencyEdge> outgoing,
        List<DependencyEdge> incoming,
        Func<MemberIdentity, OperationResult<MemberInspection>> read)
    {
        var limits = index.Limits;
        var involved = Involved(selected, outgoing, incoming).ToHashSet();
        var artifacts = index.Artifacts.Where(a => a.Kind != ArtifactKind.Member).GroupBy(a => a.Member).ToDictionary(g => g.Key, g => g.Count());
        var definitions = index.Artifacts.Where(a => a.Kind == ArtifactKind.Member).Select(a => a.Member).ToHashSet();
        var edges = index.Edges.GroupBy(e => e.SourceMember).ToDictionary(g => g.Key, g => g.Count());
        long admittedBytes = 0;
        long admittedItems = 0;
        foreach (var member in snapshot.Members.OrderBy(m => m.Ordinal))
        {
            if (!indexedMembers.TryGetValue(member.Identity, out var stored))
            {
                return IntegrityDiagnostic(index);
            }

            var claimed = SkipCode(stored);
            ParseDiagnosticCode? expected = member.Ordinal >= limits.MaxMembers ? ParseDiagnosticCode.MemberLimitExceeded
                : member.ByteLength > limits.MaxMemberBytes ? ParseDiagnosticCode.MemberTooLarge
                : admittedBytes + member.ByteLength > limits.MaxTotalBytes ? ParseDiagnosticCode.SourceLimitExceeded
                : null;
            var storedItems = 1L + artifacts.GetValueOrDefault(member.Identity) + edges.GetValueOrDefault(member.Identity);
            if (expected is null && claimed == ParseDiagnosticCode.ItemLimitExceeded)
            {
                if (involved.Contains(member.Identity))
                {
                    // Replay the item decision from source, as the index did.
                    var lines = read(member.Identity);
                    if (!lines.Succeeded)
                    {
                        return lines.Diagnostics[0];
                    }

                    var parsed = SourceParser.Parse(member.Identity.SourceType, lines.Value.Lines.Select(l => l.Text).ToList());
                    if (admittedItems + 1 + parsed.Artifacts.Count + parsed.References.Count <= limits.MaxItems)
                    {
                        return IntegrityDiagnostic(index);
                    }
                }

                expected = ParseDiagnosticCode.ItemLimitExceeded;
            }

            if (claimed != expected)
            {
                return IntegrityDiagnostic(index);
            }

            if (claimed is { } code)
            {
                // A skipped member carries exactly its one limit diagnostic and contributes nothing.
                var diagnostic = stored.Diagnostics.Count == 1 ? stored.Diagnostics[0] : null;
                if (stored.Coverage != CoverageStatus.Unsupported || stored.DiagnosticTotals.Count != 1 || stored.DiagnosticTotals[code] != 1
                    || stored.SuppressedDiagnostics != 0 || diagnostic is null || diagnostic.Code != code || diagnostic.Evidence is not null
                    || storedItems != 1 || definitions.Contains(member.Identity))
                {
                    return IntegrityDiagnostic(index);
                }

                continue;
            }

            if (admittedItems + storedItems > limits.MaxItems)
            {
                return IntegrityDiagnostic(index);
            }

            admittedBytes += member.ByteLength;
            admittedItems += storedItems;
        }

        return null;
    }

    /// <summary>The members whose source re-derivation reads: the selected member, displayed callers, and displayed resolved targets.</summary>
    private static IEnumerable<MemberIdentity> Involved(MemberIdentity selected, List<DependencyEdge> outgoing, List<DependencyEdge> incoming) =>
        new[] { selected }
            .Concat(incoming.Select(e => e.SourceMember))
            .Concat(outgoing.Where(e => e.Kind != EdgeKind.KeysField).Select(e => e.ResolvedMember).OfType<MemberIdentity>())
            .Distinct();

    /// <summary>
    /// Re-parses the selected member, each displayed caller, and each displayed resolved target
    /// (at most <see cref="ExplorerLimits.MaxRederivedMembers"/> members) with the index's parser
    /// version and rebuilds their edges against the snapshot metadata. The member's coverage,
    /// diagnostics, and definition, every displayed outgoing and incoming edge (including its F0
    /// citations), and the reference, target, and resolution of every omitted outgoing edge must
    /// match exactly. Omitted outgoing edges' target definitions and omitted incoming edges are not
    /// re-derived and are reported as such.
    /// </summary>
    private OperationResult<RederivationCheck> Rederive(
        SourceIndex index,
        SnapshotSummary snapshot,
        Dictionary<MemberIdentity, IndexedMember> indexedMembers,
        IndexedMember selected,
        IndexedArtifact? definition,
        List<DependencyEdge> outgoingAll,
        List<DependencyEdge> outgoing,
        List<DependencyEdge> incoming,
        int incomingTotal,
        Func<MemberIdentity, OperationResult<MemberInspection>> read)
    {
        var identity = selected.Member.Identity;
        var involved = Involved(identity, outgoing, incoming).ToList();
        if (involved.Count > ExplorerLimits.MaxRederivedMembers)
        {
            throw new InvalidOperationException("Re-derivation exceeded its member bound.");
        }

        var facts = new List<MemberFacts>();
        foreach (var memberIdentity in involved.OrderBy(m => indexedMembers.TryGetValue(m, out var im) ? im.Member.Ordinal : int.MaxValue))
        {
            if (!indexedMembers.TryGetValue(memberIdentity, out var indexedMember))
            {
                return IntegrityFailure<RederivationCheck>(index);
            }

            if (SkipCode(indexedMember) is { } code)
            {
                // A coherent limit skip (checked above): the index did not admit this member.
                facts.Add(SourceIndexService.Skipped(indexedMember.Member, code, string.Empty));
                continue;
            }

            var lines = read(memberIdentity);
            if (!lines.Succeeded)
            {
                return OperationResult<RederivationCheck>.Failure([.. lines.Diagnostics]);
            }

            var parsed = SourceParser.Parse(memberIdentity.SourceType, lines.Value.Lines.Select(l => l.Text).ToList());
            facts.Add(SourceIndexService.Locate(indexedMember.Member, parsed, lines.Value.Lines));
        }

        var fresh = DependencyGraph.Build(index.Provenance, snapshot, index.CreatedUtc, index.Limits, facts);
        var freshSelected = fresh.Members.Single(m => m.Member.Identity == identity);
        var selectedParsed = SkipCode(selected) is null;
        if (selectedParsed && (freshSelected.Coverage != selected.Coverage || freshSelected.SuppressedDiagnostics != selected.SuppressedDiagnostics
            || !freshSelected.Diagnostics.SequenceEqual(selected.Diagnostics) || !SameTotals(freshSelected.DiagnosticTotals, selected.DiagnosticTotals)))
        {
            return IntegrityFailure<RederivationCheck>(index);
        }

        var freshDefinition = fresh.Artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.Member && a.Member == identity);
        if (freshDefinition != definition)
        {
            return IntegrityFailure<RederivationCheck>(index);
        }

        // Displayed outgoing edges must match exactly; omitted ones must match except for the
        // target-definition citation, whose target member was deliberately not read.
        var freshOutgoing = fresh.Edges.Where(e => e.SourceMember == identity).ToList();
        if (freshOutgoing.Count != outgoingAll.Count
            || !freshOutgoing.Take(outgoing.Count).SequenceEqual(outgoing)
            || !freshOutgoing.Skip(outgoing.Count).Select(WithoutDefinition).SequenceEqual(outgoingAll.Skip(outgoing.Count).Select(WithoutDefinition)))
        {
            return IntegrityFailure<RederivationCheck>(index);
        }

        var freshEdges = fresh.Edges.ToHashSet();
        if (!incoming.All(freshEdges.Contains))
        {
            return IntegrityFailure<RederivationCheck>(index);
        }

        var omittedOutgoing = outgoingAll.Count - outgoing.Count;
        var omittedIncoming = incomingTotal - incoming.Count;
        var notRederived = new List<string>
        {
            "Index-wide counts and status, and whether other members reference this member in forms F1 did not resolve, were not re-derived (that needs a full re-index).",
        };
        if (!selectedParsed)
        {
            notRederived.Add("The selected member was not parsed by the index because of a resource limit, so it has no facts to re-derive; the skip itself was checked against the index limits.");
        }

        if (omittedOutgoing > 0)
        {
            notRederived.Add($"{omittedOutgoing} omitted outgoing edge(s): reference, target, and resolution were re-derived, but their target definitions were not read or verified.");
        }

        if (omittedIncoming > 0)
        {
            notRederived.Add($"{omittedIncoming} omitted incoming edge(s) were not re-derived; their callers were not read.");
        }

        return OperationResult<RederivationCheck>.Success(new(ParserVersion, selectedParsed, outgoing.Count, omittedOutgoing, incoming.Count, omittedIncoming,
            facts.Count(f => f.Parsed), notRederived));
    }

    private static DependencyEdge WithoutDefinition(DependencyEdge edge) => edge with { DefinitionEvidence = null };

    /// <summary>
    /// The fixed selection rule: candidates in priority order (member definition; outgoing reference
    /// lines; incoming reference lines; resolved-target definition lines; diagnostic lines; then
    /// every line of the selected member in order), each line once. A line is included when it fits
    /// both the line and byte bounds, otherwise it is omitted and counted; lines are never cut.
    /// Included lines are numbered S1..Sn by member (selected first, then snapshot order) and line.
    /// </summary>
    private static OperationResult<(IReadOnlyList<DossierEvidence> Evidence, EvidenceSelection Counts)> SelectEvidence(
        MemberIdentity member,
        IndexedArtifact? definition,
        List<DependencyEdge> outgoing,
        List<DependencyEdge> incoming,
        IndexedMember indexed,
        Func<MemberIdentity, OperationResult<MemberInspection>> read,
        SnapshotSummary snapshot,
        out Dictionary<EvidenceReference, string> ids)
    {
        ids = [];
        var candidates = new List<(EvidenceReference Reference, EvidencePurpose Purpose)>();
        if (definition is not null)
        {
            candidates.Add((definition.Evidence, EvidencePurpose.MemberDefinition));
        }

        candidates.AddRange(outgoing.Select(e => (e.ReferenceEvidence, EvidencePurpose.OutgoingReference)));
        candidates.AddRange(incoming.Select(e => (e.ReferenceEvidence, EvidencePurpose.IncomingReference)));
        candidates.AddRange(outgoing.Where(e => e.DefinitionEvidence is not null).Select(e => (e.DefinitionEvidence!, EvidencePurpose.TargetDefinition)));
        candidates.AddRange(indexed.Diagnostics.Where(d => d.Evidence is not null).Select(d => (d.Evidence!, EvidencePurpose.Diagnostic)));
        if (SkipCode(indexed) is null)
        {
            var own = read(member);
            if (!own.Succeeded)
            {
                return OperationResult<(IReadOnlyList<DossierEvidence>, EvidenceSelection)>.Failure([.. own.Diagnostics]);
            }

            candidates.AddRange(own.Value.Lines.Select(l => (l.Reference, EvidencePurpose.MemberContext)));
        }

        var included = new Dictionary<EvidenceReference, (EvidenceLine Line, List<EvidencePurpose> Purposes)>();
        var omitted = new Dictionary<EvidenceReference, EvidencePurpose>();
        var bytes = 0;
        foreach (var (reference, purpose) in candidates)
        {
            if (included.TryGetValue(reference, out var existing))
            {
                if (!existing.Purposes.Contains(purpose))
                {
                    existing.Purposes.Add(purpose);
                }

                continue;
            }

            if (omitted.ContainsKey(reference))
            {
                continue;
            }

            var line = Line(read, reference);
            if (line is null)
            {
                return OperationResult<(IReadOnlyList<DossierEvidence>, EvidenceSelection)>.Failure(new Diagnostic(DiagnosticCode.EvidenceMismatch,
                    "A selected evidence reference does not resolve to a line of the snapshot."));
            }

            var size = Encoding.UTF8.GetByteCount(line.Text);
            if (included.Count < ExplorerLimits.MaxEvidenceLines && bytes + size <= ExplorerLimits.MaxEvidenceBytes)
            {
                included[reference] = (line, [purpose]);
                bytes += size;
            }
            else
            {
                omitted[reference] = purpose;
            }
        }

        var ordinals = snapshot.Members.ToDictionary(m => m.Identity, m => m.Ordinal);
        var ordered = included.Values
            .OrderBy(v => v.Line.Reference.Member == member ? -1 : ordinals[v.Line.Reference.Member])
            .ThenBy(v => v.Line.Reference.LineNumber)
            .ToList();
        var evidence = new List<DossierEvidence>(ordered.Count);
        foreach (var (line, purposes) in ordered)
        {
            var id = "S" + (evidence.Count + 1).ToString(CultureInfo.InvariantCulture);
            ids[line.Reference] = id;
            evidence.Add(new(id, line.Reference, line.Text, purposes.Order().ToList()));
        }

        var omittedByPurpose = new SortedDictionary<string, int>(omitted.Values.GroupBy(p => Labels.Of(p)).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
        var counts = new EvidenceSelection(included.Count + omitted.Count, included.Count, omitted.Count, bytes,
            ExplorerLimits.MaxEvidenceLines, ExplorerLimits.MaxEvidenceBytes, omittedByPurpose);
        return OperationResult<(IReadOnlyList<DossierEvidence>, EvidenceSelection)>.Success((evidence, counts));
    }

    private static List<DossierGap> Gaps(
        IndexSummary index,
        IndexedMember indexed,
        IndexedArtifact? definition,
        List<DependencyEdge> outgoingAll,
        RelationshipCounts outgoing,
        RelationshipCounts incoming,
        int nameMatches,
        EvidenceSelection selection,
        RederivationCheck rederivation)
    {
        var gaps = new List<DossierGap>();
        var totals = indexed.DiagnosticTotals.Count == 0 ? string.Empty
            : " (" + string.Join(", ", indexed.DiagnosticTotals.OrderBy(t => Labels.Of(t.Key), StringComparer.Ordinal).Select(t => $"{Labels.Of(t.Key)} {t.Value}")) + ")";
        if (SkipCode(indexed) is { } skip)
        {
            gaps.Add(new("member-not-parsed", $"The member was not parsed by the index ({Labels.Of(skip)}); it has no facts or edges here."));
        }
        else if (indexed.Coverage == CoverageStatus.Partial)
        {
            gaps.Add(new("member-partial", $"Member coverage is partial: {indexed.DiagnosticTotal} statement diagnostic(s){totals} produced no facts, so relationships may be missing."));
        }
        else if (indexed.Coverage == CoverageStatus.Unsupported)
        {
            gaps.Add(new("member-unsupported", $"Member coverage is unsupported: no statement was understood{totals}."));
        }

        if (definition is null)
        {
            gaps.Add(new("no-definition", "The index has no source-backed definition for this member (no statements, or not parsed)."));
        }

        if (index.Status != IndexStatus.Complete)
        {
            gaps.Add(new("index-partial", $"The snapshot index is partial ({index.Counts.PartialMembers} partial and {index.Counts.UnsupportedMembers} unsupported member(s), {index.Counts.UnresolvedEdges} unresolved edge(s)), so incoming links may be missing."));
        }

        if (outgoing.Unresolved > 0)
        {
            gaps.Add(new("unresolved-outgoing", $"{outgoing.Unresolved} outgoing reference(s) are unresolved: {string.Join(", ", outgoing.UnresolvedByReason.Select(r => $"{r.Key} {r.Value}"))}."));
        }

        var assumed = outgoingAll.Count(e => e.Resolution == ResolutionStatus.Resolved && e.AssumedCallerLibrary);
        if (assumed > 0)
        {
            gaps.Add(new("assumed-caller-library", $"{assumed} outgoing edge(s) resolved only under the F1 assumption that an unqualified name is in the caller's manifest library; IBM i library-list resolution is not modeled."));
        }

        if (outgoing.Omitted > 0)
        {
            gaps.Add(new("outgoing-truncated", $"Showing {outgoing.Included} of {outgoing.Total} outgoing edge(s); {outgoing.Omitted} omitted by the {ExplorerLimits.MaxEdgesPerDirection}-edge bound."));
        }

        if (incoming.Omitted > 0)
        {
            gaps.Add(new("incoming-truncated", $"Showing {incoming.Included} of {incoming.Total} incoming edge(s); {incoming.Omitted} omitted by the {ExplorerLimits.MaxEdgesPerDirection}-edge bound."));
        }

        if (nameMatches > 0)
        {
            gaps.Add(new("unresolved-name-matches", $"{nameMatches} unresolved reference(s) elsewhere in the snapshot use this member's name with a compatible kind; F1 did not resolve them to this member (for example ambiguous or another library)."));
        }

        if (selection.OmittedLines > 0)
        {
            gaps.Add(new("evidence-omitted", $"{selection.OmittedLines} of {selection.CandidateLines} candidate source line(s) were omitted by the {selection.MaxLines}-line / {selection.MaxBytes}-byte evidence bound ({string.Join(", ", selection.OmittedByPurpose.Select(p => $"{p.Key} {p.Value}"))})."));
        }

        gaps.AddRange(rederivation.NotRederived.Select(text => new DossierGap("not-rederived", text)));
        return gaps;
    }

    private static RelationshipCounts Count(List<DependencyEdge> all, int included)
    {
        var unresolved = all.Where(e => e.Resolution == ResolutionStatus.Unresolved).ToList();
        return new(all.Count, included, all.Count - included, unresolved.Count,
            new SortedDictionary<string, int>(unresolved.GroupBy(e => Labels.Of(e.Reason!.Value)).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal));
    }

    private static int UnresolvedNameMatches(SourceIndex index, MemberIdentity member)
    {
        EdgeKind[] kinds = member.SourceType switch
        {
            SourceType.CL or SourceType.RPG => [EdgeKind.CallsProgram],
            SourceType.DDS => [EdgeKind.UsesFile, EdgeKind.SelectsFrom],
            _ => [],
        };
        return index.Edges.Count(e => e.Resolution == ResolutionStatus.Unresolved && kinds.Contains(e.Kind) && e.TargetName is not null
            && IdentifierKey.Of(e.TargetName) == member.Member);
    }

    /// <summary>The resource-limit code when the index did not admit the member (such a member carries only that diagnostic).</summary>
    private static ParseDiagnosticCode? SkipCode(IndexedMember member)
    {
        foreach (var code in LimitCodes)
        {
            if (member.DiagnosticTotals.ContainsKey(code))
            {
                return code;
            }
        }

        return null;
    }

    private static bool SameTotals(IReadOnlyDictionary<ParseDiagnosticCode, int> a, IReadOnlyDictionary<ParseDiagnosticCode, int> b) =>
        a.Count == b.Count && a.All(t => b.TryGetValue(t.Key, out var v) && v == t.Value);

    private static EvidenceLine? Line(Func<MemberIdentity, OperationResult<MemberInspection>> read, EvidenceReference reference)
    {
        var member = read(reference.Member);
        if (!member.Succeeded)
        {
            return null;
        }

        var lines = member.Value.Lines;
        return reference.LineNumber >= 1 && reference.LineNumber <= lines.Count && lines[reference.LineNumber - 1].Reference == reference
            ? lines[reference.LineNumber - 1]
            : null;
    }

    private static OperationResult<MemberDossier> IntegrityFailure(SourceIndex index) => IntegrityFailure<MemberDossier>(index);

    private static OperationResult<T> IntegrityFailure<T>(SourceIndex index) => OperationResult<T>.Failure(IntegrityDiagnostic(index));

    private static Diagnostic IntegrityDiagnostic(SourceIndex index) =>
        new(DiagnosticCode.IndexIntegrityFailure,
            $"Index run {index.Provenance.AnalysisRunId} for snapshot {index.Provenance.SnapshotId} does not match facts re-derived from the snapshot source or its own limits; it was not used.");

    private void Log(OperationLogLevel level, string name, params (string Key, string? Value)[] fields) =>
        _log.Write(new OperationLogEvent(_time.GetUtcNow(), level, name, fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value)));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
