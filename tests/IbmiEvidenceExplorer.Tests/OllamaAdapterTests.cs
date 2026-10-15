using System.Net;
using System.Text.Json;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Infrastructure.Ollama;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// F2 criterion 4 at the transport boundary: the pinned Ollama request/response contract, loopback
/// only, no proxy or redirect, bounded bytes and time, and typed failures. Uses an in-process fake.
/// </summary>
[Collection(nameof(SerialHttpTests))]
public sealed class OllamaAdapterTests
{
    private static readonly LocalChatRequest Chat = new(ExplorerEnvironment.Model, "SYSTEM-TEXT", "USER-TEXT FIXTURE-CANARY-PROMPT", ExplorerEnvironment.SampleSchema, 123);

    private static DiagnosticCode Code<T>(OperationResult<T> result) => Assert.Single(result.Diagnostics).Code;

    // ---- Pinned contract -----------------------------------------------------------------------

    [Fact]
    public void RequestsMatchThePinnedOllamaContract()
    {
        using var server = FakeOllamaServer.Answering(_ => "{\"ok\":true}", reportedModel: "local-test:1b-reported");
        using var client = ExplorerEnvironment.Client(server);

        var model = client.FindLocalModel(ExplorerEnvironment.Model);
        Assert.True(model.Succeeded, string.Join("; ", model.Diagnostics));
        Assert.Equal((ExplorerEnvironment.Model, FakeOllamaServer.Digest), (model.Value.Name, model.Value.Digest));
        var reply = client.Chat(Chat);
        Assert.True(reply.Succeeded, string.Join("; ", reply.Diagnostics));
        Assert.Equal(("local-test:1b-reported", "{\"ok\":true}"), (reply.Value.ReportedModel, reply.Value.Content));

        var requests = server.Requests.ToList();
        Assert.Equal([("GET", "/api/tags"), ("POST", "/api/chat")], requests.Select(r => (r.Method, r.Target)));
        Assert.All(requests, r =>
        {
            Assert.Equal($"127.0.0.1:{server.Port}", r.Headers["Host"]);
            Assert.False(r.Headers.ContainsKey("Authorization"));
            Assert.False(r.Headers.ContainsKey("Proxy-Authorization"));
            Assert.False(r.Headers.ContainsKey("Cookie"));
        });

        var chat = requests[1];
        Assert.StartsWith("application/json", chat.Headers["Content-Type"], StringComparison.Ordinal);
        using var body = JsonDocument.Parse(chat.Body);
        var root = body.RootElement;
        Assert.Equal(["model", "messages", "stream", "format", "options"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(ExplorerEnvironment.Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(JsonDocument.Parse(ExplorerEnvironment.SampleSchema).RootElement.GetRawText(), root.GetProperty("format").GetRawText());
        Assert.Equal(["system", "user"], root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(("SYSTEM-TEXT", "USER-TEXT FIXTURE-CANARY-PROMPT"), FakeOllamaServer.Messages(chat));
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal(123, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal(OllamaChatModel.ChatBody(Chat), chat.Body);
    }

    [Fact]
    public void TransportIsConfiguredWithoutProxyRedirectCookiesOrDecompression()
    {
        using var client = new OllamaChatModel(OllamaEndpoint.Loopback().Value);
        var handler = client.Handler;
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(("ollama", "http://127.0.0.1:11434"), (client.Runtime, client.Endpoint));
    }

    [Fact]
    public void AConfiguredDefaultProxyIsNeverUsed()
    {
        using var proxy = new FakeOllamaServer(_ => new FakeResponse(502, "{}"));
        using var server = FakeOllamaServer.Answering(_ => "{}");
        var original = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{proxy.Port}") { BypassProxyOnLocal = false };
            using var client = ExplorerEnvironment.Client(server);
            Assert.True(client.FindLocalModel(ExplorerEnvironment.Model).Succeeded);
            Assert.True(client.Chat(Chat).Succeeded);
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }

        Assert.Equal(0, proxy.Connections);
        Assert.Equal(2, server.Requests.Count);
    }

    // ---- Endpoint boundary ---------------------------------------------------------------------

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.2")]
    [InlineData("::")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("8.8.8.8")]
    public void NonLoopbackEndpointsAreRefusedBeforeAnyConnection(string address)
    {
        var endpoint = OllamaEndpoint.Create(IPAddress.Parse(address), 11434);
        Assert.Equal(DiagnosticCode.LocalAiEndpointRejected, Code(endpoint));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void PortMustBeValid(int port) => Assert.Equal(DiagnosticCode.LocalAiEndpointRejected, Code(OllamaEndpoint.Loopback(port)));

    [Fact]
    public void IPv6LoopbackIsAccepted()
    {
        var endpoint = OllamaEndpoint.Create(IPAddress.IPv6Loopback, 11434);
        Assert.True(endpoint.Succeeded);
        Assert.Equal("http://[::1]:11434", endpoint.Value.ToString());
    }

    [Fact]
    public void UnreachableServiceIsTyped()
    {
        int port;
        using (var reserved = new FakeOllamaServer())
        {
            port = reserved.Port;
        }

        using var client = new OllamaChatModel(OllamaEndpoint.Loopback(port).Value);
        Assert.Equal(DiagnosticCode.LocalAiUnavailable, Code(client.FindLocalModel(ExplorerEnvironment.Model)));
        Assert.Equal(DiagnosticCode.LocalAiUnavailable, Code(client.Chat(Chat)));
    }

    // ---- Model availability --------------------------------------------------------------------

    [Theory]
    [InlineData("local-test:1b", "local-test:1b", true)]
    [InlineData("plain", "plain:latest", true)]
    [InlineData("plain:7b", "plain:latest", false)]
    [InlineData("missing:1b", "local-test:1b", false)]
    [InlineData("LOCAL-TEST:1B", "local-test:1b", false)]
    public void ModelMustBeListedExactly(string requested, string listed, bool found)
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, FakeOllamaServer.Tags(listed)));
        using var client = ExplorerEnvironment.Client(server);
        var result = client.FindLocalModel(requested);
        Assert.Equal(found, result.Succeeded);
        if (!found)
        {
            Assert.Equal(DiagnosticCode.LocalAiModelNotFound, Code(result));
        }
    }

    [Theory]
    [InlineData(",\"remote_host\":\"https://ollama.com:443\",\"remote_model\":\"big\"", "local-test:1b")]
    [InlineData(",\"remote_model\":\"big\"", "local-test:1b")]
    [InlineData("", "gpt-oss:120b-cloud")]
    [InlineData("", "gemma4:cloud")]
    public void RemoteOrCloudModelsAreNotLocal(string extra, string model)
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, FakeOllamaServer.Tags(model, extra)));
        using var client = ExplorerEnvironment.Client(server);
        Assert.Equal(DiagnosticCode.LocalAiModelNotLocal, Code(client.FindLocalModel(model)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"models\":{}}")]
    [InlineData("[]")]
    public void MalformedModelListIsTyped(string body)
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, body));
        using var client = ExplorerEnvironment.Client(server);
        Assert.Equal(DiagnosticCode.LocalAiInvalidResponse, Code(client.FindLocalModel(ExplorerEnvironment.Model)));
    }

    [Fact]
    public void UnknownDigestIsReportedAsNull()
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, """{"models":[{"name":"local-test:1b","model":"local-test:1b","digest":"not a digest!"}]}"""));
        using var client = ExplorerEnvironment.Client(server);
        Assert.Null(client.FindLocalModel(ExplorerEnvironment.Model).Value.Digest);
    }

    // ---- Response failures ---------------------------------------------------------------------

    public static TheoryData<string, FakeResponse, DiagnosticCode> ChatFailures => new()
    {
        { "http-500", new FakeResponse(500, """{"error":"FIXTURE-CANARY-ERROR the model failed"}"""), DiagnosticCode.LocalAiHttpError },
        { "http-429", new FakeResponse(429, """{"error":"busy"}"""), DiagnosticCode.LocalAiHttpError },
        { "http-404", new FakeResponse(404, """{"error":"model not found"}"""), DiagnosticCode.LocalAiModelNotFound },
        { "error-200", new FakeResponse(200, """{"error":"FIXTURE-CANARY-ERROR mid-stream"}"""), DiagnosticCode.LocalAiHttpError },
        { "not-json", new FakeResponse(200, "FIXTURE-CANARY-ERROR plain text"), DiagnosticCode.LocalAiInvalidResponse },
        { "streamed", new FakeResponse(200, FakeOllamaServer.ChatEnvelope("{}").Replace("\"done\":true", "\"done\":false", StringComparison.Ordinal)), DiagnosticCode.LocalAiInvalidResponse },
        { "length", new FakeResponse(200, FakeOllamaServer.ChatEnvelope("{}", doneReason: "length")), DiagnosticCode.LocalAiInvalidResponse },
        { "no-content", new FakeResponse(200, """{"model":"m","message":{"role":"assistant"},"done":true}"""), DiagnosticCode.LocalAiInvalidResponse },
        { "wrong-role", new FakeResponse(200, """{"model":"m","message":{"role":"user","content":"{}"},"done":true}"""), DiagnosticCode.LocalAiInvalidResponse },
        { "tool-call", new FakeResponse(200, """{"model":"m","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"shell","arguments":{"cmd":"rm -rf /"}}}]},"done":true,"done_reason":"stop"}"""), DiagnosticCode.LocalAiInvalidResponse },
        { "too-large", new FakeResponse(200, FakeOllamaServer.ChatEnvelope(new string('x', 600 * 1024))), DiagnosticCode.LocalAiResponseTooLarge },
        { "too-large-chunked", new FakeResponse(200, FakeOllamaServer.ChatEnvelope(new string('x', 600 * 1024)), ChunkedBody: true), DiagnosticCode.LocalAiResponseTooLarge },
    };

    [Theory]
    [MemberData(nameof(ChatFailures))]
    public void ChatFailuresAreTypedAndNeverQuoteBodies(string name, FakeResponse response, DiagnosticCode expected)
    {
        Assert.NotNull(name);
        using var server = new FakeOllamaServer(_ => response);
        using var client = ExplorerEnvironment.Client(server);
        var result = client.Chat(Chat);
        Assert.Equal(expected, Code(result));
        Canaries.AssertAbsent(string.Join("\n", result.Diagnostics));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(307)]
    [InlineData(308)]
    public void RedirectsAreRefusedAndNotFollowed(int status)
    {
        using var elsewhere = FakeOllamaServer.Answering(_ => "{}");
        using var server = new FakeOllamaServer(_ => new FakeResponse(status, "{}",
            new Dictionary<string, string> { ["Location"] = $"http://127.0.0.1:{elsewhere.Port}/api/chat" }));
        using var client = ExplorerEnvironment.Client(server);

        Assert.Equal(DiagnosticCode.LocalAiInvalidResponse, Code(client.Chat(Chat)));
        Assert.Equal(DiagnosticCode.LocalAiInvalidResponse, Code(client.FindLocalModel(ExplorerEnvironment.Model)));
        Assert.Equal(0, elsewhere.Connections);
    }

    [Fact]
    public void SlowServiceTimesOut()
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, FakeOllamaServer.ChatEnvelope("{}"), Delay: TimeSpan.FromSeconds(5)));
        using var client = ExplorerEnvironment.Client(server, TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        Assert.Equal(DiagnosticCode.LocalAiTimeout, Code(client.Chat(Chat)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void CallerCancellationIsDistinctFromTimeout()
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, FakeOllamaServer.ChatEnvelope("{}"), Delay: TimeSpan.FromSeconds(5)));
        using var client = ExplorerEnvironment.Client(server);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.Equal(DiagnosticCode.ExplainCancelled, Code(client.Chat(Chat, cancel.Token)));
    }

    [Fact]
    public void OversizedRequestIsRefusedBeforeConnecting()
    {
        using var server = FakeOllamaServer.Answering(_ => "{}");
        using var client = new OllamaChatModel(OllamaEndpoint.Loopback(server.Port).Value, OllamaOptions.Default with { MaxRequestBytes = 1024 });
        var large = Chat with { UserMessage = new string('u', 2048) };
        Assert.Equal(DiagnosticCode.ContextTooLarge, Code(client.Chat(large)));
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public void InvalidUtf8ResponseIsRejected()
    {
        using var server = new FakeOllamaServer(_ => new FakeResponse(200, string.Empty, RawBody: [(byte)'{', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'}']));
        using var client = ExplorerEnvironment.Client(server);
        Assert.Equal(DiagnosticCode.LocalAiInvalidResponse, Code(client.Chat(Chat)));
    }

    [Fact]
    public void TimeoutOutsideBoundsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaChatModel(OllamaEndpoint.Loopback().Value, OllamaOptions.Default with { Timeout = TimeSpan.FromSeconds(601) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaChatModel(OllamaEndpoint.Loopback().Value, OllamaOptions.Default with { Timeout = TimeSpan.Zero }));
    }
}

/// <summary>
/// HTTP tests that change <see cref="HttpClient.DefaultProxy"/> or assert wall-clock deadlines run
/// alone, so other tests cannot affect them through process-wide state or thread-pool load.
/// </summary>
[CollectionDefinition(nameof(SerialHttpTests), DisableParallelization = true)]
public sealed class SerialHttpTests;
