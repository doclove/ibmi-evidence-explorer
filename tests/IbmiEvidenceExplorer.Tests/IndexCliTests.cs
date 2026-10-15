using System.Text.Json;
using IbmiEvidenceExplorer.Cli;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1 commands through the real CLI entry point: arguments, output, exit codes, and log privacy.</summary>
public sealed class IndexCliTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private (int Exit, string Out, string Log) Run(params string[] args)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var exit = CliApp.Run(args, output, log, name => name == CliApp.StorageEnvironmentVariable ? _env.StorageRoot : null);
        return (exit, output.ToString(), log.ToString());
    }

    private string Import(string workspace = "acme", string fixture = "valid-export")
    {
        var (exit, output, _) = Run("import", "--workspace", workspace, "--source", TestEnvironment.FixturePath(fixture), "--json");
        Assert.Equal(CliApp.Ok, exit);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("snapshotId").GetString()!;
    }

    [Fact]
    public void IndexSearchAndDependenciesTextOutput()
    {
        var snapshot = Import();
        var (exit, output, _) = Run("index", "--workspace", "acme", "--snapshot", snapshot);
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains($"Indexed snapshot {snapshot} in workspace acme: partial", output, StringComparison.Ordinal);
        Assert.Contains("parser version ibmi-explorer-f1.5-parser/1, index format ibmi-explorer-index/2", output, StringComparison.Ordinal);
        Assert.Contains("members 4: complete 2, partial 2, unsupported 0", output, StringComparison.Ordinal);
        Assert.Contains("edges 5 (resolved 5, unresolved 0)", output, StringComparison.Ordinal);
        Assert.Contains("line 12 unsupported-statement", output, StringComparison.Ordinal);

        (exit, output, _) = Run("search", "--workspace", "acme", "--snapshot", snapshot, "--query", "OHSTAT");
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("1 exact match(es) for 'OHSTAT'.", output, StringComparison.Ordinal);
        Assert.Contains($"evidence ibmi-explorer:{snapshot}/DEMOLIB/QDDSSRC/ORDHDR/DDS#L4", output, StringComparison.Ordinal);

        (exit, output, _) = Run("dependencies", "--workspace", "acme", "--snapshot", snapshot,
            "--library", "DEMOLIB", "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL");
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("1 direct edge(s), 0 unresolved", output, StringComparison.Ordinal);
        Assert.Contains("line 4  calls-program -> DEMOLIB/ORDINQ  resolved to DEMOLIB/QRPGLESRC/ORDINQ (RPG)", output, StringComparison.Ordinal);

        // Source text is shown only by inspect: comments, strings, and statement text never appear.
        Canaries.AssertAbsent(output);
        Assert.DoesNotContain("Order status", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutputUsesSpecificationLabelsAndFullEvidence()
    {
        var snapshot = Import();
        Assert.Equal(CliApp.Ok, Run("index", "--workspace", "acme", "--snapshot", snapshot, "--json").Exit);

        var (exit, output, _) = Run("dependencies", "--workspace", "acme", "--snapshot", snapshot, "--json",
            "--library", "DEMOLIB", "--source-file", "QDDSSRC", "--member", "ORDHDR", "--type", "DDS");
        Assert.Equal(CliApp.Ok, exit);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal("partial", root.GetProperty("index").GetProperty("status").GetString());
        Assert.Equal("complete", root.GetProperty("coverage").GetString());
        var edge = root.GetProperty("edges")[0];
        Assert.Equal("keys-field", edge.GetProperty("kind").GetString());
        Assert.Equal("resolved", edge.GetProperty("resolution").GetString());
        Assert.Equal("DDS", edge.GetProperty("sourceMember").GetProperty("sourceType").GetString());
        var reference = edge.GetProperty("referenceEvidence");
        Assert.Equal(6, reference.GetProperty("lineNumber").GetInt32());
        Assert.Equal("none", reference.GetProperty("parserVersion").GetString());
        Assert.Equal("ibmi-explorer-f1.5-parser/1", edge.GetProperty("provenance").GetProperty("parserVersion").GetString());
    }

    [Fact]
    public void UsageErrorsAndTypedRejections()
    {
        var snapshot = Import();
        Assert.Equal(CliApp.Usage, Run("index", "--workspace", "acme").Exit);
        Assert.Equal(CliApp.Usage, Run("search", "--workspace", "acme", "--snapshot", snapshot).Exit);
        Assert.Equal(CliApp.Usage, Run("dependencies", "--workspace", "acme", "--snapshot", snapshot, "--library", "DEMOLIB").Exit);
        Assert.Equal(CliApp.Usage, Run("search", "--workspace", "acme", "--snapshot", snapshot, "--query", "A", "--lines", "1").Exit);

        var (exit, output, _) = Run("search", "--workspace", "acme", "--snapshot", snapshot, "--query", "ORDHDR");
        Assert.Equal(CliApp.Rejected, exit);
        Assert.Contains("IndexNotFound", output, StringComparison.Ordinal);

        Assert.Contains("InvalidSnapshotId", Run("index", "--workspace", "acme", "--snapshot", "snap-x").Out, StringComparison.Ordinal);
        Assert.Contains("InvalidWorkspaceId", Run("index", "--workspace", "../x", "--snapshot", snapshot).Out, StringComparison.Ordinal);
        Assert.Contains("SnapshotNotFound", Run("index", "--workspace", "other", "--snapshot", snapshot).Out, StringComparison.Ordinal);

        Run("index", "--workspace", "acme", "--snapshot", snapshot);
        Assert.Contains("InvalidQuery", Run("search", "--workspace", "acme", "--snapshot", snapshot, "--query", "a b").Out, StringComparison.Ordinal);
        Assert.Contains("InvalidIdentityName", Run("dependencies", "--workspace", "acme", "--snapshot", snapshot,
            "--library", "demolib", "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL").Out, StringComparison.Ordinal);
        Assert.Contains("MemberNotFound", Run("dependencies", "--workspace", "acme", "--snapshot", snapshot,
            "--library", "DEMOLIB", "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "RPG").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsAreStructuredAndFreeOfSourceText()
    {
        var snapshot = Import("acme", "f1-edge-cases");
        var log = Run("index", "--workspace", "acme", "--snapshot", snapshot).Log
            + Run("search", "--workspace", "acme", "--snapshot", snapshot, "--query", "EFLD1").Log
            + Run("dependencies", "--workspace", "acme", "--snapshot", snapshot,
                "--library", "EDGELIB", "--source-file", "QCLSRC", "--member", "CALLS", "--type", "CL").Log;

        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var _ = JsonDocument.Parse(line);
        }

        Canaries.AssertAbsent(log);
        Assert.DoesNotContain("EFLD1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("NOSUCHPGM", log, StringComparison.Ordinal);
        Assert.Contains("\"event\":\"index.published\"", log, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitsAndHelpDescribeDefaultsAndOptInCeilings()
    {
        var (_, limits, _) = Run("limits");
        Assert.Contains("Index limits (default / opt-in ceiling):", limits, StringComparison.Ordinal);
        Assert.Contains("--max-index-items   facts and edges     100000 / 500000", limits, StringComparison.Ordinal);
        Assert.Contains("--max-index-bytes   total source bytes  104857600 / 536870912", limits, StringComparison.Ordinal);
        Assert.Contains("query results                           1000 (fixed)", limits, StringComparison.Ordinal);
        Assert.Contains("Preparation limits (fixed):", limits, StringComparison.Ordinal);
        var (_, help, _) = Run("help");
        Assert.Contains("ibmi-explorer dependencies --workspace", help, StringComparison.Ordinal);
        Assert.Contains("ibmi-explorer prepare scan  --source", help, StringComparison.Ordinal);
        Assert.Contains("ibmi-explorer gaps    --workspace", help, StringComparison.Ordinal);
    }
}
