using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;

namespace IbmiEvidenceExplorer.Infrastructure.Ollama;

/// <summary>
/// A loopback IP endpoint for the local Ollama service. Only the IPv4 or IPv6 loopback address is
/// accepted; host names are not, so no DNS lookup or hosts-file entry can redirect the connection.
/// </summary>
public sealed class OllamaEndpoint
{
    public const int DefaultPort = 11434;

    private OllamaEndpoint(IPAddress address, int port)
    {
        Address = address;
        Port = port;
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public Uri BaseUri => new($"http://{(Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString())}:{Port.ToString(CultureInfo.InvariantCulture)}/");

    public override string ToString() => BaseUri.GetLeftPart(UriPartial.Authority);

    /// <summary>127.0.0.1 on the given port (default 11434); the only form the CLI offers.</summary>
    public static OperationResult<OllamaEndpoint> Loopback(int port = DefaultPort) => Create(IPAddress.Loopback, port);

    public static OperationResult<OllamaEndpoint> Create(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.IPv6Loopback))
        {
            return OperationResult<OllamaEndpoint>.Failure(new Diagnostic(DiagnosticCode.LocalAiEndpointRejected,
                "The local model endpoint must be the loopback address 127.0.0.1 or ::1; nothing was sent."));
        }

        if (port is < 1 or > 65535)
        {
            return OperationResult<OllamaEndpoint>.Failure(new Diagnostic(DiagnosticCode.LocalAiEndpointRejected, "The local model port must be 1-65535."));
        }

        return OperationResult<OllamaEndpoint>.Success(new(address, port));
    }
}

/// <summary>Bounds for one adapter instance. Defaults are the documented F2 values.</summary>
public sealed record OllamaOptions(TimeSpan Timeout, int MaxRequestBytes, int MaxResponseBytes, int MaxModelListBytes)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(600);

    public static OllamaOptions Default { get; } = new(DefaultTimeout, 128 * 1024, 512 * 1024, 1024 * 1024);
}

/// <summary>
/// The F2 Ollama adapter (D-016): <c>GET /api/tags</c> to confirm a model is installed and not a
/// remote/cloud model, then one non-streaming <c>POST /api/chat</c> with a JSON-schema
/// <c>format</c>, temperature 0, and no tools. Connections go only to the fixed loopback endpoint:
/// the connect callback ignores host names, proxies are disabled, redirects are refused, request and
/// response bytes are capped, and one timeout bounds each call. Bodies are never logged or quoted
/// in diagnostics. Contract per https://docs.ollama.com/api/chat, /api/tags, /api/errors, and
/// /capabilities/structured-outputs (checked 2026-10-13).
/// </summary>
public sealed class OllamaChatModel : ILocalChatModel, IDisposable
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly OllamaEndpoint _endpoint;
    private readonly OllamaOptions _options;
    private readonly HttpClient _client;

    public OllamaChatModel(OllamaEndpoint endpoint, OllamaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _options = options ?? OllamaOptions.Default;
        if (_options.Timeout <= TimeSpan.Zero || _options.Timeout > OllamaOptions.MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The timeout must be positive and at most 600 seconds.");
        }

        Handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 1,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectLoopbackAsync,
        };
        _client = new HttpClient(Handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, BaseAddress = endpoint.BaseUri };
    }

    public string Runtime => "ollama";

    public string Endpoint => _endpoint.ToString();

    /// <summary>Exposed to tests to assert the transport configuration.</summary>
    internal SocketsHttpHandler Handler { get; }

    public OperationResult<LocalModelInfo> FindLocalModel(string model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        var response = Send(request, _options.MaxModelListBytes, cancellationToken);
        if (!response.Succeeded)
        {
            return OperationResult<LocalModelInfo>.Failure(response.Diagnostics);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Value, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return Failure<LocalModelInfo>(DiagnosticCode.LocalAiInvalidResponse, "The local model list has an unexpected shape.");
            }

            var entries = models.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();

            // Alias precedence: an exact listed name, else an exact model identifier, else the
            // implicit ":latest" tag of an untagged request. More than one entry at the deciding
            // level is ambiguous and refused rather than guessed.
            var matches = entries.Where(e => Text(e, "name") == model).ToList();
            if (matches.Count == 0)
            {
                matches = entries.Where(e => Text(e, "model") == model).ToList();
            }

            if (matches.Count == 0 && !model.Contains(':', StringComparison.Ordinal))
            {
                matches = entries.Where(e => Text(e, "name") == model + ":latest" || Text(e, "model") == model + ":latest").ToList();
            }

            if (matches.Count == 0)
            {
                return Failure<LocalModelInfo>(DiagnosticCode.LocalAiModelNotFound,
                    "The requested model is not installed in the local runtime. IBM i Evidence Explorer never downloads models; install one yourself and retry. Nothing was sent.");
            }

            if (matches.Count > 1)
            {
                return Failure<LocalModelInfo>(DiagnosticCode.LocalAiModelNotFound,
                    "The requested name matches more than one listed model; use the exact listed name. Nothing was sent.");
            }

            var entry = matches[0];
            var name = Text(entry, "name");
            var id = Text(entry, "model");

            // Remote models are proxied by the local server to another host: not local. Cloud markers
            // on any of the requested name, the listed name, or the listed model identifier, or any
            // remote metadata, are refused, even if the other fields look local.
            if (HasText(entry, "remote_model") || HasText(entry, "remote_host") || IsCloudTag(model) || IsCloudTag(name) || IsCloudTag(id))
            {
                return Failure<LocalModelInfo>(DiagnosticCode.LocalAiModelNotLocal,
                    "The requested model is a remote or cloud model served through the local runtime, or its listing says so; it is not local. Nothing was sent.");
            }

            var digest = Text(entry, "digest");
            return OperationResult<LocalModelInfo>.Success(new(name ?? id ?? model, id, digest is { Length: > 0 and <= 128 } && digest.All(char.IsAsciiLetterOrDigit) ? digest : null));
        }
        catch (JsonException)
        {
            return Failure<LocalModelInfo>(DiagnosticCode.LocalAiInvalidResponse, "The local model list is not valid JSON.");
        }
    }

    public OperationResult<LocalChatReply> Chat(LocalChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = ChatBody(request);
        if (body.Length > _options.MaxRequestBytes)
        {
            return Failure<LocalChatReply>(DiagnosticCode.ContextTooLarge,
                $"The chat request would be {body.Length} bytes, above the {_options.MaxRequestBytes}-byte bound; nothing was sent.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = JsonMediaType;
        var response = Send(message, _options.MaxResponseBytes, cancellationToken);
        if (!response.Succeeded)
        {
            return OperationResult<LocalChatReply>.Failure(response.Diagnostics);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Value, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiInvalidResponse, "The chat response is not a JSON object.");
            }

            if (root.TryGetProperty("error", out _))
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiHttpError, "The local model reported an error; its text is not shown.");
            }

            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiInvalidResponse, "The chat response is not a completed, non-streaming reply.");
            }

            if (Text(root, "done_reason") is { } reason && reason != "stop")
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiInvalidResponse,
                    reason == "length" ? "The model reply was cut off by the output limit; no draft is shown." : "The model reply did not finish normally; no draft is shown.");
            }

            if (!root.TryGetProperty("message", out var reply) || reply.ValueKind != JsonValueKind.Object || Text(reply, "role") != "assistant"
                || Text(reply, "content") is not { } content)
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiInvalidResponse, "The chat response has no assistant message content.");
            }

            if (reply.TryGetProperty("tool_calls", out var tools) && !(tools.ValueKind == JsonValueKind.Null || (tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0)))
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiInvalidResponse, "The model attempted a tool call, which IBM i Evidence Explorer never offers or runs.");
            }

            var reported = Text(root, "model");
            if (IsCloudTag(reported))
            {
                return Failure<LocalChatReply>(DiagnosticCode.LocalAiModelNotLocal, "The reply reports a cloud model; it is not local. No draft is shown.");
            }

            // A missing model name cannot be matched to the checked model; orchestration rejects "unknown".
            return OperationResult<LocalChatReply>.Success(new(reported ?? "unknown", content));
        }
        catch (JsonException)
        {
            return Failure<LocalChatReply>(DiagnosticCode.LocalAiInvalidResponse, "The chat response is not valid JSON.");
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The exact request body: no tools, no streaming, schema-constrained output, deterministic sampling.</summary>
    internal static byte[] ChatBody(LocalChatRequest chat)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", chat.Model);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", chat.SystemMessage);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", chat.UserMessage);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("stream", false);
            w.WritePropertyName("format");
            using (var schema = JsonDocument.Parse(chat.OutputSchemaJson))
            {
                schema.RootElement.WriteTo(w);
            }

            w.WriteStartObject("options");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("num_predict", chat.MaxOutputTokens);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// One bounded HTTP exchange. The timeout covers connecting, headers, and the whole body: the
    /// body is read with the deadline token, so a service that stalls mid-body is cut off on time.
    /// Caller cancellation is reported as <see cref="DiagnosticCode.ExplainCancelled"/>, the
    /// deadline as <see cref="DiagnosticCode.LocalAiTimeout"/>.
    /// </summary>
    private OperationResult<byte[]> Send(HttpRequestMessage request, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // Blocking on the asynchronous path is deliberate: synchronous body reads cannot be cancelled.
            return SendAsync(request, maxBytes, linked.Token).GetAwaiter().GetResult();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<byte[]>(DiagnosticCode.ExplainCancelled, "Explanation was cancelled; no draft is shown.");
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            return Failure<byte[]>(DiagnosticCode.LocalAiTimeout, $"The local model service did not answer within {_options.Timeout.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds; no draft is shown.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or OperationCanceledException)
        {
            return Failure<byte[]>(DiagnosticCode.LocalAiUnavailable, $"The local model service at {Endpoint} is not reachable; no draft is shown.");
        }
    }

    private async Task<OperationResult<byte[]>> SendAsync(HttpRequestMessage request, int maxBytes, CancellationToken token)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            return Failure<byte[]>(DiagnosticCode.LocalAiInvalidResponse, $"The local model service answered with a redirect (HTTP {status}); redirects are refused and nothing further was sent.");
        }

        if (status == 404)
        {
            return Failure<byte[]>(DiagnosticCode.LocalAiModelNotFound, "The local model service reported HTTP 404 (model or endpoint not found).");
        }

        if (status is < 200 or >= 300)
        {
            return Failure<byte[]>(DiagnosticCode.LocalAiHttpError, $"The local model service answered HTTP {status}; the error body is not shown.");
        }

        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[Math.Min(maxBytes + 1, 64 * 1024)];
            using var collected = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                collected.Write(buffer, 0, read);
                if (collected.Length > maxBytes)
                {
                    return TooLarge(maxBytes);
                }
            }

            var bytes = collected.ToArray();
            try
            {
                _ = StrictUtf8.GetCharCount(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Failure<byte[]>(DiagnosticCode.LocalAiInvalidResponse, "The local model response is not valid UTF-8.");
            }

            return OperationResult<byte[]>.Success(bytes);
        }
    }

    /// <summary>Connects only to the configured loopback IP, whatever host the request names, and re-checks the peer.</summary>
    private async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(_endpoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(_endpoint.Address, _endpoint.Port), cancellationToken).ConfigureAwait(false);
            if (socket.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                throw new HttpRequestException("Connection is not loopback.");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Conservative: a <c>cloud</c> tag or <c>-cloud</c> suffix denotes an Ollama cloud model even if the list omits remote fields.</summary>
    private static bool IsCloudTag(string? name) =>
        name is not null && (name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase));

    private static bool HasText(JsonElement element, string name) => Text(element, name) is { Length: > 0 };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static OperationResult<byte[]> TooLarge(int maxBytes) =>
        Failure<byte[]>(DiagnosticCode.LocalAiResponseTooLarge, $"The local model response exceeds {maxBytes} bytes; it was discarded.");

    private static OperationResult<T> Failure<T>(DiagnosticCode code, string message) => OperationResult<T>.Failure(new Diagnostic(code, message));
}
