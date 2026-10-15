using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>Acceptance criterion 4: failed or interrupted imports never surface as snapshots.</summary>
public sealed class InterruptedImportTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private void AssertNoSnapshotsAndCleanStaging()
    {
        Assert.Empty(_env.Store.List(TestEnvironment.Workspace("acme")).Value.Snapshots);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_env.StagingDirectory("acme")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_env.SnapshotsDirectory("acme")));
    }

    [Theory]
    [InlineData(ImportStage.MemberStaged)]
    [InlineData(ImportStage.MetadataWritten)]
    [InlineData(ImportStage.MarkerWritten)]
    public void StorageFailureAtAnyStageCleansUpAndLaterImportSucceeds(ImportStage failAt)
    {
        _env.Store.FaultInjector = stage =>
        {
            if (stage == failAt)
            {
                throw new IOException("simulated disk failure");
            }
        };

        var failed = _env.Import("acme", TestEnvironment.FixturePath("valid-export"));
        Assert.Equal(DiagnosticCode.PublishFailed, Assert.Single(failed.Diagnostics).Code);
        AssertNoSnapshotsAndCleanStaging();

        _env.Store.FaultInjector = null;
        var retried = _env.Import("acme", TestEnvironment.FixturePath("valid-export"));
        Assert.True(retried.Succeeded);
        Assert.Equal(retried.Value.Snapshot.SnapshotId, Assert.Single(_env.Store.List(TestEnvironment.Workspace("acme")).Value.Snapshots).SnapshotId);
    }

    [Theory]
    [InlineData(ImportStage.MemberStaged)]
    [InlineData(ImportStage.MarkerWritten)]
    public void CancellationCleansUpAndPublishesNothing(ImportStage cancelAt)
    {
        using var cancellation = new CancellationTokenSource();
        _env.Store.FaultInjector = stage =>
        {
            if (stage == cancelAt)
            {
                cancellation.Cancel();
            }
        };

        var result = _env.Import("acme", TestEnvironment.FixturePath("valid-export"), cancellationToken: cancellation.Token);
        Assert.Equal(DiagnosticCode.ImportCancelled, Assert.Single(result.Diagnostics).Code);
        AssertNoSnapshotsAndCleanStaging();
    }

    [Fact]
    public void AbandonedStagingAndUnmarkedSnapshotDirectoriesAreNeverListed()
    {
        // Simulate a process killed mid-import: staging data (even with a marker) left behind, and a
        // snapshot-shaped directory with no completion marker.
        var first = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var published = Path.Combine(_env.SnapshotsDirectory("acme"), first.SnapshotId.Value);

        var abandoned = Path.Combine(_env.StagingDirectory("acme"), SnapshotId.New().Value + ".run-abandoned");
        CopyDirectory(published, abandoned);

        var unmarked = Path.Combine(_env.SnapshotsDirectory("acme"), SnapshotId.New().Value);
        CopyDirectory(published, unmarked);
        File.SetAttributes(Path.Combine(unmarked, "complete.json"), FileAttributes.Normal);
        File.Delete(Path.Combine(unmarked, "complete.json"));

        Directory.CreateDirectory(Path.Combine(_env.SnapshotsDirectory("acme"), "not-a-snapshot-id"));

        var listing = _env.Store.List(TestEnvironment.Workspace("acme")).Value;
        Assert.Equal(first.SnapshotId, Assert.Single(listing.Snapshots).SnapshotId);
        Assert.Equal(2, listing.IgnoredEntries);

        var later = _env.Import("acme", TestEnvironment.FixturePath("valid-export"));
        Assert.True(later.Succeeded);
        var after = _env.Store.List(TestEnvironment.Workspace("acme")).Value;
        Assert.Equal([first.SnapshotId, later.Value.Snapshot.SnapshotId], after.Snapshots.Select(s => s.SnapshotId));
        Assert.True(Directory.Exists(abandoned)); // F0 performs no automatic deletion of foreign data
    }

    [Fact]
    public void ValidationFailureAfterStagingBeganLeavesNothingBehind()
    {
        var source = _env.CreateExport(("QSRC/A.sql", "A", "SQL", "ok"u8.ToArray()), ("QSRC/B.sql", "B", "SQL", [0xFF]));
        var result = _env.Import("acme", source);
        Assert.Equal(DiagnosticCode.InvalidUtf8, Assert.Single(result.Diagnostics).Code);
        AssertNoSnapshotsAndCleanStaging();
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
