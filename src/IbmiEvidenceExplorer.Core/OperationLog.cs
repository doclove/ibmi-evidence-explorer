namespace IbmiEvidenceExplorer.Core;

public enum OperationLogLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// A structured log event. Fields carry identifiers, counts, hashes, codes, and manifest paths only;
/// never source text.
/// </summary>
public sealed record OperationLogEvent(
    DateTimeOffset Timestamp,
    OperationLogLevel Level,
    string Name,
    IReadOnlyDictionary<string, string?> Fields);

public interface IOperationLog
{
    void Write(OperationLogEvent logEvent);
}

public sealed class NullOperationLog : IOperationLog
{
    public static NullOperationLog Instance { get; } = new();

    public void Write(OperationLogEvent logEvent)
    {
    }
}
