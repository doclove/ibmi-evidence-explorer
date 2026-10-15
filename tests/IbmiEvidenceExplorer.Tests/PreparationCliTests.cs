using System.Text.Json;
using IbmiEvidenceExplorer.Cli;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1.5 criterion 7 through the real CLI: prepare, F0 import, F1.5 index, search, and dependencies.</summary>
public sealed class PreparationCliTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly ArchiveFixture _archive;

    public PreparationCliTests() => _archive = new ArchiveFixture(_env);

    public void Dispose() => _env.Dispose();

    private (int Exit, string Out, string Log) Run(params string[] args)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var exit = CliApp.Run(args, output, log, name => name == CliApp.StorageEnvironmentVariable ? _env.StorageRoot : null);
        return (exit, output.ToString(), log.ToString());
    }

    [Fact]
    public void PrepareImportIndexSearchAndDependenciesEndToEnd()
    {
        _archive.Add("PRIVLIB/QCLSRC/PRIVJOB.clle", ArchiveFixture.EbcdicFixed(37, "PGM /* FIXTURE-CANARY-CLI */", "  CALL PGM(PRIVPGM)", "ENDPGM"));
        _archive.Add("PRIVLIB/QCLSRC/PRIVPGM.clle.utf8", ArchiveFixture.Utf8Fixed(true, "PGM", "ENDPGM"));
        var plan = _archive.NewPath("plan") + ".json";

        var (exit, scanOut, scanLog) = Run("prepare", "scan", "--source", _archive.Root, "--layout", "archived-library-tree", "--plan", plan,
            "--record-layout", "fixed-source-data", "--record-width", "80");
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("discovered 2: included 2, skipped 0", scanOut, StringComparison.Ordinal);
        Assert.Contains("2 included entries are unconfirmed", scanOut, StringComparison.Ordinal);

        // Unconfirmed plans convert nothing.
        var output = _archive.NewPath("prepared");
        (exit, var refused, _) = Run("prepare", "build", "--source", _archive.Root, "--plan", plan, "--output", output);
        Assert.Equal(CliApp.Rejected, exit);
        Assert.Contains("NothingToPrepare", refused, StringComparison.Ordinal);
        Assert.Contains("missing-choice 1, unconfirmed 1", refused, StringComparison.Ordinal); // no CCSID was suggested for the non-.utf8 member

        ArchiveFixture.Choose(plan, "PRIVLIB/QCLSRC/PRIVJOB.clle", 37, "fixed-source-data", 80);
        ArchiveFixture.Choose(plan, "PRIVLIB/QCLSRC/PRIVPGM.clle.utf8", 1208, "fixed-source-data", 80);
        (exit, var buildOut, var buildLog) = Run("prepare", "build", "--source", _archive.Root, "--plan", plan, "--output", output);
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains(": complete (preparation run", buildOut, StringComparison.Ordinal);
        Assert.Contains("prepared by type: CL 2", buildOut, StringComparison.Ordinal);

        // Prepare output and logs carry counts and codes only: no identities, paths, or source text.
        foreach (var text in new[] { scanOut, scanLog, refused, buildOut, buildLog })
        {
            Canaries.AssertAbsent(text);
            Assert.DoesNotContain("PRIVLIB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVJOB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("QCLSRC", text, StringComparison.Ordinal);
        }

        (exit, var imported, _) = Run("import", "--workspace", "acme", "--source", output, "--json");
        Assert.Equal(CliApp.Ok, exit);
        var snapshot = JsonDocument.Parse(imported).RootElement.GetProperty("snapshotId").GetString()!;
        (exit, var indexed, var indexLog) = Run("index", "--workspace", "acme", "--snapshot", snapshot);
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains(": complete", indexed, StringComparison.Ordinal);
        Canaries.AssertAbsent(indexLog);

        (exit, var search, _) = Run("search", "--workspace", "acme", "--snapshot", snapshot, "--query", "PRIVPGM");
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("1 exact match(es)", search, StringComparison.Ordinal);
        (exit, var dependencies, _) = Run("dependencies", "--workspace", "acme", "--snapshot", snapshot,
            "--library", "PRIVLIB", "--source-file", "QCLSRC", "--member", "PRIVJOB", "--type", "CL");
        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("line 2  calls-program -> PRIVPGM  resolved to PRIVLIB/QCLSRC/PRIVPGM (CL)", dependencies, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--layout", "flat")]
    [InlineData("--record-layout", "raw-source-record")]
    [InlineData("--ccsid", "abc")]
    public void InvalidScanOptionsAreRejected(string option, string value)
    {
        var args = new List<string> { "prepare", "scan", "--source", _archive.Root, "--layout", "archived-library-tree", "--plan", _archive.NewPath("plan") };
        var index = args.IndexOf(option);
        if (index >= 0)
        {
            args[index + 1] = value;
        }
        else
        {
            args.AddRange([option, value]);
        }

        Assert.Equal(CliApp.Rejected, Run([.. args]).Exit);
        Assert.Equal(CliApp.Usage, Run("prepare", "bogus").Exit);
        Assert.Equal(CliApp.Usage, Run("prepare", "scan", "--source", _archive.Root).Exit);
    }
}
