using System.Diagnostics;
using System.Text;
using IbmiEvidenceExplorer.Cli;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// Review findings: a symlinked source root must be rejected, a path swapped to a link after
/// validation must not be followed, and unreadable source must yield a typed rejection, not a crash.
/// </summary>
public sealed class SourceAccessHardeningTests : IDisposable
{
    private static readonly byte[] Outside = Encoding.UTF8.GetBytes("/* FIXTURE-CANARY-OUTSIDE */\nPGM\n");

    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private void AssertRejected(OperationResult<ImportOutcome> result, DiagnosticCode code)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        var snapshots = _env.SnapshotsDirectory("acme");
        Assert.True(!Directory.Exists(snapshots) || !Directory.EnumerateFileSystemEntries(snapshots).Any());
        var staging = _env.StagingDirectory("acme");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
        Canaries.AssertAbsent(string.Join("\n", result.Diagnostics));
        Canaries.AssertAbsent(_env.Log.AllText());
    }

    private (int Exit, string Out, string Log) RunCli(params string[] args)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var exit = CliApp.Run(args, output, log, name => name == CliApp.StorageEnvironmentVariable ? _env.StorageRoot : null);
        return (exit, output.ToString(), log.ToString());
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("/")]
    public void SymlinkedSourceRootIsRejected(string suffix)
    {
        var real = _env.CopyFixture("valid-export");
        var link = Path.Combine(_env.Root, "linked-export");
        Links.DirectorySymlink(link, real);

        AssertRejected(_env.Import("acme", link + suffix), DiagnosticCode.SymlinkRejected);

        var cli = RunCli("import", "--workspace", "acme", "--source", link + suffix);
        Assert.Equal(CliApp.Rejected, cli.Exit);
        Assert.Contains("SymlinkRejected", cli.Out, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void MemberFileSwappedForSymlinkAfterValidationIsNotFollowed()
    {
        var outside = Path.Combine(_env.Root, "outside.clle");
        File.WriteAllBytes(outside, Outside);
        var source = _env.CopyFixture("valid-export");
        var target = Path.Combine(source, "QCLSRC", "ORDBATCH.clle");
        Exception? setupFailure = null;

        _env.Store.FaultInjector = stage =>
        {
            if (stage == ImportStage.SourcesValidated)
            {
                File.Delete(target);
                try
                {
                    Links.FileSymlink(target, outside);
                }
                catch (Exception ex)
                {
                    setupFailure = ex; // a Skip thrown inside the importer would be swallowed; rethrow below
                    throw new IOException("link creation failed");
                }
            }
        };

        var result = _env.Import("acme", source);
        if (setupFailure is not null)
        {
            throw setupFailure;
        }

        AssertRejected(result, DiagnosticCode.SymlinkRejected);
    }

    [Fact]
    public void DirectorySwappedForLinkAfterValidationIsNotFollowed()
    {
        // Junction on Windows, symlink elsewhere: both must be refused at read time.
        var outsideDirectory = Path.Combine(_env.Root, "outside-dir");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllBytes(Path.Combine(outsideDirectory, "ORDBATCH.clle"), Outside);
        var source = _env.CopyFixture("valid-export");
        var directory = Path.Combine(source, "QCLSRC");

        _env.Store.FaultInjector = stage =>
        {
            if (stage == ImportStage.SourcesValidated)
            {
                Directory.Delete(directory, recursive: true);
                Links.DirectoryLink(directory, outsideDirectory);
            }
        };

        AssertRejected(_env.Import("acme", source), DiagnosticCode.SymlinkRejected);
    }

    [SkippableFact]
    public void UnreadableManifestIsATypedRejectionNotACrash()
    {
        var source = _env.CopyFixture("valid-export");
        using (Access.DenyRead(Path.Combine(source, ManifestParser.FileName)))
        {
            AssertRejected(_env.Import("acme", source), DiagnosticCode.SourceUnreadable);

            var cli = RunCli("import", "--workspace", "acme", "--source", source);
            Assert.Equal(CliApp.Rejected, cli.Exit);
            Assert.Contains("SourceUnreadable", cli.Out, StringComparison.Ordinal);
            Assert.Contains("no snapshot was published", cli.Out, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public void UnreadableMemberFileOrDirectoryIsATypedRejection()
    {
        var source = _env.CopyFixture("valid-export");
        using (Access.DenyRead(Path.Combine(source, "QSQLSRC", "ORDVIEW.sql")))
        using (Access.DenyRead(Path.Combine(source, "QCLSRC")))
        {
            var result = _env.Import("acme", source);
            AssertRejected(result, DiagnosticCode.SourceUnreadable);
            Assert.Equal([1, 3], result.Diagnostics.Where(d => d.Code == DiagnosticCode.SourceUnreadable).Select(d => d.EntryIndex));
        }
    }

    [SkippableFact]
    public void UnreadableSourceRootIsATypedRejection()
    {
        var source = _env.CopyFixture("valid-export");
        using (Access.DenyRead(source))
        {
            AssertRejected(_env.Import("acme", source), DiagnosticCode.SourceUnreadable);
        }
    }

    [SkippableFact]
    public async Task FifoMemberIsRejectedWithoutBlocking()
    {
        Skip.If(OperatingSystem.IsWindows(), "FIFOs (mkfifo) do not exist in the Windows file system; non-disk objects are covered by GetFileType");

        var source = _env.CopyFixture("valid-export");
        var fifo = Path.Combine(source, "QSQLSRC", "ORDVIEW.sql");
        File.Delete(fifo);
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", [fifo]) { UseShellExecute = false })!)
        {
            await mkfifo.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var result = await Task.Run(() => _env.Import("acme", source)).WaitAsync(TimeSpan.FromSeconds(10)); // throws if the open blocks
        AssertRejected(result, DiagnosticCode.SourceFileNotFound);
    }
}
