using System.Text;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>Acceptance criterion 2: typed diagnostics, nothing published, no source text in logs.</summary>
public sealed class ImportRejectionTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private void AssertRejected(OperationResult<ImportOutcome> result, DiagnosticCode code)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        AssertNothingPublished();
        Canaries.AssertAbsent(string.Join("\n", result.Diagnostics));
        Canaries.AssertAbsent(_env.Log.AllText());
        Assert.Contains(_env.Log.Events, e => e.Name == "import.rejected" && e.Fields["code"] == code.ToString());
    }

    private void AssertNothingPublished()
    {
        var listing = _env.Store.List(TestEnvironment.Workspace("acme"));
        if (listing.Succeeded)
        {
            Assert.Empty(listing.Value.Snapshots);
        }

        var staging = _env.StagingDirectory("acme");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
        var snapshots = _env.SnapshotsDirectory("acme");
        Assert.True(!Directory.Exists(snapshots) || !Directory.EnumerateFileSystemEntries(snapshots).Any());
    }

    private static byte[] Canary(string text) => Encoding.UTF8.GetBytes($"/* FIXTURE-CANARY-SECRET */\n{text}\n");

    [Theory]
    [InlineData("malformed-manifest", DiagnosticCode.ManifestMalformed)]
    [InlineData("duplicate-identity", DiagnosticCode.DuplicateMemberIdentity)]
    [InlineData("unsupported-ccsid", DiagnosticCode.UnsupportedCcsid)]
    [InlineData("bad-path", DiagnosticCode.PathTraversal)]
    public void RejectsCommittedInvalidFixtures(string fixture, DiagnosticCode code) =>
        AssertRejected(_env.Import("acme", TestEnvironment.FixturePath(fixture)), code);

    [Fact]
    public void RejectsInvalidUtf8WithOffsetAndLineButNoContent()
    {
        var bytes = Canary("PGM").Concat(new byte[] { 0x41, 0xFF, 0x42, (byte)'\n' }).ToArray();
        var source = _env.CreateExport(("QSRC/OK.clle", "OK", "CL", Canary("PGM")), ("QSRC/BAD.clle", "BAD", "CL", bytes));

        var result = _env.Import("acme", source);

        AssertRejected(result, DiagnosticCode.InvalidUtf8);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(1, diagnostic.EntryIndex);
        Assert.Equal("QSRC/BAD.clle", diagnostic.Path);
        Assert.Contains($"offset {Canary("PGM").Length + 1}", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsInvalidUtf8InEveryMember()
    {
        var source = _env.CreateExport(("QSRC/A.clle", "A", "CL", [0xFF]), ("QSRC/B.clle", "B", "CL", [0xC3]));
        var result = _env.Import("acme", source);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == DiagnosticCode.InvalidUtf8));
        AssertNothingPublished();
    }

    [Fact]
    public void MemberSizeBoundaryIsInclusive()
    {
        var limits = ImportLimits.Create(maxMemberBytes: 16).Value;
        var exact = _env.CreateExport(("QSRC/A.sql", "A", "SQL", Encoding.UTF8.GetBytes(new string('x', 16))));
        Assert.True(_env.Import("acme", exact, limits).Succeeded);

        var env2 = new TestEnvironment();
        try
        {
            var over = env2.CreateExport(("QSRC/A.sql", "A", "SQL", Encoding.UTF8.GetBytes("FIXTURE-CANARY-" + new string('x', 2))));
            var result = env2.Import("acme", over, limits);
            Assert.Equal(DiagnosticCode.MemberTooLarge, Assert.Single(result.Diagnostics).Code);
            Assert.False(Directory.Exists(env2.WorkspaceDirectory("acme")));
            Canaries.AssertAbsent(env2.Log.AllText());
        }
        finally
        {
            env2.Dispose();
        }
    }

    [Fact]
    public void RejectsOversizedImportTotal()
    {
        var limits = ImportLimits.Create(maxMemberBytes: 100, maxImportBytes: 150).Value;
        var source = _env.CreateExport(
            ("QSRC/A.sql", "A", "SQL", Encoding.UTF8.GetBytes(new string('a', 100))),
            ("QSRC/B.sql", "B", "SQL", Encoding.UTF8.GetBytes(new string('b', 51))));
        AssertRejected(_env.Import("acme", source, limits), DiagnosticCode.ImportTooLarge);
    }

    [Fact]
    public void ImportTotalBoundaryIsInclusive()
    {
        var limits = ImportLimits.Create(maxMemberBytes: 100, maxImportBytes: 150).Value;
        var source = _env.CreateExport(
            ("QSRC/A.sql", "A", "SQL", Encoding.UTF8.GetBytes(new string('a', 100))),
            ("QSRC/B.sql", "B", "SQL", Encoding.UTF8.GetBytes(new string('b', 50))));
        Assert.True(_env.Import("acme", source, limits).Succeeded);
    }

    [Fact]
    public void RejectsTooManyMembers()
    {
        var limits = ImportLimits.Create(maxMembers: 1).Value;
        var source = _env.CreateExport(("QSRC/A.sql", "A", "SQL", "x"u8.ToArray()), ("QSRC/B.sql", "B", "SQL", "y"u8.ToArray()));
        AssertRejected(_env.Import("acme", source, limits), DiagnosticCode.TooManyMembers);
    }

    [Fact]
    public void RejectsOversizedManifest()
    {
        var source = _env.CreateExport(("QSRC/A.sql", "A", "SQL", "x"u8.ToArray()));
        using (var stream = File.OpenWrite(Path.Combine(source, ManifestParser.FileName)))
        {
            stream.SetLength(ImportLimits.MaxManifestBytes + 1);
        }

        AssertRejected(_env.Import("acme", source), DiagnosticCode.ManifestTooLarge);
    }

    [SkippableFact]
    public void RejectsSymlinkedMemberFile()
    {
        var outside = Path.Combine(_env.Root, "outside.clle");
        File.WriteAllBytes(outside, Canary("PGM"));
        var source = _env.CreateExport(("QSRC/A.clle", "A", "CL", Canary("PGM")));
        File.Delete(Path.Combine(source, "QSRC/A.clle"));
        Links.FileSymlink(Path.Combine(source, "QSRC/A.clle"), outside);

        AssertRejected(_env.Import("acme", source), DiagnosticCode.SymlinkRejected);
    }

    [SkippableFact]
    public void RejectsSymlinkedDirectoryInsideSourceRoot()
    {
        var source = _env.CreateExport(("REAL/A.clle", "A", "CL", Canary("PGM")));
        Links.DirectorySymlink(Path.Combine(source, "QSRC"), Path.Combine(source, "REAL"));
        TestEnvironment.WriteManifest(source,
            """{ "formatVersion": 1, "members": [ { "path": "QSRC/A.clle", "library": "TESTLIB", "sourceFile": "QSRC", "member": "A", "sourceType": "CL", "ccsid": 1208 } ] }""");

        AssertRejected(_env.Import("acme", source), DiagnosticCode.SymlinkRejected);
    }

    [SkippableFact]
    public void RejectsSymlinkedManifest()
    {
        var source = _env.CreateExport(("QSRC/A.clle", "A", "CL", Canary("PGM")));
        var real = Path.Combine(_env.Root, "real-manifest.json");
        File.Move(Path.Combine(source, ManifestParser.FileName), real);
        Links.FileSymlink(Path.Combine(source, ManifestParser.FileName), real);

        AssertRejected(_env.Import("acme", source), DiagnosticCode.SymlinkRejected);
    }

    [Fact]
    public void RejectsLinkedDirectoryEveryPlatformCanCreate()
    {
        // A junction on Windows (no privilege needed), a symlink elsewhere.
        var source = _env.CreateExport(("REAL/A.clle", "A", "CL", Canary("PGM")));
        Links.DirectoryLink(Path.Combine(source, "QSRC"), Path.Combine(source, "REAL"));
        TestEnvironment.WriteManifest(source,
            """{ "formatVersion": 1, "members": [ { "path": "QSRC/A.clle", "library": "TESTLIB", "sourceFile": "QSRC", "member": "A", "sourceType": "CL", "ccsid": 1208 } ] }""");

        AssertRejected(_env.Import("acme", source), DiagnosticCode.SymlinkRejected);
    }

    [Fact]
    public void RejectsTraversalPath()
    {
        File.WriteAllBytes(Path.Combine(_env.Root, "outside.clle"), Canary("PGM"));
        var source = _env.CreateExport(("QSRC/A.clle", "A", "CL", Canary("PGM")));
        TestEnvironment.WriteManifest(source,
            """{ "formatVersion": 1, "members": [ { "path": "../../outside.clle", "library": "TESTLIB", "sourceFile": "QSRC", "member": "A", "sourceType": "CL", "ccsid": 1208 } ] }""");

        AssertRejected(_env.Import("acme", source), DiagnosticCode.PathTraversal);
    }

    [Fact]
    public void RejectsMissingReferencedFileAndDirectoryTarget()
    {
        var source = _env.CreateExport(("QSRC/A.clle", "A", "CL", Canary("PGM")));
        TestEnvironment.WriteManifest(source,
            """{ "formatVersion": 1, "members": [ { "path": "QSRC/MISSING.clle", "library": "TESTLIB", "sourceFile": "QSRC", "member": "A", "sourceType": "CL", "ccsid": 1208 }, { "path": "QSRC", "library": "TESTLIB", "sourceFile": "QSRC", "member": "B", "sourceType": "CL", "ccsid": 1208 } ] }""");

        var result = _env.Import("acme", source);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == DiagnosticCode.SourceFileNotFound));
        AssertNothingPublished();
    }

    [Fact]
    public void RejectsMissingManifestAndMissingSourceRoot()
    {
        var empty = Path.Combine(_env.Root, "empty");
        Directory.CreateDirectory(empty);
        AssertRejected(_env.Import("acme", empty), DiagnosticCode.ManifestNotFound);
        AssertRejected(_env.Import("acme", Path.Combine(_env.Root, "does-not-exist")), DiagnosticCode.SourceRootNotFound);
    }

    [Fact]
    public void ValidationFailuresDoNotCreateWorkspaceStorage()
    {
        _env.Import("acme", TestEnvironment.FixturePath("bad-path"));
        Assert.False(Directory.Exists(_env.WorkspaceDirectory("acme")));
    }
}
