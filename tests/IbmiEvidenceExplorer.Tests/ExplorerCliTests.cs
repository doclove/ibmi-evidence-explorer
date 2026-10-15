using System.Globalization;
using System.Text.Json;
using IbmiEvidenceExplorer.Cli;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F2 commands through the real entry point, with a fake loopback Ollama endpoint.</summary>
public sealed class ExplorerCliTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly string _snapshot;

    public ExplorerCliTests()
    {
        var (exit, output, _) = Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath("f2-explorer"), "--json");
        Assert.Equal(CliApp.Ok, exit);
        using (var document = JsonDocument.Parse(output))
        {
            _snapshot = document.RootElement.GetProperty("snapshotId").GetString()!;
        }

        Assert.Equal(CliApp.Ok, Run("index", "--workspace", "acme", "--snapshot", _snapshot).Exit);
    }

    public void Dispose() => _env.Dispose();

    private (int Exit, string Out, string Log) Run(params string[] args)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var exit = CliApp.Run(args, output, log, name => name == CliApp.StorageEnvironmentVariable ? _env.StorageRoot : null);
        return (exit, output.ToString(), log.ToString());
    }

    private string[] Selection(string command, params string[] extra) =>
        [command, "--workspace", "acme", "--snapshot", _snapshot, "--library", "F2LIB", "--source-file", "QRPGLESRC", "--member", "ORDPOST", "--type", "RPG", .. extra];

    [Fact]
    public void ExploreShowsACitedDossierWithEscapedSourceText()
    {
        var (exit, output, log) = Run(Selection("explore"));

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("Explorer dossier for F2LIB/QRPGLESRC/ORDPOST (RPG)", output, StringComparison.Ordinal);
        Assert.Contains("no model was called", output, StringComparison.Ordinal);
        Assert.Contains("uses-file -> ORDHDR  resolved to F2LIB/QDDSSRC/ORDHDR (DDS)", output, StringComparison.Ordinal);
        Assert.Contains($"ibmi-explorer:{_snapshot}/F2LIB/QRPGLESRC/ORDPOST/RPG#L6", output, StringComparison.Ordinal);
        Assert.Contains("\\u001B[31m", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', output);
        Canaries.AssertAbsent(log);
        Assert.DoesNotContain("ORDPOST", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploreJsonIsStableAcrossRuns()
    {
        var first = Run(Selection("explore", "--json"));
        var second = Run(Selection("explore", "--json"));
        Assert.Equal(CliApp.Ok, first.Exit);
        Assert.Equal(first.Out, second.Out);
        using var document = JsonDocument.Parse(first.Out);
        Assert.Equal("ibmi-explorer-dossier/2", document.RootElement.GetProperty("dossierFormat").GetString());
        Assert.Equal("incoming", document.RootElement.GetProperty("incoming")[0].GetProperty("direction").GetString());
    }

    [Fact]
    public void PreviewPrintsThePacketAndMakesNoCall()
    {
        using var server = FakeOllamaServer.Answering(Replies.Faithful);
        var (exit, output, _) = Run(Selection("explain", "--preview-context", "--ollama-port", server.Port.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("no model was called", output, StringComparison.Ordinal);
        Assert.Contains("----- user message -----", output, StringComparison.Ordinal);
        Assert.Contains("<<<IBMI_EXPLORER_CONTEXT_BEGIN>>>", output, StringComparison.Ordinal);
        Assert.Equal(0, server.Connections);

        var json = Run(Selection("explain", "--preview-context", "--json"));
        using var document = JsonDocument.Parse(json.Out);
        Assert.False(document.RootElement.GetProperty("modelCalled").GetBoolean());
        Assert.Equal("ibmi-explorer-explain-packet/1", document.RootElement.GetProperty("packet").GetProperty("packetFormat").GetString());
    }

    [Fact]
    public void ExplainRequiresModelAndAcknowledgementAndValidOptions()
    {
        using var server = FakeOllamaServer.Answering(Replies.Faithful);
        var port = server.Port.ToString(CultureInfo.InvariantCulture);

        Assert.Equal(CliApp.Usage, Run(Selection("explain")).Exit);
        var unacknowledged = Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--ollama-port", port));
        Assert.Equal(CliApp.Rejected, unacknowledged.Exit);
        Assert.Contains("LocalAiNotAcknowledged", unacknowledged.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Rejected, Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service", "--ollama-port", "70000")).Exit);
        Assert.Equal(CliApp.Rejected, Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service", "--ollama-port", port, "--timeout-seconds", "0")).Exit);
        Assert.Equal(CliApp.Rejected, Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service", "--ollama-port", port, "--timeout-seconds", "601")).Exit);
        Assert.Equal(CliApp.Usage, Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service", "--ollama-host", "10.0.0.1")).Exit);
        Assert.Equal(CliApp.Usage, Run(Selection("explore", "--model", ExplorerEnvironment.Model)).Exit);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public void ExplainPrintsDisclosureThenEscapedDraft()
    {
        using var server = FakeOllamaServer.Answering(r => Replies.For(r,
            ("observed", "ORDPOST uses ORDHDR.\u001B[2J", ["F2"]),
            ("unknown", "Order status values are not in the packet.", [])));
        var (exit, output, log) = Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service",
            "--ollama-port", server.Port.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(CliApp.Ok, exit);
        var disclosure = output.IndexOf("Local AI disclosure", StringComparison.Ordinal);
        var draft = output.IndexOf("DRAFT explanation (draft-requires-human-review)", StringComparison.Ordinal);
        Assert.True(disclosure >= 0 && draft > disclosure);
        Assert.Contains("[observed] ORDPOST uses ORDHDR.\\u001B[2J", output, StringComparison.Ordinal);
        Assert.Contains("[unknown] Order status values", output, StringComparison.Ordinal);
        Assert.Contains($"ibmi-explorer:{_snapshot}/F2LIB/QRPGLESRC/ORDPOST/RPG#L6", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', output);
        Assert.DoesNotContain("ORDPOST", log, StringComparison.Ordinal);
        Canaries.AssertAbsent(log);
    }

    [Fact]
    public void ExplainJsonIncludesDisclosureProvenanceAndSelectedEvidence()
    {
        using var server = FakeOllamaServer.Answering(Replies.Faithful);
        var (exit, output, _) = Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service",
            "--ollama-port", server.Port.ToString(CultureInfo.InvariantCulture), "--json"));

        Assert.Equal(CliApp.Ok, exit);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Contains("Local AI disclosure", root.GetProperty("disclosure").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.NotEmpty(root.GetProperty("disclosure").GetProperty("selectedEvidenceIds").EnumerateArray());
        var provenance = root.GetProperty("explanation").GetProperty("provenance");
        Assert.Equal(_snapshot, provenance.GetProperty("snapshotId").GetString());
        Assert.Equal(ExplorerEnvironment.Model, provenance.GetProperty("modelRequested").GetString());
        Assert.Equal("observed", root.GetProperty("explanation").GetProperty("findings")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void ExplainFailureShowsNoDraft()
    {
        using var server = FakeOllamaServer.Answering(r => Replies.For(r, ("observed", "Deletes PAYROLL.", ["F99"])));
        var (exit, output, _) = Run(Selection("explain", "--model", ExplorerEnvironment.Model, "--acknowledge-local-service",
            "--ollama-port", server.Port.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(CliApp.Rejected, exit);
        Assert.Contains("error LocalAiCitationInvalid", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PAYROLL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DRAFT", output, StringComparison.Ordinal);
    }
}
