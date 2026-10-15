using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Tests;

public class LimitsAndIdentifierTests
{
    [Fact]
    public void DefaultsMatchApprovedSpecification()
    {
        var limits = ImportLimits.Create();
        Assert.True(limits.Succeeded);
        Assert.Equal(10L * 1024 * 1024, limits.Value.MaxMemberBytes);
        Assert.Equal(100L * 1024 * 1024, limits.Value.MaxImportBytes);
        Assert.Equal(10_000, limits.Value.MaxMembers);
        Assert.Equal(ImportLimits.Default, limits.Value);
    }

    [Fact]
    public void AcceptsOverridesUpToUpperBounds()
    {
        var limits = ImportLimits.Create(ImportLimits.UpperBoundMemberBytes, ImportLimits.UpperBoundImportBytes, ImportLimits.UpperBoundMembers);
        Assert.True(limits.Succeeded);
    }

    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(-1L, null, null)]
    [InlineData(ImportLimits.UpperBoundMemberBytes + 1, null, null)]
    [InlineData(null, 0L, null)]
    [InlineData(null, ImportLimits.UpperBoundImportBytes + 1, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, ImportLimits.UpperBoundMembers + 1)]
    public void RejectsOutOfBoundLimits(long? member, long? import, int? members)
    {
        var limits = ImportLimits.Create(member, import, members);
        Assert.False(limits.Succeeded);
        Assert.All(limits.Diagnostics, d => Assert.Equal(DiagnosticCode.InvalidLimit, d.Code));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("acme-prod")]
    [InlineData("0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0")]
    public void AcceptsValidWorkspaceIds(string id) => Assert.True(WorkspaceId.TryParse(id, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Acme")]
    [InlineData("-acme")]
    [InlineData("..")]
    [InlineData("../acme")]
    [InlineData("acme/other")]
    [InlineData("acme other")]
    [InlineData("acme_prod")]
    [InlineData("acme\u0000")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz01")]
    public void RejectsInvalidWorkspaceIds(string? id) => Assert.False(WorkspaceId.TryParse(id, out _));

    [Fact]
    public void GeneratedSnapshotIdsRoundTripAndAreUnique()
    {
        var a = SnapshotId.New();
        var b = SnapshotId.New();
        Assert.NotEqual(a, b);
        Assert.True(SnapshotId.TryParse(a.Value, out var parsed));
        Assert.Equal(a, parsed);
    }

    [Theory]
    [InlineData("snap-123")]
    [InlineData("../snap-0123456789abcdef0123456789abcdef")]
    [InlineData("SNAP-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("snap-0123456789abcdef0123456789abcdef/..")]
    public void RejectsInvalidSnapshotIds(string id) => Assert.False(SnapshotId.TryParse(id, out _));
}
