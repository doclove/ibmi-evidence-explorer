using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Core.Indexing;

/// <summary>What one member contributed, with every line already mapped to its F0 evidence reference.</summary>
internal sealed record MemberFacts(
    SnapshotMember Member,
    CoverageStatus Coverage,
    IReadOnlyList<IndexDiagnostic> Diagnostics,
    IReadOnlyDictionary<ParseDiagnosticCode, int> DiagnosticTotals,
    int SuppressedDiagnostics,
    EvidenceReference? DefinitionEvidence,
    IReadOnlyList<(ParsedArtifact Fact, EvidenceReference Evidence)> Artifacts,
    IReadOnlyList<(ParsedReference Fact, EvidenceReference Evidence)> References,
    IReadOnlyList<(SyntaxGapOccurrence Gap, EvidenceReference Evidence)> Gaps,
    bool Parsed);

/// <summary>
/// Deterministic, snapshot-scoped graph construction. Each observed reference becomes exactly one
/// edge; a reference resolves only when exactly one candidate exists in the selected snapshot.
/// No transitive or inferred edges are created, so cycles need no special handling.
/// </summary>
internal static class DependencyGraph
{
    private static readonly SourceType[] ProgramTypes = [SourceType.CL, SourceType.RPG];
    private static readonly SourceType[] FileTypes = [SourceType.DDS];

    public static SourceIndex Build(
        IndexProvenance provenance,
        SnapshotSummary snapshot,
        DateTimeOffset createdUtc,
        IndexLimits limits,
        IReadOnlyList<MemberFacts> members)
    {
        var byName = snapshot.Members.ToLookup(m => (m.Identity.Library, m.Identity.Member));
        var byMemberName = snapshot.Members.ToLookup(m => m.Identity.Member);
        var definitions = members.Where(m => m.Parsed).ToDictionary(m => m.Member.Identity, m => m.DefinitionEvidence);

        var artifacts = new List<IndexedArtifact>();
        foreach (var member in members.Where(m => m.Parsed))
        {
            var identity = member.Member.Identity;

            // A member with no cited statement stays in the inventory (Members) but is not a searchable definition.
            if (member.DefinitionEvidence is { } definition)
            {
                artifacts.Add(new(ArtifactKind.Member, identity.Member, identity.Library, identity, definition, provenance));
            }

            artifacts.AddRange(member.Artifacts.Select(a => new IndexedArtifact(a.Fact.Kind, a.Fact.Name, a.Fact.Qualifier, identity, a.Evidence, provenance)));
        }

        var ordinals = snapshot.Members.ToDictionary(m => m.Identity, m => m.Ordinal);
        artifacts.Sort((a, b) => Compare(a, b, ordinals));
        var fields = artifacts.Where(a => a.Kind == ArtifactKind.Field).ToLookup(a => (a.Member, Record: IdentifierKey.Of(a.Qualifier ?? string.Empty), a.Key));

        var edges = new List<DependencyEdge>();
        foreach (var member in members)
        {
            foreach (var (reference, evidence) in member.References)
            {
                edges.Add(Resolve(member.Member, reference, evidence, provenance, byName, byMemberName, definitions, fields));
            }
        }

        edges.Sort((a, b) => Compare(a, b, ordinals));
        var indexed = members.Select(m => new IndexedMember(m.Member, m.Coverage, m.Diagnostics, m.DiagnosticTotals, m.SuppressedDiagnostics)).ToList();
        return new SourceIndex(provenance, snapshot.AnalysisRunId, createdUtc, limits, indexed, artifacts, edges, Gaps(members));
    }

    /// <summary>
    /// Groups unsupported verbs and keywords across members (members in snapshot order). Keeps the
    /// most frequent groups per kind up to the bound, each with its first cited examples.
    /// </summary>
    private static SyntaxGapReport Gaps(IReadOnlyList<MemberFacts> members)
    {
        var groups = new List<SyntaxGapGroup>();
        var omittedGroups = 0;
        var omittedOccurrences = 0;
        foreach (var kind in Enum.GetValues<SyntaxGapKind>())
        {
            var all = members.SelectMany(m => m.Gaps).Where(g => g.Gap.Kind == kind)
                .GroupBy(g => g.Gap.Key.Length <= SyntaxGapReport.MaxKeyLength ? g.Gap.Key : "(long-name)", StringComparer.Ordinal)
                .Select(g => new SyntaxGapGroup(kind, g.Key, g.Sum(o => o.Gap.Count), g.Select(o => o.Evidence.Member).Distinct().Count(),
                    g.Take(SyntaxGapReport.MaxExamples).Select(o => o.Evidence).ToList()))
                .OrderByDescending(g => g.Occurrences).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            groups.AddRange(all.Take(SyntaxGapReport.MaxGroupsPerKind));
            omittedGroups += Math.Max(0, all.Count - SyntaxGapReport.MaxGroupsPerKind);
            omittedOccurrences += all.Skip(SyntaxGapReport.MaxGroupsPerKind).Sum(g => g.Occurrences);
        }

        return new SyntaxGapReport(groups, omittedGroups, omittedOccurrences);
    }

    private static DependencyEdge Resolve(
        SnapshotMember source,
        ParsedReference reference,
        EvidenceReference evidence,
        IndexProvenance provenance,
        ILookup<(string Library, string Member), SnapshotMember> byName,
        ILookup<string, SnapshotMember> byMemberName,
        Dictionary<MemberIdentity, EvidenceReference?> definitions,
        ILookup<(MemberIdentity Member, string Record, string Key), IndexedArtifact> fields)
    {
        var identity = source.Identity;
        var sourceName = reference.SourceKind == ArtifactKind.Member ? identity.Member : reference.SourceName;
        DependencyEdge Edge(ResolutionStatus status, MemberIdentity? target, EvidenceReference? definition, UnresolvedReason? reason, int candidates, bool assumed) =>
            new(reference.Kind, identity, reference.SourceKind, sourceName, reference.Target, reference.TargetLibrary, reference.TargetName,
                evidence, status, target, definition, reason, candidates, assumed, provenance);

        if (reference.FixedReason is { } fixedReason || reference.TargetName is null)
        {
            return Edge(ResolutionStatus.Unresolved, null, null, reference.FixedReason ?? UnresolvedReason.UnsupportedForm, 0, false);
        }

        if (reference.Kind == EdgeKind.KeysField)
        {
            // A key resolves only to a field declared in its own record.
            var matches = fields[(identity, IdentifierKey.Of(reference.SourceName), IdentifierKey.Of(reference.TargetName))].ToList();
            return matches.Count == 1
                ? Edge(ResolutionStatus.Resolved, identity, matches[0].Evidence, null, 1, false)
                : Edge(ResolutionStatus.Unresolved, null, null, matches.Count == 0 ? UnresolvedReason.TargetNotFound : UnresolvedReason.AmbiguousTarget, matches.Count, false);
        }

        var assumed = reference.TargetLibrary is null;
        var library = assumed ? identity.Library : IdentifierKey.Of(reference.TargetLibrary!);
        var name = IdentifierKey.Of(reference.TargetName);
        if (!MemberIdentity.IsValidName(name) || !MemberIdentity.IsValidName(library))
        {
            return Edge(ResolutionStatus.Unresolved, null, null, UnresolvedReason.InvalidTargetName, 0, assumed);
        }

        var types = reference.Kind == EdgeKind.CallsProgram ? ProgramTypes : FileTypes;
        var candidates = byName[(library, name)].Where(m => types.Contains(m.Identity.SourceType)).ToList();
        if (candidates.Count != 1)
        {
            // Absent here: say whether the snapshot's own identities show the name under another
            // library. That is evidence for the operator, never a resolution (no library list is inferred).
            var reason = candidates.Count > 1 ? UnresolvedReason.AmbiguousTarget
                : byMemberName[name].Any(m => types.Contains(m.Identity.SourceType)) ? UnresolvedReason.TargetInOtherLibrary
                : UnresolvedReason.TargetNotInSnapshot;
            return Edge(ResolutionStatus.Unresolved, null, null, reason, candidates.Count, assumed);
        }

        var target = candidates[0].Identity;
        return Edge(ResolutionStatus.Resolved, target, definitions.GetValueOrDefault(target), null, 1, assumed);
    }

    private static int Compare(IndexedArtifact a, IndexedArtifact b, Dictionary<MemberIdentity, int> ordinals)
    {
        var result = ordinals[a.Member].CompareTo(ordinals[b.Member]);
        result = result != 0 ? result : a.Evidence.LineNumber.CompareTo(b.Evidence.LineNumber);
        result = result != 0 ? result : a.Kind.CompareTo(b.Kind);
        result = result != 0 ? result : string.CompareOrdinal(a.Key, b.Key);
        return result != 0 ? result : string.CompareOrdinal(a.Name, b.Name);
    }

    private static int Compare(DependencyEdge a, DependencyEdge b, Dictionary<MemberIdentity, int> ordinals)
    {
        var result = ordinals[a.SourceMember].CompareTo(ordinals[b.SourceMember]);
        result = result != 0 ? result : a.ReferenceEvidence.LineNumber.CompareTo(b.ReferenceEvidence.LineNumber);
        result = result != 0 ? result : a.Kind.CompareTo(b.Kind);
        result = result != 0 ? result : string.CompareOrdinal(a.Target, b.Target);
        return result != 0 ? result : string.CompareOrdinal(a.SourceName, b.SourceName);
    }
}
