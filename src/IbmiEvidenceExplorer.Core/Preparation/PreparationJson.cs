using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IbmiEvidenceExplorer.Core.Preparation;

/// <summary>
/// Strict JSON for plans and reports: camelCase, kebab-case enum labels (source types keep their
/// manifest labels), no unknown or duplicate properties, no integer enums, and nullability enforced.
/// </summary>
public static class PreparationJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
        Converters =
        {
            new JsonStringEnumConverter<SourceType>(namingPolicy: null, allowIntegerValues: false),
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false),
        },
    };

    /// <summary>
    /// Parses and structurally validates a plan. Choices inside entries (CCSID, layout, confirmation)
    /// are judged per entry at build time, so one bad entry never hides the others.
    /// </summary>
    public static OperationResult<PreparationPlan> ParsePlan(ReadOnlySpan<byte> utf8Json)
    {
        PreparationPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<PreparationPlan>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            // The exception message can quote input; report position only.
            return Invalid($"Plan is not valid plan JSON (line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1}): " +
                "unknown or duplicate fields, missing required fields, nulls, and unknown labels are rejected.");
        }

        if (plan is null || plan.Entries is null || plan.Entries.Any(e => e is null || e.Path is null))
        {
            return Invalid("Plan is missing its entries or an entry path.");
        }

        if (plan.FormatVersion != PreparationFormats.PlanFormatVersion || plan.Layout != PreparationFormats.ArchivedLibraryTree
            || plan.TransformVersion != PreparationFormats.TransformVersion)
        {
            return Invalid($"Plan must have formatVersion {PreparationFormats.PlanFormatVersion}, layout '{PreparationFormats.ArchivedLibraryTree}', " +
                $"and transformVersion '{PreparationFormats.TransformVersion}'; re-run 'prepare scan'.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (plan.Entries.Any(e => !paths.Add(e.Path)))
        {
            return Invalid("Plan lists the same path more than once.");
        }

        return OperationResult<PreparationPlan>.Success(plan);
    }

    private static OperationResult<PreparationPlan> Invalid(string message) =>
        OperationResult<PreparationPlan>.Failure(new Diagnostic(DiagnosticCode.PreparationPlanInvalid, message));
}
