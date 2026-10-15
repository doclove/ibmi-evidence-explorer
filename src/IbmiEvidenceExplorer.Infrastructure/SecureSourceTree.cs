using Microsoft.Win32.SafeHandles;

namespace IbmiEvidenceExplorer.Infrastructure;

internal enum SourceOpenStatus
{
    Opened,
    NotFound,
    SymbolicLink,
    NotRegularFile,
    AccessDenied,
    UnsupportedPlatform,
}

/// <summary>
/// Opens files below an export root by walking handles, never by re-resolving a validated path:
/// the root and each segment are opened without following links, and the final object must be a
/// regular file. The returned handle is the verified object, so a path swapped to a link after
/// validation cannot redirect the read. <see cref="UnixSourceTree"/> uses openat/O_NOFOLLOW;
/// <see cref="WindowsSourceTree"/> uses NtCreateFile relative opens with FILE_OPEN_REPARSE_POINT.
/// </summary>
internal abstract class SecureSourceTree : IDisposable
{
    public static SourceOpenStatus TryOpen(string rootPath, out SecureSourceTree? tree) =>
        OperatingSystem.IsWindows()
            ? WindowsSourceTree.TryOpen(rootPath, out tree)
            : UnixSourceTree.TryOpen(rootPath, out tree);

    /// <summary>Opens a regular file at a '/'-separated relative path. The caller owns the handle.</summary>
    public abstract SourceOpenStatus TryOpenFile(string relativePath, out SafeFileHandle? file);

    public abstract void Dispose();

    /// <summary>Reads at most <paramref name="maxBytes"/> from the handle; returns null if there is more.</summary>
    public static byte[]? ReadBounded(SafeFileHandle handle, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long offset = 0;
        int read;
        while ((read = RandomAccess.Read(handle, chunk, offset)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            offset += read;
        }

        return buffer.ToArray();
    }

    /// <summary>Splits a manifest path into segments, refusing anything that is not a plain name.</summary>
    protected static string[]? Segments(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\\', StringComparison.Ordinal) || s.Contains(':', StringComparison.Ordinal))
            ? null
            : segments;
    }

    /// <summary>Only chooses the more precise diagnostic after the OS already refused an open.</summary>
    protected static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
