using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>Raised when a storage directory exists but is not private to the current user.</summary>
internal sealed class UnsafeStorageException(string message) : IOException(message);

/// <summary>
/// Operator-private storage. Unix: directories <c>0700</c>, published files <c>0400</c> (unchanged
/// from the approved design). Windows equivalent: a protected DACL (no inheritance) that is owned
/// by and grants access only to the current user; published files get read-only rights
/// (Read, WriteAttributes, Synchronize) plus the ReadOnly attribute. On Windows, existing
/// workspace directories that are not private are refused rather than used.
/// </summary>
internal static class PrivateStorage
{
    /// <summary>Creates the directory if missing. When <paramref name="verifyExisting"/> is set (Windows), an existing directory must already be private.</summary>
    public static void CreateDirectory(string path, bool verifyExisting)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsCreateDirectory(path, verifyExisting);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Read-side check: an existing directory must be private (Windows); always true on Unix.</summary>
    public static bool IsPrivateOrMissing(string path) =>
        !OperatingSystem.IsWindows() || !Directory.Exists(path) || WindowsIsPrivate(new DirectoryInfo(path).GetAccessControl());

    /// <summary>
    /// Creates a new file (never overwriting) that only the current user can read or write: Unix
    /// <c>0600</c> at creation; Windows a protected, current-user-only DACL applied at creation.
    /// </summary>
    public static void WriteNewPrivateFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = OperatingSystem.IsWindows()
            ? WindowsCreatePrivateFile(path)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static void MakeReadOnly(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsMakeReadOnly(file);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsCreateDirectory(string path, bool verifyExisting)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            if (directory.Parent is { Exists: false } parent)
            {
                Directory.CreateDirectory(parent.FullName); // ancestors of the storage root are the operator's choice
            }

            var security = new DirectorySecurity();
            var user = CurrentUser();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security); // FileSystemAclExtensions: the DACL is applied at creation, no window
            directory.Refresh();
        }

        if (verifyExisting && !WindowsIsPrivate(directory.GetAccessControl()))
        {
            throw new UnsafeStorageException("Storage directory is not private to the current user.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream WindowsCreatePrivateFile(string path)
    {
        var user = CurrentUser();
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize, FileShare.None, 4096,
            FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsMakeReadOnly(string file)
    {
        var info = new FileInfo(file);
        info.Attributes |= FileAttributes.ReadOnly;
        var user = CurrentUser();
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user,
            FileSystemRights.Read | FileSystemRights.WriteAttributes | FileSystemRights.Synchronize, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Protected DACL, owned by the current user, and no allow entry for anyone else.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool WindowsIsPrivate(FileSystemSecurity security)
    {
        var user = CurrentUser();
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != user)
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference))
            {
                return false;
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnsafeStorageException("Current Windows user has no SID.");
    }
}
