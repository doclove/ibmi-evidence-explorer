using System.Text.Encodings.Web;
using System.Text.Json;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Cli;

/// <summary>Writes structured operation events as one JSON object per line.</summary>
public sealed class JsonLinesOperationLog(TextWriter writer) : IOperationLog
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Write(OperationLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var record = new Dictionary<string, string?>
        {
            ["timestamp"] = logEvent.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["level"] = logEvent.Level.ToString(),
            ["event"] = logEvent.Name,
        };
        foreach (var (key, value) in logEvent.Fields)
        {
            record[key] = value;
        }

        writer.WriteLine(JsonSerializer.Serialize(record, Options));
    }
}
