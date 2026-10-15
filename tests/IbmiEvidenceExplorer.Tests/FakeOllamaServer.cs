using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>One HTTP request as the fake server received it, byte for byte.</summary>
public sealed record RecordedRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// A canned HTTP response. <see cref="ChunkedBody"/> sends the body without Content-Length;
/// <see cref="StallAfterBytes"/> sends the headers and that many body bytes, then stops sending.
/// </summary>
public sealed record FakeResponse(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null, TimeSpan? Delay = null, bool ChunkedBody = false,
    byte[]? RawBody = null, int? StallAfterBytes = null);

/// <summary>
/// A minimal in-process HTTP/1.1 server on 127.0.0.1 that stands in for Ollama. It records every
/// connection and request and answers from a handler. No real model or network access is involved.
/// </summary>
public sealed class FakeOllamaServer : IDisposable
{
    public const string Digest = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _connections;

    public FakeOllamaServer(Func<RecordedRequest, FakeResponse>? handler = null)
    {
        Handler = handler ?? (_ => new FakeResponse(500, "{}"));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public Func<RecordedRequest, FakeResponse> Handler { get; set; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    public IEnumerable<RecordedRequest> Chats => Requests.Where(r => r.Target == "/api/chat");

    /// <summary>The usual Ollama behavior: the model is listed, and chat answers with <paramref name="content"/>.</summary>
    public static FakeOllamaServer Answering(Func<RecordedRequest, string> content, string model = "local-test:1b", string? reportedModel = null) =>
        new(request => request.Target switch
        {
            "/api/tags" => new FakeResponse(200, Tags(model)),
            "/api/chat" => new FakeResponse(200, ChatEnvelope(content(request), reportedModel ?? model)),
            _ => new FakeResponse(404, """{"error":"not found"}"""),
        });

    public static string Tags(string model, string? extra = null) =>
        $$"""{"models":[{"name":"other:7b","model":"other:7b","digest":"ffff"},{"name":"{{model}}","model":"{{model}}","modified_at":"2026-10-01T00:00:00Z","size":1,"digest":"{{Digest}}","details":{"format":"gguf","family":"test"}{{extra}}}]}""";

    public static string ChatEnvelope(string content, string model = "local-test:1b", string doneReason = "stop") =>
        new JsonObject
        {
            ["model"] = model,
            ["created_at"] = "2026-10-13T00:00:00Z",
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["done"] = true,
            ["done_reason"] = doneReason,
            ["total_duration"] = 1,
            ["eval_count"] = 1,
        }.ToJsonString();

    /// <summary>The user and system messages of a recorded chat request.</summary>
    public static (string System, string User) Messages(RecordedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        var messages = document.RootElement.GetProperty("messages");
        return (messages[0].GetProperty("content").GetString()!, messages[1].GetProperty("content").GetString()!);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (await ReadRequest(stream) is { } request)
                {
                    Requests.Enqueue(request);
                    var response = Handler(request);
                    if (response.Delay is { } delay)
                    {
                        await Task.Delay(delay, _stop.Token);
                    }

                    await Write(stream, response, _stop.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<RecordedRequest?> ReadRequest(NetworkStream stream)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(header))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return null;
            }

            header.Add(one[0]);
            if (header.Count > 64 * 1024)
            {
                return null;
            }
        }

        var lines = Encoding.ASCII.GetString([.. header]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read));
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return new RecordedRequest(requestLine[0], requestLine[1], headers, body);
    }

    private static bool EndsWithBlankLine(List<byte> bytes) =>
        bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n';

    private static async Task Write(NetworkStream stream, FakeResponse response, CancellationToken stop)
    {
        var body = response.RawBody ?? Encoding.UTF8.GetBytes(response.Body);
        var head = new StringBuilder($"HTTP/1.1 {response.Status} Status\r\nContent-Type: application/json; charset=utf-8\r\n");
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append(response.ChunkedBody ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), stop);
        if (response.StallAfterBytes is { } stall)
        {
            var sent = body.AsMemory(0, Math.Min(stall, body.Length));
            if (response.ChunkedBody)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{body.Length:X}\r\n"), stop);
            }

            await stream.WriteAsync(sent, stop);
            await stream.FlushAsync(stop);
            await Task.Delay(Timeout.Infinite, stop); // keep the connection open, send nothing more
        }

        if (!response.ChunkedBody)
        {
            await stream.WriteAsync(body, stop);
            return;
        }

        for (var offset = 0; offset < body.Length; offset += 4096)
        {
            var size = Math.Min(4096, body.Length - offset);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{size:X}\r\n"), stop);
            await stream.WriteAsync(body.AsMemory(offset, size), stop);
            await stream.WriteAsync("\r\n"u8.ToArray(), stop);
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), stop);
    }
}
