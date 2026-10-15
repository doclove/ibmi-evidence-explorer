using System.Text;
using System.Text.Json;
using IbmiEvidenceExplorer.Cli;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>CLI behavior, applied-limit reporting, and logging privacy through the real entry point.</summary>
public sealed class CliTests : IDisposable
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

    private string ImportValid()
    {
        var (exit, output, _) = Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath("valid-export"), "--json");
        Assert.Equal(CliApp.Ok, exit);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("snapshotId").GetString()!;
    }

    [Fact]
    public void HelpAndUsageErrors()
    {
        Assert.Equal(CliApp.Ok, Run("help").Exit);
        Assert.Equal(CliApp.Usage, Run().Exit);
        Assert.Equal(CliApp.Usage, Run("frobnicate").Exit);
        Assert.Equal(CliApp.Usage, Run("list").Exit);
        Assert.Equal(CliApp.Usage, Run("list", "--workspace").Exit);
        Assert.Equal(CliApp.Usage, Run("list", "--workspace", "a", "--workspace", "b").Exit);
        Assert.Equal(CliApp.Usage, Run("list", "--workspace", "a", "--bogus", "1").Exit);
        Assert.Equal(CliApp.Usage, Run("import", "--workspace", "acme").Exit);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("import")]
    [InlineData("inspect")]
    public void InvalidWorkspaceIdIsRejected(string command)
    {
        var (exit, output, _) = Run(command, "--workspace", "../Other");
        Assert.Equal(CliApp.Rejected, exit);
        Assert.Contains("InvalidWorkspaceId", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportReportsCompleteSnapshotAndAppliedLimits()
    {
        var (exit, output, log) = Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath("valid-export"),
            "--max-member-bytes", "4096", "--max-members", "10");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains(": complete", output, StringComparison.Ordinal);
        Assert.Contains("applied limits: max member bytes 4096, max import bytes 104857600, max members 10", output, StringComparison.Ordinal);
        Assert.Contains("DEMOLIB/QRPGLESRC/ORDINQ (RPG)", output, StringComparison.Ordinal);
        Assert.Contains("\"event\":\"import.published\"", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--max-member-bytes", "0")]
    [InlineData("--max-member-bytes", "67108865")]
    [InlineData("--max-import-bytes", "-5")]
    [InlineData("--max-members", "abc")]
    [InlineData("--max-members", "99999999999")]
    public void OutOfBoundLimitsAreRejected(string option, string value)
    {
        var (exit, output, _) = Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath("valid-export"), option, value);
        Assert.Equal(CliApp.Rejected, exit);
        Assert.Contains("InvalidLimit", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_env.StorageRoot));
    }

    [Fact]
    public void LimitsCommandListsDefaultsAndBounds()
    {
        var (exit, output, _) = Run("limits");
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("10485760 / 67108864", output, StringComparison.Ordinal);
        Assert.Contains("104857600 / 1073741824", output, StringComparison.Ordinal);
        Assert.Contains("10000 / 100000", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListAndInspectThroughCli()
    {
        var snapshotId = ImportValid();

        var list = Run("list", "--workspace", "acme");
        Assert.Equal(CliApp.Ok, list.Exit);
        Assert.Contains("1 complete snapshot(s)", list.Out, StringComparison.Ordinal);
        Assert.Contains($"Snapshot {snapshotId}: complete", list.Out, StringComparison.Ordinal);

        var inspect = Run("inspect", "--workspace", "acme", "--snapshot", snapshotId, "--library", "DEMOLIB",
            "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL", "--lines", "2-3");
        Assert.Equal(CliApp.Ok, inspect.Exit);
        Assert.Contains("parser version none", inspect.Out, StringComparison.Ordinal);
        Assert.Matches(@"\n\s+2  [0-9a-f]{12}  PGM\r?\n", inspect.Out);
        Assert.Contains("DCL VAR(&ORDID)", inspect.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("CALL PGM", inspect.Out, StringComparison.Ordinal);

        var json = Run("inspect", "--workspace", "acme", "--snapshot", snapshotId, "--library", "DEMOLIB",
            "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL", "--json");
        using var document = JsonDocument.Parse(json.Out);
        var line = document.RootElement.GetProperty("lines")[0].GetProperty("reference");
        Assert.Equal(snapshotId, line.GetProperty("snapshotId").GetString());
        Assert.Equal("none", line.GetProperty("parserVersion").GetString());
        Assert.Equal("CL", line.GetProperty("member").GetProperty("sourceType").GetString());
    }

    [Fact]
    public void InspectRejectsMalformedArguments()
    {
        var snapshotId = ImportValid();
        Assert.Contains("InvalidSnapshotId", Run("inspect", "--workspace", "acme", "--snapshot", "../x", "--library", "DEMOLIB",
            "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL").Out, StringComparison.Ordinal);
        Assert.Contains("InvalidIdentityName", Run("inspect", "--workspace", "acme", "--snapshot", snapshotId, "--library", "demolib",
            "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL").Out, StringComparison.Ordinal);
        Assert.Contains("InvalidArgument", Run("inspect", "--workspace", "acme", "--snapshot", snapshotId, "--library", "DEMOLIB",
            "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL", "--lines", "3-1").Out, StringComparison.Ordinal);
        Assert.Contains("SnapshotNotFound", Run("inspect", "--workspace", "other", "--snapshot", snapshotId, "--library", "DEMOLIB",
            "--source-file", "QCLSRC", "--member", "ORDBATCH", "--type", "CL").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsNeverContainSourceTextOnSuccessOrFailure()
    {
        var snapshotId = ImportValid();
        var logs = new StringBuilder();

        logs.Append(Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath("valid-export")).Log);
        logs.Append(Run("list", "--workspace", "acme").Log);
        var inspect = Run("inspect", "--workspace", "acme", "--snapshot", snapshotId, "--library", "DEMOLIB",
            "--source-file", "QRPGLESRC", "--member", "ORDINQ", "--type", "RPG");
        Assert.Contains("FIXTURE-CANARY-RPG", inspect.Out, StringComparison.Ordinal); // shown to the operator on stdout...
        logs.Append(inspect.Log);                                                     // ...but never logged

        var invalid = _env.CreateExport(("QSRC/BAD.sql", "BAD", "SQL", Encoding.UTF8.GetBytes("-- FIXTURE-CANARY-UTF8\n").Append((byte)0xFF).ToArray()));
        var rejected = Run("import", "--workspace", "acme", "--source", invalid);
        Assert.Equal(CliApp.Rejected, rejected.Exit);
        Assert.Contains("InvalidUtf8", rejected.Out, StringComparison.Ordinal);
        Canaries.AssertAbsent(rejected.Out);
        logs.Append(rejected.Log);

        var oversized = Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath("valid-export"), "--max-member-bytes", "10");
        Canaries.AssertAbsent(oversized.Out);
        logs.Append(oversized.Log);

        foreach (var fixture in new[] { "malformed-manifest", "duplicate-identity", "unsupported-ccsid", "bad-path" })
        {
            var result = Run("import", "--workspace", "acme", "--source", TestEnvironment.FixturePath(fixture));
            Assert.Equal(CliApp.Rejected, result.Exit);
            Canaries.AssertAbsent(result.Out);
            logs.Append(result.Log);
        }

        var allLogs = logs.ToString();
        Assert.Contains("import.rejected", allLogs, StringComparison.Ordinal);
        Assert.Contains("member.inspected", allLogs, StringComparison.Ordinal);
        Canaries.AssertAbsent(allLogs);
        foreach (var line in allLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var _ = JsonDocument.Parse(line); // every log line is structured JSON
        }
    }

    [Fact]
    public void TerminalOutputEscapesControlCharacters()
    {
        Assert.Equal(@"a\u001B[31mb\u0007", CliApp.EscapeForTerminal("a\u001b[31mb\u0007"));
        Assert.Equal("tab\tok é", CliApp.EscapeForTerminal("tab\tok é"));
    }
}
