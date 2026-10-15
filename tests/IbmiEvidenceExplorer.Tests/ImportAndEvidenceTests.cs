using System.Security.Cryptography;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>Acceptance criterion 1: identity, hashes, and line evidence for the synthetic export.</summary>
public sealed class ImportAndEvidenceTests : IDisposable
{
    private static readonly (MemberIdentity Identity, string Path)[] Expected =
    [
        (new("DEMOLIB", "QRPGLESRC", "ORDINQ", SourceType.RPG), "QRPGLESRC/ORDINQ.rpgle"),
        (new("DEMOLIB", "QCLSRC", "ORDBATCH", SourceType.CL), "QCLSRC/ORDBATCH.clle"),
        (new("DEMOLIB", "QDDSSRC", "ORDHDR", SourceType.DDS), "QDDSSRC/ORDHDR.pf"),
        (new("DEMOLIB", "QSQLSRC", "ORDVIEW", SourceType.SQL), "QSQLSRC/ORDVIEW.sql"),
    ];

    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void ImportsSyntheticExportAsOneCompleteSnapshot()
    {
        var source = TestEnvironment.FixturePath("valid-export");
        var result = _env.Import("acme", source);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var listing = _env.Store.List(TestEnvironment.Workspace("acme"));
        Assert.True(listing.Succeeded);
        var snapshot = Assert.Single(listing.Value.Snapshots);
        Assert.Equal(0, listing.Value.IgnoredEntries);
        Assert.Equal("complete", snapshot.Status);
        Assert.Equal(result.Value.Snapshot.SnapshotId, snapshot.SnapshotId);
        Assert.Equal(FormatVersions.ImportFormat, snapshot.ImportFormatVersion);
        Assert.Equal("none", snapshot.ParserVersion);
        Assert.StartsWith("run-", snapshot.AnalysisRunId, StringComparison.Ordinal);
        Assert.Equal(ImportLimits.Default, snapshot.AppliedLimits);

        Assert.Equal(Expected.Select(e => e.Identity), snapshot.Members.Select(m => m.Identity));
        foreach (var (identity, path) in Expected)
        {
            var original = File.ReadAllBytes(Path.Combine(source, path));
            var member = snapshot.Members.Single(m => m.Identity == identity);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), member.Sha256);
            Assert.Equal(original.Length, member.ByteLength);
            Assert.Equal(1208, member.Ccsid);
            Assert.Equal(path, member.SourcePath);
        }

        Assert.Equal(Expected.Sum(e => new FileInfo(Path.Combine(source, e.Path)).Length), snapshot.TotalBytes);
    }

    [Fact]
    public void StoresOriginalBytesAndEveryLineResolvesToThem()
    {
        var source = TestEnvironment.FixturePath("valid-export");
        var snapshot = _env.Import("acme", source).Value.Snapshot;
        var workspace = TestEnvironment.Workspace("acme");

        foreach (var (identity, path) in Expected)
        {
            var original = File.ReadAllBytes(Path.Combine(source, path));
            var inspection = _env.Store.Inspect(workspace, snapshot.SnapshotId, identity);
            Assert.True(inspection.Succeeded, string.Join("\n", inspection.Diagnostics));
            Assert.Equal(inspection.Value.Member.LineCount, inspection.Value.Lines.Count);
            Assert.True(inspection.Value.Lines.Count > 0);

            foreach (var line in inspection.Value.Lines)
            {
                var reference = line.Reference;
                Assert.Equal(snapshot.SnapshotId, reference.SnapshotId);
                Assert.Equal(identity, reference.Member);
                Assert.Equal(inspection.Value.Member.Sha256, reference.MemberSha256);
                Assert.Equal(snapshot.AnalysisRunId, reference.AnalysisRunId);
                Assert.Equal(FormatVersions.ImportFormat, reference.ImportFormatVersion);
                Assert.Equal("none", reference.ParserVersion);

                var exact = original.AsSpan((int)reference.ByteOffset, reference.ByteLength).ToArray();
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(exact)), reference.LineSha256);

                var resolved = _env.Store.Resolve(workspace, reference);
                Assert.True(resolved.Succeeded, string.Join("\n", resolved.Diagnostics));
                Assert.Equal(exact, resolved.Value.Bytes);
                Assert.Equal(line.Text, resolved.Value.Text);
            }

            // Lines plus their terminators reproduce the original file.
            var lines = inspection.Value.Lines;
            var rebuilt = lines.SelectMany((l, i) =>
            {
                var end = i + 1 < lines.Count ? lines[i + 1].Reference.ByteOffset : original.Length;
                return original.Skip((int)l.Reference.ByteOffset).Take((int)(end - l.Reference.ByteOffset));
            }).ToArray();
            Assert.Equal(original, rebuilt);
        }
    }

    [Fact]
    public void CrlfMemberExcludesCarriageReturnFromLineText()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var dds = _env.Store.Inspect(TestEnvironment.Workspace("acme"), snapshot.SnapshotId, Expected[2].Identity).Value;
        Assert.Equal(6, dds.Lines.Count);
        Assert.All(dds.Lines, l => Assert.DoesNotContain('\r', l.Text));
        Assert.Equal("     A          K OHORID", dds.Lines[^1].Text);
    }

    [Fact]
    public void InspectsRequestedLineRange()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var result = _env.Store.Inspect(TestEnvironment.Workspace("acme"), snapshot.SnapshotId, Expected[1].Identity, new LineRange(2, 3));
        Assert.Equal([2, 3], result.Value.Lines.Select(l => l.Reference.LineNumber));
        Assert.Equal("PGM", result.Value.Lines[0].Text);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 999)]
    public void RejectsOutOfRangeLines(int first, int last)
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var result = _env.Store.Inspect(TestEnvironment.Workspace("acme"), snapshot.SnapshotId, Expected[1].Identity, new LineRange(first, last));
        Assert.Equal(DiagnosticCode.LineOutOfRange, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void HandlesZeroOneAndManyLineMembers()
    {
        var many = string.Concat(Enumerable.Range(1, 2500).Select(i => $"       LINE {i}\n"));
        var source = _env.CreateExport(
            ("QSRC/EMPTY.sql", "EMPTY", "SQL", []),
            ("QSRC/ONE.sql", "ONE", "SQL", "SELECT 1"u8.ToArray()),
            ("QSRC/MANY.sql", "MANY", "SQL", System.Text.Encoding.UTF8.GetBytes(many)));

        var snapshot = _env.Import("acme", source).Value.Snapshot;
        var workspace = TestEnvironment.Workspace("acme");
        Assert.Equal([0, 1, 2500], snapshot.Members.Select(m => m.LineCount));

        Assert.Empty(_env.Store.Inspect(workspace, snapshot.SnapshotId, snapshot.Members[0].Identity).Value.Lines);
        Assert.Equal("SELECT 1", Assert.Single(_env.Store.Inspect(workspace, snapshot.SnapshotId, snapshot.Members[1].Identity).Value.Lines).Text);
        var last = _env.Store.Inspect(workspace, snapshot.SnapshotId, snapshot.Members[2].Identity, new LineRange(2500, 2500)).Value.Lines;
        Assert.Equal("       LINE 2500", Assert.Single(last).Text);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), snapshot.Members[0].Sha256);
    }

    [Fact]
    public void ReimportCreatesNewSnapshotWithIdenticalHashesAndEvidence()
    {
        var source = TestEnvironment.FixturePath("valid-export");
        var first = _env.Import("acme", source).Value.Snapshot;
        var second = _env.Import("acme", source).Value.Snapshot;
        var workspace = TestEnvironment.Workspace("acme");

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Equal(2, _env.Store.List(workspace).Value.Snapshots.Count);
        Assert.Equal(first.Members, second.Members);

        foreach (var (identity, _) in Expected)
        {
            var a = _env.Store.Inspect(workspace, first.SnapshotId, identity).Value.Lines;
            var b = _env.Store.Inspect(workspace, second.SnapshotId, identity).Value.Lines;
            Assert.Equal(
                a.Select(l => (l.Reference.LineNumber, l.Reference.MemberSha256, l.Reference.LineSha256, l.Reference.ByteOffset, l.Reference.ByteLength, l.Text)),
                b.Select(l => (l.Reference.LineNumber, l.Reference.MemberSha256, l.Reference.LineSha256, l.Reference.ByteOffset, l.Reference.ByteLength, l.Text)));
        }
    }

    [Fact]
    public void PublishedSnapshotIsReadOnlyAndPrivate()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var directory = Path.Combine(_env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value);
        if (OperatingSystem.IsWindows())
        {
            StoragePrivacy.AssertPrivateSnapshot(_env.StorageRoot, "acme", directory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(file));
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_env.StorageRoot));
    }

    [Fact]
    public void TamperedStoredBytesFailIntegrityCheck()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var stored = Path.Combine(_env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value, "members", "000001.src");
        Access.MakeWritable(stored);

        File.WriteAllText(stored, "tampered");
        var result = _env.Store.Inspect(TestEnvironment.Workspace("acme"), snapshot.SnapshotId, Expected[1].Identity);
        Assert.Equal(DiagnosticCode.IntegrityFailure, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void TamperedMetadataHidesSnapshotFromListing()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var metadata = Path.Combine(_env.SnapshotsDirectory("acme"), snapshot.SnapshotId.Value, "snapshot.json");
        Access.MakeWritable(metadata);

        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("ORDINQ", "ORDINX", StringComparison.Ordinal));
        var listing = _env.Store.List(TestEnvironment.Workspace("acme")).Value;
        Assert.Empty(listing.Snapshots);
        Assert.Equal(1, listing.IgnoredEntries);
    }

    [Fact]
    public void MismatchedEvidenceReferencesAreRejected()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var workspace = TestEnvironment.Workspace("acme");
        var reference = _env.Store.Inspect(workspace, snapshot.SnapshotId, Expected[0].Identity, new LineRange(2, 2)).Value.Lines[0].Reference;

        EvidenceReference[] tampered =
        [
            reference with { LineSha256 = new string('0', 64) },
            reference with { MemberSha256 = new string('0', 64) },
            reference with { LineNumber = 3 },
            reference with { ByteOffset = reference.ByteOffset + 1 },
            reference with { AnalysisRunId = "run-other" },
            reference with { ParserVersion = "1.0" },
            reference with { LineNumber = 10_000 },
        ];
        foreach (var bad in tampered)
        {
            Assert.Equal(DiagnosticCode.EvidenceMismatch, Assert.Single(_env.Store.Resolve(workspace, bad).Diagnostics).Code);
        }

        var otherMember = reference with { Member = Expected[1].Identity };
        Assert.Equal(DiagnosticCode.EvidenceMismatch, Assert.Single(_env.Store.Resolve(workspace, otherMember).Diagnostics).Code);
        var missingMember = reference with { Member = new MemberIdentity("DEMOLIB", "QCLSRC", "NOPE", SourceType.CL) };
        Assert.Equal(DiagnosticCode.MemberNotFound, Assert.Single(_env.Store.Resolve(workspace, missingMember).Diagnostics).Code);
    }

    [Fact]
    public void InspectUnknownSnapshotOrMemberFailsWithTypedDiagnostic()
    {
        var snapshot = _env.Import("acme", TestEnvironment.FixturePath("valid-export")).Value.Snapshot;
        var workspace = TestEnvironment.Workspace("acme");

        Assert.Equal(DiagnosticCode.SnapshotNotFound,
            Assert.Single(_env.Store.Inspect(workspace, SnapshotId.New(), Expected[0].Identity).Diagnostics).Code);
        Assert.Equal(DiagnosticCode.MemberNotFound,
            Assert.Single(_env.Store.Inspect(workspace, snapshot.SnapshotId, Expected[0].Identity with { SourceType = SourceType.SQL }).Diagnostics).Code);
    }
}
