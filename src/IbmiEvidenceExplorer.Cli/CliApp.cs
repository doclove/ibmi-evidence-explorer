using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Core.Preparation;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Cli;

/// <summary>
/// Command-line entry point. Command results go to stdout; structured operation logs go to stderr.
/// Exit codes: 0 success, 1 operation rejected with diagnostics, 2 usage error.
/// </summary>
public static partial class CliApp
{
    public const int Ok = 0;
    public const int Rejected = 1;
    public const int Usage = 2;

    public const string StorageEnvironmentVariable = "IBMI_EXPLORER_STORAGE";

    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            // F1 vocabulary uses the specification's labels (calls-program, partial, ...); F0 enums keep their names.
            new JsonStringEnumConverter<CoverageStatus>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ArtifactKind>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<EdgeKind>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<UnresolvedReason>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ParseDiagnosticCode>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<IndexStatus>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ResolutionStatus>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<PreparationStatus>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<SyntaxGapKind>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<EdgeDirection>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<DossierCompleteness>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<EvidencePurpose>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ClaimLabel>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<CitationKind>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter(),
            new WorkspaceIdConverter(),
            new SnapshotIdConverter(),
        },
    };

    /// <summary>Options that take no value.</summary>
    private static readonly string[] Flags = ["json", "acknowledge-local-service", "preview-context"];

    private static readonly Dictionary<string, string[]> CommandOptions = new(StringComparer.Ordinal)
    {
        ["import"] = ["workspace", "source", "storage", "max-member-bytes", "max-import-bytes", "max-members", "json"],
        ["list"] = ["workspace", "storage", "json"],
        ["inspect"] = ["workspace", "snapshot", "library", "source-file", "member", "type", "lines", "storage", "json"],
        ["index"] = ["workspace", "snapshot", "storage", "max-index-bytes", "max-index-members", "max-index-items", "json"],
        ["gaps"] = ["workspace", "snapshot", "storage", "json"],
        ["search"] = ["workspace", "snapshot", "query", "storage", "json"],
        ["dependencies"] = ["workspace", "snapshot", "library", "source-file", "member", "type", "storage", "json"],
        ["explore"] = ["workspace", "snapshot", "library", "source-file", "member", "type", "storage", "json"],
        ["explain"] = ["workspace", "snapshot", "library", "source-file", "member", "type", "model", "acknowledge-local-service", "preview-context",
            "ollama-port", "timeout-seconds", "storage", "json"],
        ["limits"] = [],
        ["prepare scan"] = ["source", "layout", "plan", "ccsid", "record-layout", "record-width", "json"],
        ["prepare build"] = ["source", "plan", "output", "json"],
    };

    public static int Run(string[] args, TextWriter output, TextWriter log, Func<string, string?> environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(environment);

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.Write(HelpText);
            return args.Length == 0 ? Usage : Ok;
        }

        var command = args[0];
        var optionStart = 1;
        if (command == "prepare")
        {
            if (args.Length < 2 || args[1] is not ("scan" or "build"))
            {
                return UsageError(output, "Use 'prepare scan' or 'prepare build'.");
            }

            command = $"prepare {args[1]}";
            optionStart = 2;
        }

        if (!CommandOptions.TryGetValue(command, out var allowed))
        {
            return UsageError(output, $"Unknown command '{ManifestParser.ForDisplay(command)}'.");
        }

        if (!TryParseOptions(args.AsSpan(optionStart), allowed, out var options, out var error))
        {
            return UsageError(output, error);
        }

        if (command == "limits")
        {
            WriteLimits(output);
            return Ok;
        }

        if (command.StartsWith("prepare ", StringComparison.Ordinal))
        {
            var preparer = new ArchivePreparer(new JsonLinesOperationLog(log));
            return command == "prepare scan"
                ? PrepareScan(preparer, options, output, cancellationToken)
                : PrepareBuild(preparer, options, output, cancellationToken);
        }

        if (!options.TryGetValue("workspace", out var workspaceText))
        {
            return UsageError(output, "--workspace is required.");
        }

        if (!WorkspaceId.TryParse(workspaceText, out var workspaceId))
        {
            return Reject(output, new Diagnostic(DiagnosticCode.InvalidWorkspaceId,
                "Workspace ID must be 1-63 characters of a-z, 0-9, or '-', starting with a letter or digit."));
        }

        var storage = options.GetValueOrDefault("storage") ?? environment(StorageEnvironmentVariable) ?? DefaultStorageRoot();
        var operationLog = new JsonLinesOperationLog(log);
        var store = new FileSystemSnapshotStore(storage, operationLog);
        var json = options.ContainsKey("json");

        if (command is "explore" or "explain")
        {
            return Explorer(command, store, storage, operationLog, workspaceId, options, json, output, cancellationToken);
        }

        if (command is "index" or "search" or "dependencies" or "gaps")
        {
            if (!options.TryGetValue("snapshot", out var snapshotText))
            {
                return UsageError(output, "--snapshot is required.");
            }

            if (!SnapshotId.TryParse(snapshotText, out var snapshotId))
            {
                return Reject(output, InvalidSnapshot());
            }

            var invalidLimits = new List<Diagnostic>();
            var maxBytes = ParseLong(options, "max-index-bytes", invalidLimits);
            var maxMembers = ParseLong(options, "max-index-members", invalidLimits);
            var maxItems = ParseLong(options, "max-index-items", invalidLimits);
            if (invalidLimits.Count > 0)
            {
                return Reject(output, [.. invalidLimits]);
            }

            var limits = IndexLimits.Create(maxBytes, Clamp(maxMembers), Clamp(maxItems));
            if (!limits.Succeeded)
            {
                return Reject(output, [.. limits.Diagnostics]);
            }

            var indexes = new SourceIndexService(store, new FileSystemIndexStore(storage, operationLog), operationLog, limits: limits.Value);
            return command switch
            {
                "index" => Index(indexes, workspaceId, snapshotId, json, output, cancellationToken),
                "search" => Search(indexes, workspaceId, snapshotId, options, json, output),
                "gaps" => Gaps(indexes, workspaceId, snapshotId, json, output),
                _ => Dependencies(indexes, workspaceId, snapshotId, options, json, output),
            };
        }

        return command switch
        {
            "import" => Import(store, workspaceId, options, json, output, cancellationToken),
            "list" => List(store, workspaceId, json, output),
            _ => Inspect(store, workspaceId, options, json, output),
        };
    }

    public static string DefaultStorageRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ibmi-explorer");

    private static int Import(FileSystemSnapshotStore store, WorkspaceId workspaceId, Dictionary<string, string?> options, bool json,
        TextWriter output, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("source", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return UsageError(output, "--source is required.");
        }

        var parsedLimits = new List<Diagnostic>();
        var maxMemberBytes = ParseLong(options, "max-member-bytes", parsedLimits);
        var maxImportBytes = ParseLong(options, "max-import-bytes", parsedLimits);
        var maxMembers = ParseLong(options, "max-members", parsedLimits);
        if (parsedLimits.Count > 0)
        {
            return Reject(output, [.. parsedLimits]);
        }

        var limits = ImportLimits.Create(maxMemberBytes, maxImportBytes,
            maxMembers is null ? null : (int)Math.Clamp(maxMembers.Value, int.MinValue, int.MaxValue));
        if (!limits.Succeeded)
        {
            return Reject(output, [.. limits.Diagnostics]);
        }

        var result = store.Import(new ImportRequest(workspaceId, source, limits.Value), cancellationToken);
        if (!result.Succeeded)
        {
            output.WriteLine("Import rejected; no snapshot was published.");
            return Reject(output, [.. result.Diagnostics]);
        }

        var snapshot = result.Value.Snapshot;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(snapshot, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Imported snapshot {snapshot.SnapshotId} into workspace {workspaceId}: {snapshot.Status}");
        WriteSnapshot(output, snapshot);
        return Ok;
    }

    private static int List(FileSystemSnapshotStore store, WorkspaceId workspaceId, bool json, TextWriter output)
    {
        var result = store.List(workspaceId);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        var listing = result.Value;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(listing, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Workspace {workspaceId}: {listing.Snapshots.Count} complete snapshot(s).");
        if (listing.IgnoredEntries > 0)
        {
            output.WriteLine($"Ignored {listing.IgnoredEntries} incomplete or invalid snapshot entr{(listing.IgnoredEntries == 1 ? "y" : "ies")}.");
        }

        foreach (var snapshot in listing.Snapshots)
        {
            output.WriteLine();
            output.WriteLine($"Snapshot {snapshot.SnapshotId}: {snapshot.Status}");
            WriteSnapshot(output, snapshot);
        }

        return Ok;
    }

    private static int Inspect(FileSystemSnapshotStore store, WorkspaceId workspaceId, Dictionary<string, string?> options, bool json, TextWriter output)
    {
        foreach (var required in new[] { "snapshot", "library", "source-file", "member", "type" })
        {
            if (!options.ContainsKey(required))
            {
                return UsageError(output, $"--{required} is required.");
            }
        }

        if (!SnapshotId.TryParse(options["snapshot"], out var snapshotId))
        {
            return Reject(output, InvalidSnapshot());
        }

        if (ParseIdentity(options, out var identity) is { } invalidIdentity)
        {
            return Reject(output, invalidIdentity);
        }

        LineRange? range = null;
        if (options.TryGetValue("lines", out var linesText) && !TryParseRange(linesText, out range))
        {
            return Reject(output, new Diagnostic(DiagnosticCode.InvalidArgument, "--lines must be N or N-M with positive integers."));
        }

        var result = store.Inspect(workspaceId, snapshotId, identity!, range);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        var inspection = result.Value;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(inspection, JsonOutput));
            return Ok;
        }

        var snapshot = inspection.Snapshot;
        var member = inspection.Member;
        output.WriteLine($"Snapshot {snapshot.SnapshotId}: {snapshot.Status}");
        output.WriteLine($"Member {member.Identity}");
        output.WriteLine($"  sha256 {member.Sha256}");
        output.WriteLine($"  bytes {member.ByteLength}, lines {member.LineCount}, ccsid {member.Ccsid}, source path {ManifestParser.ForDisplay(member.SourcePath)}");
        output.WriteLine($"  import format {snapshot.ImportFormatVersion}, parser version {snapshot.ParserVersion}, analysis run {snapshot.AnalysisRunId}");
        output.WriteLine($"  evidence reference form: ibmi-explorer:{snapshot.SnapshotId}/{member.Identity.Library}/{member.Identity.SourceFile}/{member.Identity.Member}/{member.Identity.SourceType}#L<n>");
        output.WriteLine("  line  line-sha256 (first 12)  text");
        var width = Math.Max(5, member.LineCount.ToString(CultureInfo.InvariantCulture).Length);
        foreach (var line in inspection.Lines)
        {
            var number = line.Reference.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width);
            output.WriteLine($"  {number}  {line.Reference.LineSha256[..12]}  {EscapeForTerminal(line.Text)}");
        }

        return Ok;
    }

    private static int Index(SourceIndexService indexes, WorkspaceId workspaceId, SnapshotId snapshotId, bool json, TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = indexes.Build(workspaceId, snapshotId, cancellationToken);
        if (!result.Succeeded)
        {
            output.WriteLine("Indexing rejected; no index was published.");
            return Reject(output, [.. result.Diagnostics]);
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(result.Value, JsonOutput));
            return Ok;
        }

        var index = result.Value.Index;
        output.WriteLine($"Indexed snapshot {snapshotId} in workspace {workspaceId}: {Labels.Of(index.Status)}");
        WriteIndex(output, index);
        foreach (var member in result.Value.Members)
        {
            var shown = member.Diagnostics.Count(d => d.Code != ParseDiagnosticCode.DiagnosticsTruncated);
            output.WriteLine($"  - {member.Member.Identity}  coverage {Labels.Of(member.Coverage)}  {member.DiagnosticTotal} diagnostic(s)"
                + (member.SuppressedDiagnostics > 0 ? $" ({shown} shown, {member.SuppressedDiagnostics} suppressed)" : string.Empty));
            if (member.DiagnosticTotal > 0)
            {
                output.WriteLine($"      totals by code: {Describe(new SortedDictionary<string, int>(member.DiagnosticTotals.ToDictionary(t => Labels.Of(t.Key), t => t.Value), StringComparer.Ordinal))}");
            }

            foreach (var diagnostic in member.Diagnostics)
            {
                var line = diagnostic.Evidence is { } evidence ? $"line {evidence.LineNumber}" : "member";
                output.WriteLine($"      {line} {Labels.Of(diagnostic.Code)}: {diagnostic.Message}");
                if (diagnostic.Evidence is not null)
                {
                    output.WriteLine($"        evidence {diagnostic.Evidence}");
                }
            }
        }

        return Ok;
    }

    private static int Search(SourceIndexService indexes, WorkspaceId workspaceId, SnapshotId snapshotId, Dictionary<string, string?> options, bool json,
        TextWriter output)
    {
        if (!options.TryGetValue("query", out var query))
        {
            return UsageError(output, "--query is required.");
        }

        var result = indexes.Search(workspaceId, snapshotId, query!);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        var search = result.Value;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(search, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Index for snapshot {snapshotId}: {Labels.Of(search.Index.Status)}");
        WriteIndex(output, search.Index);
        output.WriteLine($"{search.TotalMatches} exact match(es) for '{EscapeForTerminal(search.Query)}'{(search.Truncated ? $"; showing the first {search.Hits.Count} (truncated)" : string.Empty)}.");
        foreach (var hit in search.Hits)
        {
            var qualifier = hit.Qualifier is null ? string.Empty : $" in {EscapeForTerminal(hit.Qualifier)}";
            output.WriteLine($"  {Labels.Of(hit.Kind),-13} {EscapeForTerminal(hit.Name)}{qualifier}  member {hit.Member}  coverage {Labels.Of(hit.Coverage)}");
            output.WriteLine($"      evidence {hit.Evidence}");
        }

        return Ok;
    }

    private static int Dependencies(SourceIndexService indexes, WorkspaceId workspaceId, SnapshotId snapshotId, Dictionary<string, string?> options, bool json,
        TextWriter output)
    {
        foreach (var required in new[] { "library", "source-file", "member", "type" })
        {
            if (!options.ContainsKey(required))
            {
                return UsageError(output, $"--{required} is required.");
            }
        }

        if (ParseIdentity(options, out var identity) is { } invalidIdentity)
        {
            return Reject(output, invalidIdentity);
        }

        var result = indexes.Dependencies(workspaceId, snapshotId, identity!);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        var dependencies = result.Value;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(dependencies, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Index for snapshot {snapshotId}: {Labels.Of(dependencies.Index.Status)}");
        WriteIndex(output, dependencies.Index);
        output.WriteLine($"Member {dependencies.Member}: coverage {Labels.Of(dependencies.Coverage)}; {dependencies.TotalEdges} direct edge(s), {dependencies.UnresolvedEdges} unresolved"
            + (dependencies.Truncated ? $"; showing the first {dependencies.Edges.Count} (truncated)." : "."));
        foreach (var edge in dependencies.Edges)
        {
            var source = edge.SourceKind == ArtifactKind.Member ? string.Empty : $" from {Labels.Of(edge.SourceKind)} {EscapeForTerminal(edge.SourceName)}";
            var resolution = edge.Resolution == ResolutionStatus.Resolved
                ? $"resolved to {(edge.Kind == EdgeKind.KeysField ? $"field in {edge.ResolvedMember}" : edge.ResolvedMember!.ToString())}"
                : $"unresolved ({Labels.Of(edge.Reason!.Value)}{(edge.CandidateCount > 1 ? $", {edge.CandidateCount} candidates" : string.Empty)})";
            var assumption = edge.AssumedCallerLibrary ? " [unqualified: assumed caller's manifest library]" : string.Empty;
            output.WriteLine($"  line {edge.ReferenceEvidence.LineNumber}  {Labels.Of(edge.Kind)}{source} -> {EscapeForTerminal(edge.Target)}  {resolution}{assumption}");
            output.WriteLine($"      reference  {edge.ReferenceEvidence}");
            if (edge.DefinitionEvidence is not null)
            {
                output.WriteLine($"      definition {edge.DefinitionEvidence}");
            }
        }

        return Ok;
    }

    private static int PrepareScan(ArchivePreparer preparer, Dictionary<string, string?> options, TextWriter output, CancellationToken cancellationToken)
    {
        foreach (var required in new[] { "source", "layout", "plan" })
        {
            if (!options.ContainsKey(required))
            {
                return UsageError(output, $"--{required} is required.");
            }
        }

        if (options["layout"] != PreparationFormats.ArchivedLibraryTree)
        {
            return Reject(output, new Diagnostic(DiagnosticCode.InvalidArgument, $"--layout must be '{PreparationFormats.ArchivedLibraryTree}' (the only F1.5 layout)."));
        }

        var invalid = new List<Diagnostic>();
        var ccsid = ParseLong(options, "ccsid", invalid, "a CCSID number");
        var width = ParseLong(options, "record-width", invalid, "a record width");
        RecordLayout? layout = options.GetValueOrDefault("record-layout") switch
        {
            null => null,
            "lines" => RecordLayout.Lines,
            "fixed-source-data" => RecordLayout.FixedSourceData,
            _ => Invalid(invalid, "--record-layout must be 'lines' or 'fixed-source-data'."),
        };
        if (invalid.Count > 0)
        {
            return Reject(output, [.. invalid]);
        }

        var defaults = new ScanDefaults(ccsid is { } c ? (int)Math.Clamp(c, 0, int.MaxValue) : null, layout, width is { } w ? (int)Math.Clamp(w, 0, int.MaxValue) : null);
        var result = preparer.Scan(options["source"]!, options["plan"]!, defaults, cancellationToken);
        if (!result.Succeeded)
        {
            output.WriteLine("Scan rejected; no plan was written.");
            return Reject(output, [.. result.Diagnostics]);
        }

        var scan = result.Value;
        if (options.ContainsKey("json"))
        {
            output.WriteLine(JsonSerializer.Serialize(scan, JsonOutput));
            return Ok;
        }

        var counts = scan.Counts;
        output.WriteLine($"Wrote private preparation plan {scan.PlanPath} (sha256 {scan.PlanSha256}).");
        output.WriteLine($"  discovered {counts.Discovered}: included {counts.Included}, skipped {counts.Skipped}; projected input bytes {counts.ProjectedBytes}");
        output.WriteLine($"  included by type: {Describe(counts.IncludedByType)}");
        output.WriteLine($"  skipped by reason: {Describe(counts.SkippedByReason)}");
        output.WriteLine($"  {counts.Unconfirmed} included entr{(counts.Unconfirmed == 1 ? "y is" : "ies are")} unconfirmed. Review each entry's ccsid, recordLayout,");
        output.WriteLine("  and recordWidth, set \"confirmed\": true, then run 'prepare build'. Unconfirmed entries are not converted.");
        return Ok;
    }

    private static int PrepareBuild(ArchivePreparer preparer, Dictionary<string, string?> options, TextWriter output, CancellationToken cancellationToken)
    {
        foreach (var required in new[] { "source", "plan", "output" })
        {
            if (!options.ContainsKey(required))
            {
                return UsageError(output, $"--{required} is required.");
            }
        }

        var result = preparer.Build(options["source"]!, options["plan"]!, options["output"]!, cancellationToken);
        if (!result.Succeeded)
        {
            output.WriteLine("Preparation rejected; no export was published.");
            return Reject(output, [.. result.Diagnostics]);
        }

        var build = result.Value;
        if (options.ContainsKey("json"))
        {
            output.WriteLine(JsonSerializer.Serialize(build, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Prepared export {build.OutputPath}: {Labels.Of(build.Status)} (preparation run {build.PreparationRunId})");
        output.WriteLine($"  plan entries {build.PlanEntries}: included {build.Included} (prepared {build.Prepared}, rejected {build.Rejected}), skipped {build.Skipped}");
        output.WriteLine($"  input bytes {build.InputBytes}, prepared bytes {build.PreparedBytes}");
        output.WriteLine($"  prepared by type: {Describe(build.PreparedByType)}");
        output.WriteLine($"  rejected by reason: {Describe(build.RejectedByReason)}");
        output.WriteLine($"  skipped by reason: {Describe(build.SkippedByReason)}");
        output.WriteLine($"  details per entry are in the private {PreparationFormats.PlanReportFileName}; import with 'ibmi-explorer import --source <output>'.");
        return Ok;
    }

    private static RecordLayout? Invalid(List<Diagnostic> diagnostics, string message)
    {
        diagnostics.Add(new(DiagnosticCode.InvalidArgument, message));
        return null;
    }

    private static string Describe(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"));

    private static int Gaps(SourceIndexService indexes, WorkspaceId workspaceId, SnapshotId snapshotId, bool json, TextWriter output)
    {
        var result = indexes.SyntaxGaps(workspaceId, snapshotId);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        var gaps = result.Value;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(gaps, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Private syntax-gap report for snapshot {snapshotId} (unsupported CL commands and DDS keywords; not indexed facts):");
        WriteIndex(output, gaps.Index);
        foreach (var group in gaps.Report.Groups)
        {
            output.WriteLine($"  {Labels.Of(group.Kind),-11} {EscapeForTerminal(group.Key),-12} {group.Occurrences} occurrence(s) in {group.Members} member(s)");
            foreach (var example in group.Examples)
            {
                output.WriteLine($"      e.g. {example}");
            }
        }

        if (gaps.Report.OmittedGroups > 0)
        {
            output.WriteLine($"  {gaps.Report.OmittedGroups} less frequent group(s) with {gaps.Report.OmittedOccurrences} occurrence(s) omitted (limit {SyntaxGapReport.MaxGroupsPerKind} per kind).");
        }

        return Ok;
    }

    private static int? Clamp(long? value) => value is { } v ? (int)Math.Clamp(v, int.MinValue, int.MaxValue) : null;

    private static void WriteIndex(TextWriter output, IndexSummary index)
    {
        var p = index.Provenance;
        var c = index.Counts;
        output.WriteLine($"  index run {p.AnalysisRunId}, created {index.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)}");
        output.WriteLine($"  parser version {p.ParserVersion}, index format {p.IndexFormatVersion}");
        output.WriteLine($"  members {c.Members}: complete {c.CompleteMembers}, partial {c.PartialMembers}, unsupported {c.UnsupportedMembers}");
        output.WriteLine($"  artifacts {c.Artifacts}, edges {c.Edges} (resolved {c.ResolvedEdges}, unresolved {c.UnresolvedEdges})");
        output.WriteLine($"  diagnostics {c.Diagnostics} ({c.DiagnosticsShown} shown, {c.SuppressedDiagnostics} suppressed)"
            + (index.DiagnosticsByCode.Count > 0 ? $": {Describe(index.DiagnosticsByCode)}" : string.Empty));
        foreach (var type in index.ByType)
        {
            output.WriteLine($"  {type.SourceType,-3} members {type.Members}: complete {type.Complete}, partial {type.Partial}, unsupported {type.Unsupported}; diagnostics {type.Diagnostics} ({type.SuppressedDiagnostics} suppressed)");
        }

        var l = index.Limits;
        if (l != IndexLimits.Default)
        {
            output.WriteLine($"  raised limits: total bytes {l.MaxTotalBytes}, members {l.MaxMembers}, facts and edges {l.MaxItems}");
        }
    }

    internal static Diagnostic? ParseIdentity(Dictionary<string, string?> options, out MemberIdentity? identity)
    {
        identity = null;
        var library = options["library"];
        var sourceFile = options["source-file"];
        var memberName = options["member"];
        if (!MemberIdentity.IsValidName(library) || !MemberIdentity.IsValidName(sourceFile) || !MemberIdentity.IsValidName(memberName))
        {
            return new Diagnostic(DiagnosticCode.InvalidIdentityName, "Library, source file, and member must be IBM i system names.");
        }

        if (!SourceTypes.TryParse(options["type"], out var sourceType))
        {
            return new Diagnostic(DiagnosticCode.UnsupportedSourceType, "Type must be one of RPG, CL, DDS, SQL.");
        }

        identity = new MemberIdentity(library!, sourceFile!, memberName!, sourceType);
        return null;
    }

    internal static Diagnostic InvalidSnapshot() =>
        new(DiagnosticCode.InvalidSnapshotId, "Snapshot ID must be 'snap-' followed by 32 lowercase hex digits.");

    private static void WriteSnapshot(TextWriter output, SnapshotSummary snapshot)
    {
        var limits = snapshot.AppliedLimits;
        output.WriteLine($"  created {snapshot.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)}, analysis run {snapshot.AnalysisRunId}");
        output.WriteLine($"  import format {snapshot.ImportFormatVersion}, parser version {snapshot.ParserVersion}");
        output.WriteLine($"  applied limits: max member bytes {limits.MaxMemberBytes}, max import bytes {limits.MaxImportBytes}, max members {limits.MaxMembers}");
        output.WriteLine($"  {snapshot.Members.Count} member(s), {snapshot.TotalBytes} bytes");
        foreach (var member in snapshot.Members)
        {
            output.WriteLine($"  - {member.Identity}  sha256 {member.Sha256}  {member.ByteLength} bytes  {member.LineCount} line(s)");
        }
    }

    private static void WriteLimits(TextWriter output)
    {
        output.WriteLine("Import limits (default / upper bound):");
        output.WriteLine($"  --max-member-bytes  {ImportLimits.DefaultMaxMemberBytes} / {ImportLimits.UpperBoundMemberBytes}");
        output.WriteLine($"  --max-import-bytes  {ImportLimits.DefaultMaxImportBytes} / {ImportLimits.UpperBoundImportBytes}");
        output.WriteLine($"  --max-members       {ImportLimits.DefaultMaxMembers} / {ImportLimits.UpperBoundMembers}");
        output.WriteLine($"  manifest size       {ImportLimits.MaxManifestBytes} (fixed)");
        var index = IndexLimits.Default;
        output.WriteLine("Index limits (default / opt-in ceiling):");
        output.WriteLine($"  bytes per member                        {index.MaxMemberBytes} (fixed)");
        output.WriteLine($"  --max-index-bytes   total source bytes  {index.MaxTotalBytes} / {IndexLimits.UpperBoundTotalBytes}");
        output.WriteLine($"  --max-index-members members             {index.MaxMembers} / {IndexLimits.UpperBoundMembers}");
        output.WriteLine($"  --max-index-items   facts and edges     {index.MaxItems} / {IndexLimits.UpperBoundItems}");
        output.WriteLine($"  query results                           {index.MaxQueryResults} (fixed)");
        output.WriteLine($"  stored diagnostics per member           {SourceParser.MaxDiagnosticsPerMember} (fixed; totals by code are always kept)");
        output.WriteLine($"  syntax-gap groups per kind              {SyntaxGapReport.MaxGroupsPerKind} (fixed, {SyntaxGapReport.MaxExamples} examples each)");
        var prepare = PreparationLimits.Default;
        output.WriteLine("Preparation limits (fixed):");
        output.WriteLine($"  bytes per member    {prepare.MaxMemberBytes}");
        output.WriteLine($"  total bytes         {prepare.MaxTotalBytes}");
        output.WriteLine($"  archive entries     {prepare.MaxEntries}");
        output.WriteLine($"  plan file bytes     {PreparationLimits.MaxPlanBytes}");
    }

    /// <summary>Source text is untrusted: render control characters visibly so they cannot drive the terminal.</summary>
    internal static string EscapeForTerminal(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value != '\t' && (Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    private static bool TryParseOptions(ReadOnlySpan<string> args, string[] allowed, out Dictionary<string, string?> options, out string error)
    {
        options = new Dictionary<string, string?>(StringComparer.Ordinal);
        error = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var name = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : null;
            if (name is null || !allowed.Contains(name, StringComparer.Ordinal))
            {
                error = $"Unexpected argument '{ManifestParser.ForDisplay(arg)}'.";
                return false;
            }

            if (options.ContainsKey(name))
            {
                error = $"Option --{name} was given more than once.";
                return false;
            }

            if (Flags.Contains(name, StringComparer.Ordinal))
            {
                options[name] = null;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"Option --{name} requires a value.";
                return false;
            }

            options[name] = args[++i];
        }

        return true;
    }

    private static long? ParseLong(Dictionary<string, string?> options, string name, List<Diagnostic> diagnostics, string? what = null)
    {
        if (!options.TryGetValue(name, out var text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        diagnostics.Add(what is null
            ? new(DiagnosticCode.InvalidLimit, $"--{name} must be a positive whole number of {(name.EndsWith("members", StringComparison.Ordinal) ? "members" : name.EndsWith("items", StringComparison.Ordinal) ? "facts and edges" : "bytes")}.")
            : new(DiagnosticCode.InvalidArgument, $"--{name} must be {what}."));
        return null;
    }

    private static bool TryParseRange(string? text, out LineRange? range)
    {
        range = null;
        var parts = (text ?? string.Empty).Split('-');
        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            || first < 1)
        {
            return false;
        }

        var last = first;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first))
        {
            return false;
        }

        range = new LineRange(first, last);
        return true;
    }

    private static int Reject(TextWriter output, params Diagnostic[] diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            output.WriteLine($"error {diagnostic}");
        }

        return Rejected;
    }

    private static int UsageError(TextWriter output, string message)
    {
        output.WriteLine($"usage error: {message}");
        output.WriteLine("Run 'ibmi-explorer help' for usage.");
        return Usage;
    }

    private const string HelpText = """
        IBM i Evidence Explorer — local IBM i source preparation (F1.5), snapshots (F0), a deterministic source index (F1/F1.5),
        and a cited member explorer (F2): 'explore' never calls a model; 'explain' sends one bounded packet to a
        locally installed Ollama model on 127.0.0.1 only after --acknowledge-local-service.

        Usage:
          ibmi-explorer prepare scan  --source <archive-dir> --layout archived-library-tree --plan <new-plan.json>
                                [--ccsid <1208|37|1140>] [--record-layout <lines|fixed-source-data>] [--record-width 80] [--json]
          ibmi-explorer prepare build --source <archive-dir> --plan <plan.json> --output <new-dir> [--json]
          ibmi-explorer import  --workspace <id> --source <dir> [--storage <dir>]
                                [--max-member-bytes <n>] [--max-import-bytes <n>] [--max-members <n>] [--json]
          ibmi-explorer list    --workspace <id> [--storage <dir>] [--json]
          ibmi-explorer inspect --workspace <id> --snapshot <snapshot-id> --library <lib> --source-file <file>
                                --member <mbr> --type <RPG|CL|DDS|SQL> [--lines <n>|<n-m>] [--storage <dir>] [--json]
          ibmi-explorer index   --workspace <id> --snapshot <snapshot-id> [--storage <dir>]
                                [--max-index-bytes <n>] [--max-index-members <n>] [--max-index-items <n>] [--json]
          ibmi-explorer gaps    --workspace <id> --snapshot <snapshot-id> [--storage <dir>] [--json]
          ibmi-explorer search  --workspace <id> --snapshot <snapshot-id> --query <name> [--storage <dir>] [--json]
          ibmi-explorer dependencies --workspace <id> --snapshot <snapshot-id> --library <lib> --source-file <file>
                                --member <mbr> --type <RPG|CL|DDS|SQL> [--storage <dir>] [--json]
          ibmi-explorer explore --workspace <id> --snapshot <snapshot-id> --library <lib> --source-file <file>
                                --member <mbr> --type <RPG|CL|DDS|SQL> [--storage <dir>] [--json]
          ibmi-explorer explain --workspace <id> --snapshot <snapshot-id> --library <lib> --source-file <file>
                                --member <mbr> --type <RPG|CL|DDS|SQL> --preview-context [--storage <dir>] [--json]
          ibmi-explorer explain --workspace <id> --snapshot <snapshot-id> --library <lib> --source-file <file>
                                --member <mbr> --type <RPG|CL|DDS|SQL> --model <local-model> --acknowledge-local-service
                                [--ollama-port <n>] [--timeout-seconds <n>] [--storage <dir>] [--json]
          ibmi-explorer limits
          ibmi-explorer help

        Storage defaults to $IBMI_EXPLORER_STORAGE, else ~/.ibmi-explorer.
        Command output goes to stdout; structured operation logs (no source text) go to stderr.
        Exit codes: 0 success, 1 rejected with diagnostics, 2 usage error.

        """;

    private sealed class WorkspaceIdConverter : JsonConverter<WorkspaceId>
    {
        public override WorkspaceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, WorkspaceId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    private sealed class SnapshotIdConverter : JsonConverter<SnapshotId>
    {
        public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}
