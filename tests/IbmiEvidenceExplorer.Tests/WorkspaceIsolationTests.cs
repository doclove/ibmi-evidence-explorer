using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>Acceptance criterion 3: workspace scoping, invalid IDs, and inaccessible storage.</summary>
public sealed class WorkspaceIsolationTests : IDisposable
{
    private static readonly MemberIdentity Shared = new("TESTLIB", "QSRC", "ORDERS", SourceType.SQL);

    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private (SnapshotSummary A, SnapshotSummary B) ImportSimilarWorkspaces()
    {
        var a = _env.CreateExport(("QSRC/ORDERS.sql", "ORDERS", "SQL", "-- workspace A\nSELECT 1"u8.ToArray()));
        var b = _env.CreateExport(("QSRC/ORDERS.sql", "ORDERS", "SQL", "-- workspace B\nSELECT 2"u8.ToArray()));
        return (_env.Import("acme", a).Value.Snapshot, _env.Import("acme-2", b).Value.Snapshot);
    }

    [Fact]
    public void ListReturnsOnlyTheRequestedWorkspace()
    {
        var (a, b) = ImportSimilarWorkspaces();

        var listA = _env.Store.List(TestEnvironment.Workspace("acme")).Value;
        var listB = _env.Store.List(TestEnvironment.Workspace("acme-2")).Value;

        Assert.Equal(a.SnapshotId, Assert.Single(listA.Snapshots).SnapshotId);
        Assert.Equal(b.SnapshotId, Assert.Single(listB.Snapshots).SnapshotId);
        Assert.Equal(Shared, Assert.Single(listA.Snapshots[0].Members).Identity);
        Assert.Equal(Shared, Assert.Single(listB.Snapshots[0].Members).Identity);
        Assert.NotEqual(listA.Snapshots[0].Members[0].Sha256, listB.Snapshots[0].Members[0].Sha256);
        Assert.Empty(_env.Store.List(TestEnvironment.Workspace("acme-3")).Value.Snapshots);
    }

    [Fact]
    public void InspectAndResolveCannotReachAnotherWorkspace()
    {
        var (a, b) = ImportSimilarWorkspaces();
        var wsA = TestEnvironment.Workspace("acme");
        var wsB = TestEnvironment.Workspace("acme-2");

        Assert.Equal("-- workspace A", _env.Store.Inspect(wsA, a.SnapshotId, Shared).Value.Lines[0].Text);
        Assert.Equal("-- workspace B", _env.Store.Inspect(wsB, b.SnapshotId, Shared).Value.Lines[0].Text);

        Assert.Equal(DiagnosticCode.SnapshotNotFound, Assert.Single(_env.Store.Inspect(wsB, a.SnapshotId, Shared).Diagnostics).Code);
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Assert.Single(_env.Store.Inspect(wsA, b.SnapshotId, Shared).Diagnostics).Code);

        var referenceA = _env.Store.Inspect(wsA, a.SnapshotId, Shared).Value.Lines[0].Reference;
        Assert.Equal(DiagnosticCode.SnapshotNotFound, Assert.Single(_env.Store.Resolve(wsB, referenceA).Diagnostics).Code);
    }

    [Fact]
    public void SnapshotCopiedIntoAnotherWorkspaceIsNotAccepted()
    {
        var (a, _) = ImportSimilarWorkspaces();
        var copied = Path.Combine(_env.SnapshotsDirectory("acme-2"), a.SnapshotId.Value);
        CopyDirectory(Path.Combine(_env.SnapshotsDirectory("acme"), a.SnapshotId.Value), copied);

        var listing = _env.Store.List(TestEnvironment.Workspace("acme-2")).Value;
        Assert.Single(listing.Snapshots);
        Assert.Equal(1, listing.IgnoredEntries);
        Assert.Equal(DiagnosticCode.IntegrityFailure,
            Assert.Single(_env.Store.Inspect(TestEnvironment.Workspace("acme-2"), a.SnapshotId, Shared).Diagnostics).Code);
    }

    [Fact]
    public void SymlinkedWorkspaceDirectoryIsRefused()
    {
        ImportSimilarWorkspaces();
        Links.DirectoryLink(_env.WorkspaceDirectory("evil"), _env.WorkspaceDirectory("acme"));

        var result = _env.Store.List(TestEnvironment.Workspace("evil"));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(result.Diagnostics).Code);

        var import = _env.Import("evil", TestEnvironment.FixturePath("valid-export"));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(import.Diagnostics).Code);
        Assert.Single(_env.Store.List(TestEnvironment.Workspace("acme")).Value.Snapshots);
    }

    [Fact]
    public void MissingStorageRootFailsSafelyOnRead()
    {
        var store = new FileSystemSnapshotStore(Path.Combine(_env.Root, "nowhere"));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(store.List(TestEnvironment.Workspace("acme")).Diagnostics).Code);
        Assert.Equal(DiagnosticCode.StorageUnavailable,
            Assert.Single(store.Inspect(TestEnvironment.Workspace("acme"), SnapshotId.New(), Shared).Diagnostics).Code);
        Assert.False(Directory.Exists(Path.Combine(_env.Root, "nowhere")));
    }

    [Fact]
    public void StorageRootThatIsAFileFailsSafely()
    {
        var file = Path.Combine(_env.Root, "not-a-directory");
        File.WriteAllText(file, "x");
        var store = new FileSystemSnapshotStore(file);

        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(store.List(TestEnvironment.Workspace("acme")).Diagnostics).Code);
        var import = store.Import(new ImportRequest(TestEnvironment.Workspace("acme"), TestEnvironment.FixturePath("valid-export"), ImportLimits.Default));
        Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(import.Diagnostics).Code);
    }

    [SkippableFact]
    public void UnreadableStorageFailsSafely()
    {
        ImportSimilarWorkspaces();
        using (Access.DenyRead(_env.StorageRoot))
        {
            Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(_env.Store.List(TestEnvironment.Workspace("acme")).Diagnostics).Code);
            var import = _env.Import("acme", TestEnvironment.FixturePath("valid-export"));
            Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(import.Diagnostics).Code);
        }

        using (Access.DenyRead(_env.SnapshotsDirectory("acme")))
        {
            Assert.Equal(DiagnosticCode.StorageUnavailable, Assert.Single(_env.Store.List(TestEnvironment.Workspace("acme")).Diagnostics).Code);
            Assert.Single(_env.Store.List(TestEnvironment.Workspace("acme-2")).Value.Snapshots);
        }
    }

    [Fact]
    public void StoreRequiresAnExplicitWorkspace()
    {
        Assert.Throws<ArgumentException>(() => _env.Store.List(default));
        Assert.Throws<ArgumentException>(() => _env.Store.Inspect(default, SnapshotId.New(), Shared));
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
