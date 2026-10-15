using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace IbmiEvidenceExplorer.Core.Exploration;

/// <summary>What will leave the process, shown before the model call and returned with the draft.</summary>
public sealed record ExplanationDisclosure(
    string Runtime,
    string Endpoint,
    string Model,
    string? ModelDigest,
    SnapshotId SnapshotId,
    MemberIdentity Member,
    int SourceLines,
    int SourceBytes,
    int PromptBytes,
    string PacketSha256,
    IReadOnlyList<string> SelectedEvidenceIds)
{
    public string Text =>
        $"Local AI disclosure: IBM i Evidence Explorer will send {SourceLines} numbered source line(s) ({SourceBytes} bytes of source) and index facts for one member "
        + $"of snapshot {SnapshotId} to the {Runtime} service at {Endpoint} for model '{Model}'. The connection is loopback-only with no proxy or redirect, "
        + "but IBM i Evidence Explorer cannot prove the local service does not forward data: the operator must control its configuration (for Ollama, disable cloud features). "
        + "The reply is a draft for human review and is not stored.";
}

/// <param name="Timeout">One deadline for the whole exchange with the local service (model check and chat together); default 180 s, at most 600 s.</param>
public sealed record ExplainRequest(WorkspaceId WorkspaceId, SnapshotId SnapshotId, MemberIdentity Member, string Model, bool LocalServiceAcknowledged, TimeSpan? Timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(600);
}

public sealed record ExplanationOutcome(ExplanationDisclosure Disclosure, ExplanationDraft Draft);

/// <summary>The exact packet and prompt an explanation would send, built without any model call.</summary>
public sealed record ContextPreview(MemberDossier Dossier, ContextPacket Packet, ExplanationPrompt Prompt)
{
    public bool ModelCalled { get; }
}

/// <summary>
/// F2 explanation orchestration, independent of any runtime SDK. <see cref="Preview"/> never touches
/// the model. <see cref="Explain"/> requires an explicit local-service acknowledgement, confirms the
/// model is installed locally, sends only the bounded packet, validates the reply strictly, and
/// re-resolves every citation through F0. Nothing is persisted. Logs carry IDs, counts, codes,
/// elapsed time, and the model identifier only.
/// </summary>
public sealed partial class ExplanationService
{
    private readonly ExplorerService _explorer;
    private readonly ISnapshotStore _snapshots;
    private readonly ILocalChatModel? _model;
    private readonly IOperationLog _log;
    private readonly TimeProvider _time;

    public ExplanationService(ExplorerService explorer, ISnapshotStore snapshots, ILocalChatModel? model, IOperationLog? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(explorer);
        ArgumentNullException.ThrowIfNull(snapshots);
        _explorer = explorer;
        _snapshots = snapshots;
        _model = model;
        _log = log ?? NullOperationLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    public OperationResult<ContextPreview> Preview(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member)
    {
        var built = Build(workspaceId, snapshotId, member);
        if (built.Succeeded)
        {
            Log(OperationLogLevel.Information, "explain.preview", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
                ("indexRunId", built.Value.Dossier.Index.Provenance.AnalysisRunId), ("packetSha256", built.Value.Packet.Sha256),
                ("evidenceLines", Text(built.Value.Packet.SourceLines)), ("promptBytes", Text(built.Value.Prompt.Bytes)), ("modelCalled", "false"));
        }

        return built;
    }

    /// <param name="beforeSend">Called with the disclosure after every check passes and immediately before the packet is sent.</param>
    public OperationResult<ExplanationOutcome> Explain(ExplainRequest request, Action<ExplanationDisclosure>? beforeSend = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = AnalysisRunId.New();
        var started = Stopwatch.GetTimestamp();
        var result = ExplainCore(request, runId, beforeSend, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        var model = ModelPattern().IsMatch(request.Model ?? string.Empty) ? request.Model : "(invalid)";
        if (result.Succeeded)
        {
            var draft = result.Value.Draft;
            Log(OperationLogLevel.Information, "explain.completed", ("workspaceId", request.WorkspaceId.Value), ("snapshotId", request.SnapshotId.Value),
                ("indexRunId", draft.Provenance.IndexRunId), ("explanationRunId", runId), ("model", model), ("modelReported", ModelPattern().IsMatch(draft.Provenance.ModelReported) ? draft.Provenance.ModelReported : "(invalid)"),
                ("packetSha256", draft.Provenance.PacketSha256), ("findingCount", Text(draft.Findings.Count)), ("unknownCount", Text(draft.Unknowns.Count)), ("elapsedMs", elapsed));
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                Log(OperationLogLevel.Warning, "explain.rejected", ("workspaceId", request.WorkspaceId.Value), ("snapshotId", request.SnapshotId.Value),
                    ("explanationRunId", runId), ("model", model), ("code", diagnostic.Code.ToString()), ("elapsedMs", elapsed));
            }
        }

        return result;
    }

    private OperationResult<ExplanationOutcome> ExplainCore(ExplainRequest request, string runId, Action<ExplanationDisclosure>? beforeSend, CancellationToken cancellationToken)
    {
        if (request.Model is null || !ModelPattern().IsMatch(request.Model))
        {
            return Fail(DiagnosticCode.InvalidArgument, "The model name must be 1-200 characters of letters, digits, and . _ : / - (as listed by the local runtime).");
        }

        if (!request.LocalServiceAcknowledged)
        {
            return Fail(DiagnosticCode.LocalAiNotAcknowledged,
                "Explanation sends source to the local model service. Confirm the service and model are local with --acknowledge-local-service; nothing was sent.");
        }

        var timeout = request.Timeout ?? ExplainRequest.DefaultTimeout;
        if (timeout <= TimeSpan.Zero || timeout > ExplainRequest.MaxTimeout)
        {
            return Fail(DiagnosticCode.InvalidArgument, "The explanation timeout must be positive and at most 600 seconds.");
        }

        if (_model is null)
        {
            return Fail(DiagnosticCode.LocalAiUnavailable, "No local model runtime is configured; nothing was sent.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Fail(DiagnosticCode.ExplainCancelled, "Explanation was cancelled; nothing was sent.");
        }

        var built = Build(request.WorkspaceId, request.SnapshotId, request.Member);
        if (!built.Succeeded)
        {
            return OperationResult<ExplanationOutcome>.Failure(built.Diagnostics);
        }

        var (dossier, packet, prompt) = (built.Value.Dossier, built.Value.Packet, built.Value.Prompt);

        // One deadline bounds the whole exchange with the local service: the model check and the chat.
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        // The availability check sends only the model name; customer material has not left the process.
        var found = _model.FindLocalModel(request.Model, linked.Token);
        if (!found.Succeeded)
        {
            return Deadline(found.Diagnostics, deadline, timeout, cancellationToken);
        }

        var evidenceIds = dossier.Evidence.Select(e => e.Id).ToList();
        var disclosure = new ExplanationDisclosure(_model.Runtime, _model.Endpoint, request.Model, found.Value.Digest, request.SnapshotId, request.Member,
            packet.SourceLines, packet.SourceBytes, prompt.Bytes, packet.Sha256, evidenceIds);
        Log(OperationLogLevel.Information, "explain.sending", ("workspaceId", request.WorkspaceId.Value), ("snapshotId", request.SnapshotId.Value),
            ("indexRunId", dossier.Index.Provenance.AnalysisRunId), ("explanationRunId", runId), ("model", request.Model), ("runtime", _model.Runtime),
            ("endpoint", _model.Endpoint), ("packetSha256", packet.Sha256), ("evidenceLines", Text(packet.SourceLines)), ("promptBytes", Text(prompt.Bytes)));
        beforeSend?.Invoke(disclosure);

        var reply = _model.Chat(new LocalChatRequest(request.Model, prompt.SystemMessage, prompt.UserMessage, prompt.OutputSchemaJson, ExplanationFormats.MaxOutputTokens), linked.Token);
        if (!reply.Succeeded)
        {
            return Deadline(reply.Diagnostics, deadline, timeout, cancellationToken);
        }

        // The reply must come from the model that was checked and disclosed (or a listed alias of it).
        if (!found.Value.Aliases.Append(request.Model).Any(name => ModelNames.Equivalent(name, reply.Value.ReportedModel)))
        {
            return Fail(DiagnosticCode.LocalAiModelMismatch, "The local service reports that a different model answered than the one requested and checked; no draft is shown.");
        }

        var parsed = ExplanationValidator.Validate(reply.Value.Content, packet);
        if (!parsed.Succeeded)
        {
            return OperationResult<ExplanationOutcome>.Failure(parsed.Diagnostics);
        }

        // Every cited reference must still match F0's reference for its line, re-read and re-hashed
        // from the same workspace and snapshot (through the reader, which logs no member identity).
        var opened = _snapshots.OpenSnapshot(request.WorkspaceId, request.SnapshotId);
        if (!opened.Succeeded)
        {
            return OperationResult<ExplanationOutcome>.Failure(opened.Diagnostics);
        }

        var cited = parsed.Value.Claims.SelectMany(c => c.Evidence).SelectMany(c => c.References).Distinct().ToList();
        foreach (var group in cited.GroupBy(r => r.Member))
        {
            var read = opened.Value.ReadMember(group.Key);
            if (!read.Succeeded)
            {
                return OperationResult<ExplanationOutcome>.Failure(read.Diagnostics);
            }

            var lines = read.Value.Lines;
            if (group.Any(r => r.SnapshotId != request.SnapshotId || r.LineNumber < 1 || r.LineNumber > lines.Count || lines[r.LineNumber - 1].Reference != r))
            {
                return Fail(DiagnosticCode.LocalAiCitationInvalid, "A cited reference no longer matches the selected snapshot.");
            }
        }

        var index = dossier.Index.Provenance;
        var provenance = new ExplanationProvenance(runId, _time.GetUtcNow(), request.WorkspaceId, request.SnapshotId, request.Member,
            index.ParserVersion, index.AnalysisRunId, index.IndexFormatVersion, ExplorerFormats.ExplorerVersion, prompt.TemplateVersion,
            ExplanationFormats.OutputSchemaVersion, ExplanationFormats.PacketFormat, packet.Sha256, _model.Runtime, _model.Endpoint,
            request.Model, found.Value.Name, reply.Value.ReportedModel, found.Value.Digest ?? "unknown");
        var claims = parsed.Value.Claims;
        var draft = new ExplanationDraft(provenance, dossier.Completeness, claims.Where(c => c.Label != ClaimLabel.Unknown).ToList(),
            claims.Where(c => c.Label == ClaimLabel.Unknown).ToList(), dossier.Gaps, evidenceIds);
        return OperationResult<ExplanationOutcome>.Success(new(disclosure, draft));
    }

    private OperationResult<ContextPreview> Build(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member)
    {
        var explored = _explorer.Explore(workspaceId, snapshotId, member);
        if (!explored.Succeeded)
        {
            return OperationResult<ContextPreview>.Failure(explored.Diagnostics);
        }

        var packet = ContextPacket.From(explored.Value);
        var prompt = packet.Prompt();
        if (prompt.Bytes > ExplanationFormats.MaxPromptBytes)
        {
            return OperationResult<ContextPreview>.Failure(new Diagnostic(DiagnosticCode.ContextTooLarge,
                $"The prompt would be {prompt.Bytes} bytes, above the {ExplanationFormats.MaxPromptBytes}-byte bound; nothing was sent."));
        }

        return OperationResult<ContextPreview>.Success(new(explored.Value, packet, prompt));
    }

    /// <summary>A cancellation caused by the explanation deadline (not the caller) is a timeout.</summary>
    private static OperationResult<ExplanationOutcome> Deadline(IReadOnlyList<Diagnostic> diagnostics, CancellationTokenSource deadline, TimeSpan timeout, CancellationToken caller) =>
        deadline.IsCancellationRequested && !caller.IsCancellationRequested && diagnostics.Any(d => d.Code == DiagnosticCode.ExplainCancelled)
            ? Fail(DiagnosticCode.LocalAiTimeout, $"The local model exchange did not finish within {timeout.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds; no draft is shown.")
            : OperationResult<ExplanationOutcome>.Failure(diagnostics);

    private static OperationResult<ExplanationOutcome> Fail(DiagnosticCode code, string message) =>
        OperationResult<ExplanationOutcome>.Failure(new Diagnostic(code, message));

    private void Log(OperationLogLevel level, string name, params (string Key, string? Value)[] fields) =>
        _log.Write(new OperationLogEvent(_time.GetUtcNow(), level, name, fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value)));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Operator-chosen model names as local runtimes list them (for example <c>name:tag</c> or <c>namespace/name:tag</c>).</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ModelPattern();
}
