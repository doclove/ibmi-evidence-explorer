using System.Text.Json;

namespace IbmiEvidenceExplorer.Core.Exploration;

public enum ClaimLabel
{
    Observed,
    Inference,
    Unknown,
}

/// <summary>A packet ID cited by a claim, with the F0 evidence references it stands for.</summary>
public sealed record ClaimCitation(string Id, CitationKind Kind, IReadOnlyList<EvidenceReference> References);

public sealed record ExplanationClaim(ClaimLabel Label, string Text, IReadOnlyList<ClaimCitation> Evidence);

/// <summary>Everything needed to tell which snapshot, index, prompt, and model produced a draft.</summary>
public sealed record ExplanationProvenance(
    string ExplanationRunId,
    DateTimeOffset CreatedUtc,
    WorkspaceId WorkspaceId,
    SnapshotId SnapshotId,
    MemberIdentity Member,
    string ParserVersion,
    string IndexRunId,
    string IndexFormatVersion,
    string ExplorerVersion,
    string PromptTemplateVersion,
    string OutputSchemaVersion,
    string PacketFormat,
    string PacketSha256,
    string Runtime,
    string Endpoint,
    string ModelRequested,
    string ModelListed,
    string ModelReported,
    string ModelDigest);

/// <summary>
/// A validated AI draft. <see cref="Findings"/> are the model's <c>observed</c> and
/// <c>inference</c> claims, each citing packet evidence that was re-verified through F0.
/// <see cref="Unknowns"/> are the model's <c>unknown</c> claims: uncited, unverified statements of
/// missing evidence, never findings. <see cref="Overview"/> is written by IBM i Evidence Explorer from the
/// validated claims and the dossier, not by the model. Validation proves structure and citation
/// integrity; it cannot prove that cited prose is true, so every draft requires human review and is
/// never stored or cached.
/// </summary>
public sealed record ExplanationDraft(
    ExplanationProvenance Provenance,
    DossierCompleteness Completeness,
    IReadOnlyList<ExplanationClaim> Findings,
    IReadOnlyList<ExplanationClaim> Unknowns,
    IReadOnlyList<DossierGap> DossierGaps,
    IReadOnlyList<string> SelectedEvidenceIds)
{
    public string Status { get; } = "draft-requires-human-review";

    public string Overview =>
        $"{Findings.Count(c => c.Label == ClaimLabel.Observed)} observed and {Findings.Count(c => c.Label == ClaimLabel.Inference)} inference claim(s), each citing verified evidence; "
        + $"{Unknowns.Count} unknown(s) stated by the model without verification; dossier completeness {Parsing.Labels.Of(Completeness)} with {DossierGaps.Count} gap(s)."
        + (Findings.Count == 0 ? " The model made no cited claim, so this draft explains nothing." : string.Empty);

    public string ReviewNotice { get; } =
        "AI-generated draft from a local model. Only cited claims are findings; their citations were checked against the sent packet and the snapshot, but their prose was not verified. Unknowns are unverified model text. This does not certify business behavior.";
}

/// <summary>A model reply parsed and checked against one packet, before F0 re-resolution.</summary>
public sealed record ParsedExplanation(IReadOnlyList<ExplanationClaim> Claims);

/// <summary>
/// Strict validation of a model reply against <see cref="ContextPacket.OutputSchemaJson"/> and the
/// packet it answered. Every rule is checked here whether or not the runtime enforced the schema
/// while decoding. Any violation rejects the whole reply with a typed diagnostic; nothing is
/// repaired, relabeled, or replaced. Diagnostic messages never quote the reply.
/// </summary>
public static class ExplanationValidator
{
    /// <summary>Upper bound on reply content, well above what the schema's limits allow.</summary>
    public const int MaxContentChars = 64 * 1024;

    private static readonly string[] RootProperties = ["schemaVersion", "status", "snapshotId", "member", "claims"];
    private static readonly string[] ClaimProperties = ["label", "text", "evidence"];

    public static OperationResult<ParsedExplanation> Validate(string content, ContextPacket packet)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(packet);
        if (content.Length > MaxContentChars)
        {
            return Invalid(DiagnosticCode.LocalAiInvalidOutput, $"The model reply exceeds {MaxContentChars} characters.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException)
        {
            return Invalid(DiagnosticCode.LocalAiInvalidOutput, "The model reply is not a single valid JSON document.");
        }

        using (document)
        {
            return ValidateRoot(document.RootElement, packet);
        }
    }

    private static OperationResult<ParsedExplanation> ValidateRoot(JsonElement root, ContextPacket packet)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, RootProperties))
        {
            return Invalid(DiagnosticCode.LocalAiInvalidOutput, "The model reply does not have exactly the schema's properties.");
        }

        if (String(root, "schemaVersion") != ExplanationFormats.OutputSchemaVersion)
        {
            return Invalid(DiagnosticCode.LocalAiInvalidOutput, $"The model reply is not schema {ExplanationFormats.OutputSchemaVersion}.");
        }

        switch (String(root, "status"))
        {
            case "answered":
                break;
            case "declined":
                return Invalid(DiagnosticCode.LocalAiRefused, "The model declined to produce an explanation; no draft is shown.");
            default:
                return Invalid(DiagnosticCode.LocalAiInvalidOutput, "The model reply has an invalid status.");
        }

        if (String(root, "snapshotId") != packet.Dossier.SnapshotId.Value || String(root, "member") != packet.MemberKey)
        {
            return Invalid(DiagnosticCode.LocalAiIdentityMismatch, "The model reply names a different snapshot or member than the packet it was sent.");
        }

        var claimsElement = root.GetProperty("claims");
        if (claimsElement.ValueKind != JsonValueKind.Array || claimsElement.GetArrayLength() is < 1 or > ExplanationFormats.MaxClaims)
        {
            return Invalid(DiagnosticCode.LocalAiInvalidOutput, $"An answered reply must have 1-{ExplanationFormats.MaxClaims} claims.");
        }

        var claims = new List<ExplanationClaim>();
        var number = 0;
        foreach (var element in claimsElement.EnumerateArray())
        {
            number++;
            var claim = ValidateClaim(element, number, packet);
            if (!claim.Succeeded)
            {
                return OperationResult<ParsedExplanation>.Failure(claim.Diagnostics);
            }

            claims.Add(claim.Value);
        }

        return OperationResult<ParsedExplanation>.Success(new(claims));
    }

    private static OperationResult<ExplanationClaim> ValidateClaim(JsonElement element, int number, ContextPacket packet)
    {
        if (element.ValueKind != JsonValueKind.Object || !HasExactly(element, ClaimProperties))
        {
            return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiInvalidOutput, $"Claim {number} does not have exactly label, text, and evidence.");
        }

        ClaimLabel? label = String(element, "label") switch
        {
            "observed" => ClaimLabel.Observed,
            "inference" => ClaimLabel.Inference,
            "unknown" => ClaimLabel.Unknown,
            _ => null,
        };
        if (label is null)
        {
            return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiInvalidOutput, $"Claim {number} has an invalid label.");
        }

        var text = String(element, "text");
        if (string.IsNullOrWhiteSpace(text) || text.Length > ExplanationFormats.MaxClaimChars)
        {
            return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiInvalidOutput, $"Claim {number} text must be 1-{ExplanationFormats.MaxClaimChars} characters.");
        }

        var evidence = element.GetProperty("evidence");
        if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > ExplanationFormats.MaxEvidencePerClaim
            || evidence.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiInvalidOutput, $"Claim {number} evidence must be at most {ExplanationFormats.MaxEvidencePerClaim} ID strings.");
        }

        var ids = evidence.EnumerateArray().Select(e => e.GetString()!).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiInvalidOutput, $"Claim {number} repeats an evidence ID.");
        }

        if (label != ClaimLabel.Unknown && ids.Count == 0)
        {
            return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiCitationInvalid, $"Claim {number} is labeled {Parsing.Labels.Of(label.Value)} but cites no evidence.");
        }

        var citations = new List<ClaimCitation>();
        foreach (var id in ids)
        {
            if (!packet.Citations.TryGetValue(id, out var citation))
            {
                return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiCitationInvalid, $"Claim {number} cites an ID that is not in the packet that was sent.");
            }

            if (label == ClaimLabel.Observed && citation.Kind != CitationKind.Fact)
            {
                return Invalid<ExplanationClaim>(DiagnosticCode.LocalAiCitationInvalid, $"Claim {number} is labeled observed but cites a source line; observed claims may cite only verified F1 facts.");
            }

            citations.Add(new(citation.Id, citation.Kind, citation.References));
        }

        return OperationResult<ExplanationClaim>.Success(new(label.Value, text, citations));
    }

    private static bool HasExactly(JsonElement element, string[] names)
    {
        var seen = element.EnumerateObject().Select(p => p.Name).ToList();
        return seen.Count == names.Length && names.All(n => seen.Contains(n, StringComparer.Ordinal));
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static OperationResult<ParsedExplanation> Invalid(DiagnosticCode code, string message) => Invalid<ParsedExplanation>(code, message);

    private static OperationResult<T> Invalid<T>(DiagnosticCode code, string message) => OperationResult<T>.Failure(new Diagnostic(code, message));
}
