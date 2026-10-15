using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>Windows assertions that snapshot storage is private to the operator (equivalent of 0700/0400).</summary>
[SupportedOSPlatform("windows")]
public static class StoragePrivacy
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static void AssertPrivateSnapshot(string storageRoot, string workspace, string snapshotDirectory)
    {
        var workspaceDirectory = Path.Combine(storageRoot, "workspaces", workspace);
        foreach (var directory in new[] { Path.Combine(storageRoot, "workspaces"), workspaceDirectory,
            Path.Combine(workspaceDirectory, "snapshots"), Path.Combine(workspaceDirectory, "staging") }
            .Concat(Directory.EnumerateDirectories(snapshotDirectory, "*", SearchOption.AllDirectories).Prepend(snapshotDirectory)))
        {
            AssertOnlyCurrentUser(new DirectoryInfo(directory).GetAccessControl(), directory);
        }

        var files = Directory.EnumerateFiles(snapshotDirectory, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var security = new FileInfo(file).GetAccessControl();
            AssertOnlyCurrentUser(security, file);
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly), file);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                Assert.Equal(0, (int)(rule.FileSystemRights & WriteRights));
            }
        }
    }

    public static void AssertOnlyCurrentUser(FileSystemSecurity security, string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        Assert.True(security.AreAccessRulesProtected, $"DACL inherits from parent: {path}");
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(user, rule.IdentityReference);
        });
    }
}
