using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>
/// Decides whether a new output path lands in the source tree once every symbolic link and
/// junction is resolved. Comparing the operator's path strings alone is not enough: an output
/// parent that is (or passes through) a link to the source root would place output inside it.
/// Windows asks the OS for the final path of an opened directory, which also expands 8.3 names
/// and junctions; macOS and Linux resolve each component without following it implicitly,
/// applying '..' to the already resolved prefix as the kernel does.
/// </summary>
internal static partial class PathContainment
{
    private const int MaxLinkHops = 40;

    /// <summary>
    /// True when <paramref name="path"/>, with links in its parent resolved, is the root or below
    /// it; false when it is outside; null when the parent or the root cannot be resolved.
    /// </summary>
    public static bool? IsWithin(string path, string root)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || ResolveDirectory(parent) is not { } resolvedParent || ResolveDirectory(root) is not { } resolvedRoot)
        {
            return null;
        }

        var candidate = Path.Join(resolvedParent, Path.GetFileName(path));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var prefix = Path.EndsInDirectorySeparator(resolvedRoot) ? resolvedRoot : resolvedRoot + Path.DirectorySeparatorChar;
        return candidate.Equals(resolvedRoot, comparison) || candidate.StartsWith(prefix, comparison);
    }

    /// <summary>The directory's path with every link resolved; null if it does not exist or cannot be resolved.</summary>
    public static string? ResolveDirectory(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            return OperatingSystem.IsWindows() ? WindowsFinalPath(full) : ResolveComponents(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveComponents(string full)
    {
        var current = Path.GetPathRoot(full)!;
        var pending = new Stack<string>(Components(full[current.Length..]).Reverse());
        var hops = 0;
        while (pending.TryPop(out var name))
        {
            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? current; // the prefix is resolved, so its parent is physical
                continue;
            }

            var next = Path.Join(current, name);
            if (new DirectoryInfo(next).LinkTarget is { } target)
            {
                if (++hops > MaxLinkHops)
                {
                    return null;
                }

                if (Path.IsPathRooted(target))
                {
                    current = Path.GetPathRoot(target)!;
                    target = target[current.Length..];
                }

                foreach (var part in Components(target).Reverse())
                {
                    pending.Push(part);
                }

                continue;
            }

            if (!Directory.Exists(next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static string[] Components(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    // ---- Windows ------------------------------------------------------------------------------

    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [SupportedOSPlatform("windows")]
    private static unsafe string? WindowsFinalPath(string full)
    {
        var extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
        using var handle = CreateFileW(extended, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !Directory.Exists(full))
        {
            return null;
        }

        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
            {
                length = GetFinalPathNameByHandleW(handle, chars, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                var final = new string(buffer, 0, (int)length);
                return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
                    : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
                    : final;
            }

            buffer = new char[length];
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);
}
