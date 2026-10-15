namespace IbmiEvidenceExplorer.Core.Exploration;

/// <summary>
/// A model the local runtime reports as installed and local: its listed name, the listing's model
/// identifier when it differs (an alias of the same entry), and the digest, or null when not reported.
/// </summary>
public sealed record LocalModelInfo(string Name, string? Model, string? Digest)
{
    /// <summary>Names a reply may report for this entry.</summary>
    public IEnumerable<string> Aliases => Model is null || Model == Name ? [Name] : [Name, Model];
}

/// <summary>Model-name comparison shared by orchestration and adapters.</summary>
public static class ModelNames
{
    /// <summary>Exact match, or an untagged name and the same name with its implicit <c>:latest</c> tag.</summary>
    public static bool Equivalent(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a == b || Latest(a) == Latest(b);
    }

    private static string Latest(string name) => name.Contains(':', StringComparison.Ordinal) ? name : name + ":latest";
}

/// <summary>One chat request: fixed instructions, the delimited packet, and the reply schema.</summary>
public sealed record LocalChatRequest(string Model, string SystemMessage, string UserMessage, string OutputSchemaJson, int MaxOutputTokens);

/// <summary>The reply content and the model name as the runtime reported it.</summary>
public sealed record LocalChatReply(string ReportedModel, string Content);

/// <summary>
/// The one boundary between explanation orchestration and a local model runtime (D-016: Ollama on
/// loopback). Implementations must refuse non-loopback endpoints, proxies, and redirects, bound
/// request/response size and time, and return typed diagnostics that never quote source, prompts,
/// replies, or error bodies. No other provider is supported in F2.
/// </summary>
public interface ILocalChatModel
{
    /// <summary>Runtime label recorded in provenance (for example <c>ollama</c>).</summary>
    string Runtime { get; }

    /// <summary>The loopback endpoint, for disclosure and provenance.</summary>
    string Endpoint { get; }

    /// <summary>Confirms the named model is installed and local. Sends no customer material.</summary>
    OperationResult<LocalModelInfo> FindLocalModel(string model, CancellationToken cancellationToken = default);

    /// <summary>Sends one request. The only call that transmits the packet.</summary>
    OperationResult<LocalChatReply> Chat(LocalChatRequest request, CancellationToken cancellationToken = default);
}
