using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>
/// Reads files below an export root without following symbolic links. The root is opened once with
/// O_NOFOLLOW|O_DIRECTORY; every directory segment and the final file are then opened relative to
/// their parent descriptor with O_NOFOLLOW, and the file is required to be a regular file (fstat).
/// The kernel enforces no-follow at each open, so replacing a validated path with a link afterwards
/// cannot redirect a read. Ancestors of the root itself are trusted as chosen by the operator.
/// </summary>
internal sealed partial class UnixSourceTree : SecureSourceTree
{
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int ENOTDIR = 20;
    private const int S_IFMT = 0xF000;
    private const int S_IFREG = 0x8000;

    private static readonly Abi? Current = Abi.Detect();

    private readonly SafeFileHandle _root;
    private readonly string _rootPath;

    private UnixSourceTree(SafeFileHandle root, string rootPath)
    {
        _root = root;
        _rootPath = rootPath;
    }

    public static new SourceOpenStatus TryOpen(string rootPath, out SecureSourceTree? tree)
    {
        tree = null;
        if (Current is null)
        {
            return SourceOpenStatus.UnsupportedPlatform;
        }

        // A trailing separator would make the kernel resolve a final-segment link; strip it.
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var fd = Open(path, Current.ReadDirectory);
        if (fd < 0)
        {
            return Classify(Marshal.GetLastPInvokeError(), path, expectDirectory: true);
        }

        tree = new UnixSourceTree(new SafeFileHandle(fd, ownsHandle: true), path);
        return SourceOpenStatus.Opened;
    }

    /// <summary>Opens a regular file at a '/'-separated relative path. The caller owns the handle.</summary>
    public override SourceOpenStatus TryOpenFile(string relativePath, out SafeFileHandle? file)
    {
        file = null;
        var abi = Current!;
        if (Segments(relativePath) is not { } segments)
        {
            return SourceOpenStatus.NotFound;
        }

        var parent = _root;
        var opened = new List<SafeFileHandle>();
        try
        {
            for (var i = 0; i < segments.Length; i++)
            {
                var last = i == segments.Length - 1;
                var fd = OpenAt((int)parent.DangerousGetHandle(), segments[i], last ? abi.ReadFile : abi.ReadDirectory);
                if (fd < 0)
                {
                    var partial = Path.Combine([_rootPath, .. segments[..(i + 1)]]);
                    return Classify(Marshal.GetLastPInvokeError(), partial, expectDirectory: !last);
                }

                var handle = new SafeFileHandle(fd, ownsHandle: true);
                if (last)
                {
                    if (!IsRegularFile(handle, abi))
                    {
                        handle.Dispose();
                        return SourceOpenStatus.NotRegularFile;
                    }

                    file = handle;
                    return SourceOpenStatus.Opened;
                }

                opened.Add(handle);
                parent = handle;
            }

            return SourceOpenStatus.NotFound;
        }
        finally
        {
            foreach (var handle in opened)
            {
                handle.Dispose();
            }
        }
    }

    public override void Dispose() => _root.Dispose();

    private static bool IsRegularFile(SafeFileHandle handle, Abi abi)
    {
        var stat = new byte[512];
        var result = abi.UseInode64Fstat ? FStatInode64((int)handle.DangerousGetHandle(), stat) : FStat((int)handle.DangerousGetHandle(), stat);
        if (result != 0)
        {
            return false;
        }

        int mode = abi.ModeSize == 2 ? BitConverter.ToUInt16(stat, abi.ModeOffset) : (int)BitConverter.ToUInt32(stat, abi.ModeOffset);
        return (mode & S_IFMT) == S_IFREG;
    }

    /// <summary>
    /// Maps errno to a status. ELOOP/ENOTDIR are rejections either way; the managed link check only
    /// chooses the more precise diagnostic and has no bearing on safety.
    /// </summary>
    private static SourceOpenStatus Classify(int errno, string path, bool expectDirectory)
    {
        if (errno == Current!.Eloop || errno == ENOTDIR)
        {
            return IsLink(path) ? SourceOpenStatus.SymbolicLink : expectDirectory ? SourceOpenStatus.NotFound : SourceOpenStatus.NotRegularFile;
        }

        return errno switch
        {
            ENOENT => SourceOpenStatus.NotFound,
            EACCES => SourceOpenStatus.AccessDenied,
            _ => SourceOpenStatus.AccessDenied,
        };
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int directoryFd, string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(int fd, [Out] byte[] stat);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int FStatInode64(int fd, [Out] byte[] stat);

    /// <summary>Per-platform open flags, ELOOP value, and st_mode location in struct stat.</summary>
    private sealed record Abi(int Directory, int NoFollow, int NonBlock, int CloseOnExec, int Eloop, int ModeOffset, int ModeSize, bool UseInode64Fstat)
    {
        // O_NONBLOCK keeps a FIFO from blocking the open; it is then rejected as not a regular file.
        public int ReadFile => NoFollow | NonBlock | CloseOnExec;

        public int ReadDirectory => Directory | NoFollow | CloseOnExec;

        public static Abi? Detect()
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            if (OperatingSystem.IsMacOS() && arch is Architecture.Arm64 or Architecture.X64)
            {
                // struct stat (64-bit inode): st_dev int32, st_mode uint16 at offset 4.
                return new(0x100000, 0x100, 0x4, 0x1000000, 62, 4, 2, arch == Architecture.X64);
            }

            if (OperatingSystem.IsLinux() && arch == Architecture.X64)
            {
                return new(0x10000, 0x20000, 0x800, 0x80000, 40, 24, 4, false);
            }

            if (OperatingSystem.IsLinux() && arch == Architecture.Arm64)
            {
                return new(0x4000, 0x8000, 0x800, 0x80000, 40, 16, 4, false);
            }

            return null; // fail closed: no import without a verified no-follow reader (Windows uses WindowsSourceTree)
        }
    }
}
