using System.Security.Cryptography;
using IbmiEvidenceExplorer.Cli;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// Acceptance criterion 6 (Windows): valid import and evidence with private storage; junction and
/// reparse-point rejection; Win32 name normalisation not applied; sharing violations and unsafe
/// storage reported as typed diagnostics. Symlink, swap, and unreadable cases shared with Unix live
/// in <see cref="SourceAccessHardeningTests"/> and <see cref="ImportRejectionTests"/>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSourceAccessTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public WindowsSourceAccessTests() => Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only test");

    public void Dispose() => _env.Dispose();

    private void AssertRejected(OperationResult<ImportOutcome> result, DiagnosticCode code)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        var snapshots = _env.SnapshotsDirectory("acme");
        Assert.True(!Directory.Exists(snapshots) || !Directory.EnumerateFileSystemEntries(snapshots).Any());
        Canaries.AssertAbsent(string.Join("\n", result.Diagnostics));
        Canaries.AssertAbsent(_env.Log.AllText());
    }

    private static string ManifestFor(string path) =>
        $$"""{ "formatVersion": 1, "members": [ { "path": "{{path}}", "library": "DEMOLIB", "sourceFile": "QCLSRC", "member": "ORDBATCH", "sourceType": "CL", "ccsid": 1208 } ] }""";

    [SkippableFact]
    public void ValidFixtureImportsResolvesEvidenceAndStorageIsPrivate()
    {
        var source = TestEnvironment.FixturePath("valid-export");
        var snapshot = _env.Import("acme", source).Value.Snapshot;
        var workspace = TestEnvironment.Workspace("acme");
        Assert.Equal("complete", Assert.Single(_env.Store.List(workspace).Value.Snapshots).Status);

        foreach (var member in snapshot.Members)
        {
            var original = File.ReadAllBytes(Path.Combine(source, member.SourcePath));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), member.Sha256);
            foreach (var line in _env.Store.Inspect(workspace, snapshot.SnapshotId, member.Identity).Value.Lines)
            {
                var resolved = _env.Store.Resolve(workspace, line.Reference);
                Assert.True(resolved.Succeeded);
                Assert.Equal(original.AsSpan((int)line.Reference.ByteOffset, line.Reference.ByteLength).ToArray(), resolved.Value.Bytes);
            }
        }

        StoragePrivacy.AssertPrivateSnapshot(_env.StorageRoot, "acme", Path.Combine(_env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value));
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("\\")]
    public void JunctionRootIsRejected(string suffix)
    {
        var real = _env.CopyFixture("valid-export");
        var link = Path.Combine(_env.Root, "junction-export");
        Links.Junction(link, real);

        AssertRejected(_env.Import("acme", link + suffix), DiagnosticCode.SymlinkRejected);
    }

    [SkippableFact]
    public void JunctionDirectoryInsideRootIsRejected()
    {
        var source = _env.CopyFixture("valid-export");
        var real = Path.Combine(source, "REALCL");
        Directory.Move(Path.Combine(source, "QCLSRC"), real);
        Links.Junction(Path.Combine(source, "QCLSRC"), real);

        var result = _env.Import("acme", source);
        AssertRejected(result, DiagnosticCode.SymlinkRejected);
        Assert.Equal(1, Assert.Single(result.Diagnostics).EntryIndex);
    }

    [SkippableFact]
    public void JunctionRootIsRejectedThroughCli()
    {
        var link = Path.Combine(_env.Root, "junction-cli");
        Links.Junction(link, _env.CopyFixture("valid-export"));
        var output = new StringWriter();
        var exit = CliApp.Run(["import", "--workspace", "acme", "--source", link], output, new StringWriter(),
            name => name == CliApp.StorageEnvironmentVariable ? _env.StorageRoot : null);
        Assert.Equal(CliApp.Rejected, exit);
        Assert.Contains("SymlinkRejected", output.ToString(), StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("QCLSRC/NUL")]
    [InlineData("QCLSRC/CON")]
    [InlineData("QCLSRC/ORDBATCH.clle.")]
    [InlineData("QCLSRC/ORDBATCH.clle ")]
    public void DeviceNamesAndWin32NormalisedNamesDoNotResolve(string path)
    {
        // Relative NT opens do not map DOS devices or strip trailing dots/spaces, so these names
        // cannot reach a device or a different file than the one named.
        var source = _env.CopyFixture("valid-export");
        TestEnvironment.WriteManifest(source, ManifestFor(path));
        var result = _env.Import("acme", source);
        Assert.False(result.Succeeded);
        Assert.True(result.Diagnostics.Single().Code is DiagnosticCode.SourceFileNotFound, result.Diagnostics.Single().ToString());
    }

    [SkippableFact]
    public void MemberHeldOpenForWritingIsReportedUnreadable()
    {
        var source = _env.CopyFixture("valid-export");
        using (new FileStream(Path.Combine(source, "QCLSRC", "ORDBATCH.clle"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            AssertRejected(_env.Import("acme", source), DiagnosticCode.SourceUnreadable);
        }

        Assert.True(_env.Import("acme", source).Succeeded);
    }

    [SkippableFact]
    public void ExistingNonPrivateWorkspaceStorageIsRefused()
    {
        // Created without a protected DACL, so it inherits SYSTEM/Administrators access from the temp directory.
        Directory.CreateDirectory(Path.Combine(_env.StorageRoot, "workspaces", "acme"));

        var import = _env.Import("acme", TestEnvironment.FixturePath("valid-export"));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(import.Diagnostics).Code);
        Assert.Contains("not private", import.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(_env.Store.List(TestEnvironment.Workspace("acme")).Diagnostics).Code);
    }

    [SkippableFact]
    public void NewStorageDirectoriesGetAProtectedCurrentUserOnlyDacl()
    {
        Assert.True(_env.Import("acme", TestEnvironment.FixturePath("valid-export")).Succeeded);
        StoragePrivacy.AssertOnlyCurrentUser(new DirectoryInfo(_env.StorageRoot).GetAccessControl(), _env.StorageRoot);
    }

    [SkippableFact]
    public void ReaderRefusesReparsePointsDirectly()
    {
        var source = _env.CopyFixture("valid-export");
        Links.Junction(Path.Combine(source, "LINKED"), Path.Combine(source, "QCLSRC"));
        Assert.Equal(SourceOpenStatus.Opened, SecureSourceTree.TryOpen(source, out var tree));
        using (tree)
        {
            Assert.Equal(SourceOpenStatus.SymbolicLink, tree!.TryOpenFile("LINKED/ORDBATCH.clle", out _));
            Assert.Equal(SourceOpenStatus.NotRegularFile, tree.TryOpenFile("QCLSRC", out _));
            Assert.Equal(SourceOpenStatus.NotFound, tree.TryOpenFile("QCLSRC/../manifest.json", out _));
            Assert.Equal(SourceOpenStatus.Opened, tree.TryOpenFile("QCLSRC/ORDBATCH.clle", out var handle));
            handle!.Dispose();
        }
    }
}
