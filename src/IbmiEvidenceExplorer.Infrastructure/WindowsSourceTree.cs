using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>
/// Windows counterpart of <see cref="UnixSourceTree"/>. The root is opened with CreateFileW using
/// FILE_FLAG_OPEN_REPARSE_POINT, so a symlinked or junction root is opened as itself and rejected.
/// Each segment is then opened with NtCreateFile relative to the parent handle
/// (OBJECT_ATTRIBUTES.RootDirectory) and FILE_OPEN_REPARSE_POINT, and any object carrying
/// FILE_ATTRIBUTE_REPARSE_POINT (symlink, junction, or other reparse point) is rejected. The member
/// handle is the object that was verified, and directory handles are held without FILE_SHARE_DELETE,
/// so no segment can be renamed or replaced by a link while the walk is in progress. Members are
/// opened sharing read only; a member another process holds open for writing is reported unreadable.
/// Ancestors of the root are trusted as chosen by the operator, as on Unix.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsSourceTree : SecureSourceTree
{
    private const uint FILE_READ_DATA = 0x0001;            // FILE_LIST_DIRECTORY for directories
    private const uint FILE_TRAVERSE = 0x0020;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint DirectoryAccess = FILE_READ_DATA | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE;
    private const uint FileAccess = FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE;

    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;

    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private const uint FILE_OPEN = 1;
    private const uint FILE_DIRECTORY_FILE = 0x00000001;
    private const uint FILE_SEQUENTIAL_ONLY = 0x00000004;
    private const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    private const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
    private const uint FILE_OPEN_REPARSE_POINT = 0x00200000;
    private const uint OBJ_CASE_INSENSITIVE = 0x40;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int FileAttributeTagInfo = 9;
    private const uint FILE_TYPE_DISK = 1;

    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;

    private const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    private const int STATUS_SHARING_VIOLATION = unchecked((int)0xC0000043);
    private const int STATUS_NOT_A_DIRECTORY = unchecked((int)0xC0000103);
    private const int STATUS_FILE_IS_A_DIRECTORY = unchecked((int)0xC00000BA);
    private const int STATUS_OBJECT_NAME_INVALID = unchecked((int)0xC0000033);
    private const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
    private const int STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);
    private const int STATUS_OBJECT_PATH_SYNTAX_BAD = unchecked((int)0xC000003B);
    private const int STATUS_DELETE_PENDING = unchecked((int)0xC0000056);

    private readonly SafeFileHandle _root;
    private readonly string _rootPath;

    private WindowsSourceTree(SafeFileHandle root, string rootPath)
    {
        _root = root;
        _rootPath = rootPath;
    }

    private enum ObjectKind
    {
        Directory,
        RegularFile,
        ReparsePoint,
        Other,
    }

    public static new SourceOpenStatus TryOpen(string rootPath, out SecureSourceTree? tree)
    {
        tree = null;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var handle = CreateFileW(ExtendedLengthPath(path), DirectoryAccess, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION ? SourceOpenStatus.AccessDenied : SourceOpenStatus.NotFound;
        }

        switch (Classify(handle))
        {
            case ObjectKind.Directory:
                tree = new WindowsSourceTree(handle, path);
                return SourceOpenStatus.Opened;
            case ObjectKind.ReparsePoint:
                handle.Dispose();
                return SourceOpenStatus.SymbolicLink;
            default:
                handle.Dispose();
                return SourceOpenStatus.NotFound;
        }
    }

    public override SourceOpenStatus TryOpenFile(string relativePath, out SafeFileHandle? file)
    {
        file = null;
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
                var status = OpenRelative(parent, segments[i], directory: !last, out var handle);
                if (status != 0)
                {
                    return MapStatus(status, Path.Combine([_rootPath, .. segments[..(i + 1)]]));
                }

                var kind = Classify(handle!);
                if (kind == ObjectKind.ReparsePoint)
                {
                    handle!.Dispose();
                    return SourceOpenStatus.SymbolicLink;
                }

                if (last)
                {
                    if (kind != ObjectKind.RegularFile)
                    {
                        handle!.Dispose();
                        return SourceOpenStatus.NotRegularFile;
                    }

                    file = handle;
                    return SourceOpenStatus.Opened;
                }

                if (kind != ObjectKind.Directory)
                {
                    handle!.Dispose();
                    return SourceOpenStatus.NotFound;
                }

                opened.Add(handle!);
                parent = handle!;
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

    private static int OpenRelative(SafeFileHandle parent, string name, bool directory, out SafeFileHandle? handle)
    {
        handle = null;
        var addedReference = false;
        parent.DangerousAddRef(ref addedReference);
        try
        {
            fixed (char* buffer = name)
            {
                var unicodeName = new UNICODE_STRING
                {
                    Length = checked((ushort)(name.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(name.Length * sizeof(char))),
                    Buffer = buffer,
                };
                var attributes = new OBJECT_ATTRIBUTES
                {
                    Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = &unicodeName,
                    Attributes = OBJ_CASE_INSENSITIVE,
                };
                var options = FILE_SYNCHRONOUS_IO_NONALERT | FILE_OPEN_REPARSE_POINT
                    | (directory ? FILE_DIRECTORY_FILE : FILE_NON_DIRECTORY_FILE | FILE_SEQUENTIAL_ONLY);
                var status = NtCreateFile(out var raw, directory ? DirectoryAccess : FileAccess, &attributes, out _,
                    IntPtr.Zero, 0, directory ? FILE_SHARE_READ | FILE_SHARE_WRITE : FILE_SHARE_READ, FILE_OPEN, options, IntPtr.Zero, 0);
                if (status >= 0)
                {
                    handle = new SafeFileHandle(raw, ownsHandle: true);
                }

                return status >= 0 ? 0 : status;
            }
        }
        finally
        {
            if (addedReference)
            {
                parent.DangerousRelease();
            }
        }
    }

    private static ObjectKind Classify(SafeFileHandle handle)
    {
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            return ObjectKind.Other;
        }

        if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return ObjectKind.ReparsePoint;
        }

        if ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            return ObjectKind.Directory;
        }

        return GetFileType(handle) == FILE_TYPE_DISK ? ObjectKind.RegularFile : ObjectKind.Other;
    }

    /// <summary>
    /// Maps NTSTATUS to a status. Not-a-directory / is-a-directory are rejections either way; the
    /// managed link check only picks the more precise diagnostic.
    /// </summary>
    private static SourceOpenStatus MapStatus(int status, string path) => status switch
    {
        STATUS_NOT_A_DIRECTORY => IsLink(path) ? SourceOpenStatus.SymbolicLink : SourceOpenStatus.NotFound,
        STATUS_FILE_IS_A_DIRECTORY => IsLink(path) ? SourceOpenStatus.SymbolicLink : SourceOpenStatus.NotRegularFile,
        STATUS_OBJECT_NAME_NOT_FOUND or STATUS_OBJECT_PATH_NOT_FOUND or STATUS_OBJECT_NAME_INVALID
            or STATUS_OBJECT_PATH_SYNTAX_BAD or STATUS_DELETE_PENDING => SourceOpenStatus.NotFound,
        STATUS_ACCESS_DENIED or STATUS_SHARING_VIOLATION => SourceOpenStatus.AccessDenied,
        _ => SourceOpenStatus.AccessDenied,
    };

    /// <summary>Uses the \\?\ form so long roots work and Win32 does not reinterpret the normalized path.</summary>
    private static string ExtendedLengthPath(string fullPath) =>
        fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
        : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
        : @"\\?\" + fullPath;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static partial int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, OBJECT_ATTRIBUTES* objectAttributes,
        out IO_STATUS_BLOCK ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition,
        uint createOptions, IntPtr eaBuffer, uint eaLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, FILE_ATTRIBUTE_TAG_INFO* information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType")]
    private static partial uint GetFileType(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public UNICODE_STRING* ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}
