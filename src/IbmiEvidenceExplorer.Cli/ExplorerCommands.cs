using System.Globalization;
using System.Text.Json;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Infrastructure;
using IbmiEvidenceExplorer.Infrastructure.Ollama;

namespace IbmiEvidenceExplorer.Cli;

/// <summary>
/// F2 commands. <c>explore</c> and <c>explain --preview-context</c> never construct a model client.
/// Only <c>explain</c> with <c>--model</c> and <c>--acknowledge-local-service</c> creates the loopback
/// Ollama adapter. Source and model text go only to stdout, terminal-escaped; logs go to stderr.
/// </summary>
public static partial class CliApp
{
    private static int Explorer(string command, FileSystemSnapshotStore store, string storage, IOperationLog log, WorkspaceId workspaceId,
        Dictionary<string, string?> options, bool json, TextWriter output, CancellationToken cancellationToken)
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

        var explorer = new ExplorerService(store, new FileSystemIndexStore(storage, log), log);
        if (command == "explore")
        {
            return Explore(explorer, workspaceId, snapshotId, identity!, json, output);
        }

        var preview = options.ContainsKey("preview-context");
        if (preview)
        {
            return Preview(new ExplanationService(explorer, store, model: null, log), workspaceId, snapshotId, identity!, json, output);
        }

        if (!options.TryGetValue("model", out var model))
        {
            return UsageError(output, "--model is required (a model already installed in the local Ollama service), or use --preview-context.");
        }

        var invalid = new List<Diagnostic>();
        var port = ParseLong(options, "ollama-port", invalid, "a port number 1-65535") ?? OllamaEndpoint.DefaultPort;
        var timeout = ParseLong(options, "timeout-seconds", invalid, $"a whole number of seconds 1-{OllamaOptions.MaxTimeout.TotalSeconds:F0}")
            ?? (long)OllamaOptions.DefaultTimeout.TotalSeconds;
        if (timeout < 1 || timeout > OllamaOptions.MaxTimeout.TotalSeconds)
        {
            invalid.Add(new(DiagnosticCode.InvalidArgument, $"--timeout-seconds must be a whole number of seconds 1-{OllamaOptions.MaxTimeout.TotalSeconds:F0}."));
        }

        if (invalid.Count > 0)
        {
            return Reject(output, [.. invalid]);
        }

        var request = new ExplainRequest(workspaceId, snapshotId, identity!, model!, options.ContainsKey("acknowledge-local-service"), TimeSpan.FromSeconds(timeout));
        if (!request.LocalServiceAcknowledged)
        {
            // Checked again in the service; failing here means no network client is even created.
            return Reject(output, new Diagnostic(DiagnosticCode.LocalAiNotAcknowledged,
                "Explanation sends source to the local model service. Confirm the service and model are local with --acknowledge-local-service; nothing was sent."));
        }

        var endpoint = OllamaEndpoint.Loopback((int)Math.Clamp(port, int.MinValue, int.MaxValue));
        if (!endpoint.Succeeded)
        {
            return Reject(output, [.. endpoint.Diagnostics]);
        }

        using var ollama = new OllamaChatModel(endpoint.Value, OllamaOptions.Default with { Timeout = TimeSpan.FromSeconds(timeout) });
        return Explain(new ExplanationService(explorer, store, ollama, log), request, json, output, cancellationToken);
    }

    private static int Explore(ExplorerService explorer, WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity identity, bool json, TextWriter output)
    {
        var result = explorer.Explore(workspaceId, snapshotId, identity);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(result.Value, JsonOutput));
            return Ok;
        }

        WriteDossier(output, result.Value);
        return Ok;
    }

    private static int Preview(ExplanationService explanations, WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity identity, bool json, TextWriter output)
    {
        var result = explanations.Preview(workspaceId, snapshotId, identity);
        if (!result.Succeeded)
        {
            return Reject(output, [.. result.Diagnostics]);
        }

        var (packet, prompt) = (result.Value.Packet, result.Value.Prompt);
        if (json)
        {
            using var parsed = JsonDocument.Parse(packet.Json);
            using var schema = JsonDocument.Parse(prompt.OutputSchemaJson);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                modelCalled = false,
                packetFormat = ExplanationFormats.PacketFormat,
                promptTemplateVersion = prompt.TemplateVersion,
                outputSchemaVersion = ExplanationFormats.OutputSchemaVersion,
                packetSha256 = packet.Sha256,
                sourceLines = packet.SourceLines,
                sourceBytes = packet.SourceBytes,
                promptBytes = prompt.Bytes,
                selectedEvidenceIds = packet.Dossier.Evidence.Select(e => e.Id).ToList(),
                completeness = packet.Dossier.Completeness,
                packet = parsed.RootElement,
                outputSchema = schema.RootElement,
                systemMessage = prompt.SystemMessage,
                userMessage = prompt.UserMessage,
            }, JsonOutput));
            return Ok;
        }

        output.WriteLine($"Context preview (no model was called) for {packet.Dossier.Identity} in snapshot {packet.Dossier.SnapshotId}: completeness {Labels.Of(packet.Dossier.Completeness)}");
        output.WriteLine($"  packet {ExplanationFormats.PacketFormat}, sha256 {packet.Sha256}; prompt {prompt.TemplateVersion}, {prompt.Bytes} bytes");
        output.WriteLine($"  {packet.SourceLines} source line(s), {packet.SourceBytes} bytes; {packet.Dossier.EvidenceSelection.OmittedLines} candidate line(s) omitted by the bound");
        output.WriteLine("  'explain --model <m> --acknowledge-local-service' sends exactly these two messages (rebuilt from the same index) to the local model.");
        output.WriteLine("----- system message -----");
        WriteLines(output, prompt.SystemMessage);
        output.WriteLine("----- user message -----");
        WriteLines(output, prompt.UserMessage);
        output.WriteLine("----- end -----");
        return Ok;
    }

    private static int Explain(ExplanationService explanations, ExplainRequest request, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        var result = explanations.Explain(request, json ? null : disclosure =>
        {
            output.WriteLine(EscapeForTerminal(disclosure.Text));
            output.WriteLine($"  sending evidence {string.Join(", ", disclosure.SelectedEvidenceIds)} (packet sha256 {disclosure.PacketSha256}, prompt {disclosure.PromptBytes} bytes)");
            output.Flush();
        }, cancellationToken);
        if (!result.Succeeded)
        {
            output.WriteLine("Explanation failed; no draft is shown.");
            return Reject(output, [.. result.Diagnostics]);
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { disclosure = result.Value.Disclosure, explanation = result.Value.Draft }, JsonOutput));
            return Ok;
        }

        WriteDraft(output, result.Value.Draft);
        return Ok;
    }

    private static void WriteDossier(TextWriter output, MemberDossier d)
    {
        var p = d.Index.Provenance;
        output.WriteLine($"Explorer dossier for {d.Identity} in snapshot {d.SnapshotId}: completeness {Labels.Of(d.Completeness)} (deterministic; no model was called)");
        output.WriteLine($"  index run {p.AnalysisRunId}, created {d.Index.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)}, status {Labels.Of(d.Index.Status)}");
        output.WriteLine($"  parser version {p.ParserVersion}, index format {p.IndexFormatVersion}, explorer {d.ExplorerVersion}, snapshot import run {d.SnapshotAnalysisRunId}");
        output.WriteLine($"  member sha256 {d.Member.Sha256}, {d.Member.LineCount} line(s), coverage {Labels.Of(d.Coverage)}; diagnostics "
            + (d.DiagnosticTotals.Count == 0 ? "none" : Describe(d.DiagnosticTotals)) + (d.SuppressedDiagnostics > 0 ? $" ({d.SuppressedDiagnostics} suppressed from the sample)" : string.Empty));
        var r = d.Rederivation;
        output.WriteLine($"  re-derived from source with {r.ParserVersion}: selected member {(r.SelectedMemberReparsed ? "yes" : "no")}, displayed outgoing {r.OutgoingEdgesRederived}, displayed incoming {r.IncomingEdgesRederived}; "
            + $"omitted outgoing without target check {r.OutgoingEdgesWithoutTargetDefinition}, omitted incoming not re-derived {r.IncomingEdgesNotRederived}; {r.MembersReparsed} member(s) re-parsed");

        if (d.Definition is { } definition)
        {
            output.WriteLine($"Definition {definition.FactId}: member defined at line {definition.Artifact.Evidence.LineNumber}  {Ids(definition.EvidenceId)}");
            output.WriteLine($"      evidence {definition.Artifact.Evidence}");
        }
        else
        {
            output.WriteLine("Definition: none in the index (see gaps).");
        }

        WriteRelationships(output, "Outgoing", d.Outgoing, d.OutgoingCounts);
        WriteRelationships(output, "Incoming", d.Incoming, d.IncomingCounts);

        foreach (var diagnostic in d.Diagnostics)
        {
            var line = diagnostic.Evidence is { } evidence ? $"line {evidence.LineNumber}" : "member";
            output.WriteLine($"  diagnostic {line} {Labels.Of(diagnostic.Code)}: {diagnostic.Message}");
        }

        var s = d.EvidenceSelection;
        output.WriteLine($"Evidence: {s.IncludedLines} line(s), {s.IncludedBytes} bytes; {s.OmittedLines} of {s.CandidateLines} candidate line(s) omitted (bounds {s.MaxLines} lines / {s.MaxBytes} bytes)");
        foreach (var line in d.Evidence)
        {
            var member = line.Reference.Member == d.Identity ? string.Empty : $" {line.Reference.Member}";
            output.WriteLine($"  {line.Id,-4}{member} L{line.Reference.LineNumber.ToString(CultureInfo.InvariantCulture),-5} {EscapeForTerminal(line.Text)}");
        }

        output.WriteLine("Gaps:");
        foreach (var gap in d.Gaps)
        {
            output.WriteLine($"  - [{gap.Code}] {EscapeForTerminal(gap.Text)}");
        }

        output.WriteLine("Notes:");
        foreach (var note in d.Notes)
        {
            output.WriteLine($"  - {note}");
        }

        output.WriteLine($"Inspect any line: ibmi-explorer inspect --workspace <id> --snapshot {d.SnapshotId} --library <lib> --source-file <file> --member <mbr> --type <type> --lines <n>");
    }

    private static void WriteRelationships(TextWriter output, string title, IReadOnlyList<DossierRelationship> relationships, RelationshipCounts counts)
    {
        output.WriteLine($"{title}: {counts.Total} direct edge(s), {counts.Unresolved} unresolved"
            + (counts.Omitted > 0 ? $"; showing the first {counts.Included} ({counts.Omitted} omitted)." : "."));
        foreach (var relationship in relationships)
        {
            var edge = relationship.Edge;
            var source = edge.SourceKind == ArtifactKind.Member ? string.Empty : $" from {Labels.Of(edge.SourceKind)} {EscapeForTerminal(edge.SourceName)}";
            var from = relationship.Direction == EdgeDirection.Incoming ? $" in {edge.SourceMember}" : string.Empty;
            var resolution = edge.Resolution == ResolutionStatus.Resolved
                ? $"resolved to {(edge.Kind == EdgeKind.KeysField ? $"field in {edge.ResolvedMember}" : edge.ResolvedMember!.ToString())}"
                : $"unresolved ({Labels.Of(edge.Reason!.Value)}{(edge.CandidateCount > 1 ? $", {edge.CandidateCount} candidates" : string.Empty)})";
            output.WriteLine($"  {relationship.FactId,-4} line {edge.ReferenceEvidence.LineNumber}{from}  {Labels.Of(edge.Kind)}{source} -> {EscapeForTerminal(edge.Target)}  {resolution}  [{relationship.Observation}; {relationship.Basis}]  {Ids(relationship.ReferenceEvidenceId, relationship.DefinitionEvidenceId)}");
            output.WriteLine($"      reference  {edge.ReferenceEvidence}");
            if (edge.DefinitionEvidence is not null)
            {
                output.WriteLine($"      definition {edge.DefinitionEvidence}");
            }
        }
    }

    private static void WriteDraft(TextWriter output, ExplanationDraft draft)
    {
        var p = draft.Provenance;
        output.WriteLine();
        output.WriteLine($"DRAFT explanation ({draft.Status}) for {p.Member} in snapshot {p.SnapshotId}: completeness {Labels.Of(draft.Completeness)}");
        output.WriteLine($"  {draft.ReviewNotice}");
        output.WriteLine($"  explanation run {p.ExplanationRunId}, created {p.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)}");
        output.WriteLine($"  model requested {EscapeForTerminal(p.ModelRequested)}, listed {EscapeForTerminal(p.ModelListed)}, reported {EscapeForTerminal(p.ModelReported)}, digest {EscapeForTerminal(p.ModelDigest)} ({p.Runtime} at {p.Endpoint})");
        output.WriteLine($"  index run {p.IndexRunId}, parser {p.ParserVersion}, explorer {p.ExplorerVersion}, prompt {p.PromptTemplateVersion}, packet sha256 {p.PacketSha256}");
        output.WriteLine($"Overview (written by IBM i Evidence Explorer from the validated claims, not by the model): {draft.Overview}");
        output.WriteLine("Findings (each cites verified evidence; the prose is the model's and needs review):");
        if (draft.Findings.Count == 0)
        {
            output.WriteLine("  none");
        }

        foreach (var claim in draft.Findings)
        {
            output.WriteLine($"  [{Labels.Of(claim.Label)}] {EscapeForTerminal(claim.Text)}");
            foreach (var citation in claim.Evidence)
            {
                foreach (var reference in citation.References)
                {
                    output.WriteLine($"      {citation.Id,-4} {reference}");
                }
            }
        }

        if (draft.Unknowns.Count > 0)
        {
            output.WriteLine("Unknowns stated by the model (unverified, uncited; not findings):");
            foreach (var claim in draft.Unknowns)
            {
                output.WriteLine($"  [unknown] {EscapeForTerminal(claim.Text)}");
            }
        }

        output.WriteLine("Gaps from the dossier:");
        foreach (var gap in draft.DossierGaps)
        {
            output.WriteLine($"  - [{gap.Code}] {EscapeForTerminal(gap.Text)}");
        }

        output.WriteLine($"Inspect any citation: ibmi-explorer inspect --workspace {p.WorkspaceId} --snapshot {p.SnapshotId} --library <lib> --source-file <file> --member <mbr> --type <type> --lines <n>");
    }

    private static string Ids(params string?[] ids)
    {
        var present = ids.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return present.Count == 0 ? "[line not in packet]" : $"[{string.Join(", ", present)}]";
    }

    /// <summary>Writes multi-line text one escaped line at a time, so only real line breaks reach the terminal.</summary>
    private static void WriteLines(TextWriter output, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            output.WriteLine(EscapeForTerminal(line));
        }
    }
}
