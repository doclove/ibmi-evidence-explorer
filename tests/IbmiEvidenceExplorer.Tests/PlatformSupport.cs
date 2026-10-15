using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// Cross-platform link creation for tests. When the OS refuses to create a link (Windows without
/// SeCreateSymbolicLinkPrivilege or Developer Mode), the test is reported as Skipped, never passed.
/// </summary>
public static class Links
{
    private const string PrivilegeReason =
        "Creating symbolic links on Windows requires SeCreateSymbolicLinkPrivilege (elevated/SYSTEM) or Developer Mode";

    public static void FileSymlink(string link, string target) => Create(() => File.CreateSymbolicLink(link, target));

    public static void DirectorySymlink(string link, string target) => Create(() => Directory.CreateSymbolicLink(link, target));

    /// <summary>A linked directory that every platform can create: a junction on Windows, a symlink elsewhere.</summary>
    public static void DirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Junction(link, target);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    public static void Junction(string link, string target)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Junctions exist only on Windows");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    private static void Create(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, $"{PrivilegeReason} ({ex.GetType().Name})");
        }
    }
}

/// <summary>Makes paths unreadable (chmod 000, or a deny ACE for the current user on Windows) and restores them.</summary>
public static class Access
{
    public static IDisposable DenyRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDeny(path);
        }

        Skip.If(Environment.IsPrivilegedProcess, "Unix permission bits are not enforced for root");
        var original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new Restore(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, original);
            }
        });
    }

    /// <summary>Undoes snapshot read-only protection so a test can tamper with stored data.</summary>
    public static void MakeWritable(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsMakeWritable(file);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsMakeWritable(string file)
    {
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
        info.Attributes &= ~FileAttributes.ReadOnly;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsDeny : IDisposable
    {
        private readonly FileSystemInfo _info;
        private readonly FileSystemAccessRule _rule;

        public WindowsDeny(string path)
        {
            _info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var rights = FileSystemRights.ReadData | FileSystemRights.Traverse;
            _rule = _info is DirectoryInfo
                ? new FileSystemAccessRule(CurrentUser(), rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny)
                : new FileSystemAccessRule(CurrentUser(), rights, AccessControlType.Deny);
            Update(security => security.AddAccessRule(_rule));
        }

        public void Dispose() => Update(security => security.RemoveAccessRule(_rule));

        private void Update(Action<FileSystemSecurity> change)
        {
            if (_info is DirectoryInfo directory)
            {
                var security = directory.GetAccessControl();
                change(security);
                directory.SetAccessControl(security);
            }
            else
            {
                var file = (FileInfo)_info;
                var security = file.GetAccessControl();
                change(security);
                file.SetAccessControl(security);
            }
        }
    }
}
