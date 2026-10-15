using System.Globalization;
using System.Text.Json;
using IbmiEvidenceExplorer.Core;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>
/// Stores snapshots under <c>{root}/workspaces/{workspaceId}/snapshots/{snapshotId}</c>. An import is
/// staged under <c>{root}/workspaces/{workspaceId}/staging</c>, verified, marked complete, made
/// read-only, and published with a single directory rename. Only directories that carry a valid
/// completion marker are ever reported as snapshots.
/// </summary>
public sealed class FileSystemSnapshotStore : ISnapshotStore
{
    internal const string WorkspacesDirectory = "workspaces";
    internal const string SnapshotsDirectory = "snapshots";
    internal const string StagingDirectory = "staging";
    internal const string MembersDirectory = "members";
    internal const string MetadataFile = "snapshot.json";
    internal const string MarkerFile = "complete.json";

    private const long MaxMetadataBytes = 256 * ImportLimits.MiB;
    private const long MaxMarkerBytes = 64 * 1024;

    private readonly string _root;
    private readonly IOperationLog _log;
    private readonly TimeProvider _time;

    public FileSystemSnapshotStore(string storageRoot, IOperationLog? log = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _root = Path.GetFullPath(storageRoot);
        _log = log ?? NullOperationLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string StorageRoot => _root;

    /// <summary>Test hook: invoked at each staging stage so tests can simulate a failed or interrupted import.</summary>
    internal Action<ImportStage>? FaultInjector { get; set; }

    public OperationResult<ImportOutcome> Import(ImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = AnalysisRunId.New();
        Log(OperationLogLevel.Information, "import.started", ("workspaceId", request.WorkspaceId.Value), ("analysisRunId", runId),
            ("maxMemberBytes", Text(request.Limits.MaxMemberBytes)), ("maxImportBytes", Text(request.Limits.MaxImportBytes)),
            ("maxMembers", Text(request.Limits.MaxMembers)));

        OperationResult<ImportOutcome> result;
        try
        {
            result = ImportCore(request, runId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = Fail<ImportOutcome>(new Diagnostic(DiagnosticCode.SourceUnreadable,
                $"Source export could not be read ({ex.GetType().Name}); nothing was published."));
        }

        if (result.Succeeded)
        {
            var snapshot = result.Value.Snapshot;
            Log(OperationLogLevel.Information, "import.published", ("workspaceId", request.WorkspaceId.Value), ("analysisRunId", runId),
                ("snapshotId", snapshot.SnapshotId.Value), ("memberCount", Text(snapshot.Members.Count)), ("totalBytes", Text(snapshot.TotalBytes)));
        }
        else
        {
            LogDiagnostics("import.rejected", request.WorkspaceId, runId, result.Diagnostics);
        }

        return result;
    }

    public OperationResult<SnapshotListing> List(WorkspaceId workspaceId)
    {
        RequireWorkspace(workspaceId);
        try
        {
            if (CheckStorage(workspaceId) is { } storageProblem)
            {
                return Denied<SnapshotListing>("snapshot.list.denied", workspaceId, storageProblem);
            }

            var snapshots = new List<SnapshotSummary>();
            var ignored = 0;
            var snapshotsPath = SnapshotsPath(workspaceId);
            if (Directory.Exists(snapshotsPath))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(snapshotsPath))
                {
                    var name = Path.GetFileName(entry);
                    var loaded = SnapshotId.TryParse(name, out var snapshotId) ? LoadSnapshot(workspaceId, snapshotId) : null;
                    if (loaded is { Succeeded: true })
                    {
                        snapshots.Add(loaded.Value);
                    }
                    else
                    {
                        ignored++;
                    }
                }
            }

            snapshots.Sort((a, b) => a.CreatedUtc != b.CreatedUtc
                ? a.CreatedUtc.CompareTo(b.CreatedUtc)
                : string.CompareOrdinal(a.SnapshotId.Value, b.SnapshotId.Value));
            Log(OperationLogLevel.Information, "snapshot.listed", ("workspaceId", workspaceId.Value),
                ("snapshotCount", Text(snapshots.Count)), ("ignoredEntries", Text(ignored)));
            return OperationResult<SnapshotListing>.Success(new(workspaceId, snapshots, ignored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Denied<SnapshotListing>("snapshot.list.denied", workspaceId, StorageUnavailable(ex));
        }
    }

    public OperationResult<MemberInspection> Inspect(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity member, LineRange? lines = null)
    {
        RequireWorkspace(workspaceId);
        ArgumentNullException.ThrowIfNull(member);
        var result = InspectCore(workspaceId, snapshotId, member, lines);
        if (result.Succeeded)
        {
            Log(OperationLogLevel.Information, "member.inspected", ("workspaceId", workspaceId.Value), ("snapshotId", snapshotId.Value),
                ("member", member.ToString()), ("lineCount", Text(result.Value.Lines.Count)));
            return result;
        }

        return Denied<MemberInspection>("member.inspect.denied", workspaceId, [.. result.Diagnostics]);
    }

    public OperationResult<ResolvedEvidence> Resolve(WorkspaceId workspaceId, EvidenceReference reference)
    {
        RequireWorkspace(workspaceId);
        ArgumentNullException.ThrowIfNull(reference);
        var loaded = LoadMember(workspaceId, reference.SnapshotId, reference.Member);
        if (!loaded.Succeeded)
        {
            return Denied<ResolvedEvidence>("evidence.resolve.denied", workspaceId, [.. loaded.Diagnostics]);
        }

        var (snapshot, member, bytes, sourceLines) = loaded.Value;
        var line = reference.LineNumber >= 1 && reference.LineNumber <= sourceLines.Count ? sourceLines[reference.LineNumber - 1] : null;
        var matches = line is not null
            && reference.MemberSha256 == member.Sha256
            && reference.LineSha256 == line.Sha256
            && reference.ByteOffset == line.ByteOffset
            && reference.ByteLength == line.ByteLength
            && reference.AnalysisRunId == snapshot.AnalysisRunId
            && reference.ImportFormatVersion == snapshot.ImportFormatVersion
            && reference.ParserVersion == snapshot.ParserVersion;
        if (!matches)
        {
            return Denied<ResolvedEvidence>("evidence.resolve.denied", workspaceId,
                new Diagnostic(DiagnosticCode.EvidenceMismatch, "Evidence reference does not match the stored snapshot, member, or line."));
        }

        var slice = bytes.AsSpan((int)line!.ByteOffset, line.ByteLength).ToArray();
        Log(OperationLogLevel.Information, "evidence.resolved", ("workspaceId", workspaceId.Value), ("snapshotId", snapshot.SnapshotId.Value),
            ("member", member.Identity.ToString()), ("lineNumber", Text(line.Number)));
        return OperationResult<ResolvedEvidence>.Success(new(reference, slice, Utf8Text.Decode(slice)));
    }

    public OperationResult<ISnapshotReader> OpenSnapshot(WorkspaceId workspaceId, SnapshotId snapshotId)
    {
        RequireWorkspace(workspaceId);
        try
        {
            if (CheckStorage(workspaceId) is { } storageProblem)
            {
                return Denied<ISnapshotReader>("snapshot.open.denied", workspaceId, storageProblem);
            }

            var snapshot = LoadSnapshot(workspaceId, snapshotId);
            return snapshot.Succeeded
                ? OperationResult<ISnapshotReader>.Success(new SnapshotReader(this, workspaceId, snapshot.Value))
                : Denied<ISnapshotReader>("snapshot.open.denied", workspaceId, [.. snapshot.Diagnostics]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Denied<ISnapshotReader>("snapshot.open.denied", workspaceId, StorageUnavailable(ex));
        }
    }

    // ---- Import -------------------------------------------------------------------------------

    private OperationResult<ImportOutcome> ImportCore(ImportRequest request, string runId, CancellationToken cancellationToken)
    {
        var limits = request.Limits;
        var rootStatus = SecureSourceTree.TryOpen(request.SourceRoot, out var openedTree);
        if (rootStatus != SourceOpenStatus.Opened)
        {
            return Fail<ImportOutcome>(rootStatus switch
            {
                SourceOpenStatus.SymbolicLink => new Diagnostic(DiagnosticCode.SymlinkRejected, "Source directory must not be a symbolic link, junction, or other reparse point."),
                SourceOpenStatus.AccessDenied => new Diagnostic(DiagnosticCode.SourceUnreadable, "Source directory is not readable."),
                SourceOpenStatus.UnsupportedPlatform => new Diagnostic(DiagnosticCode.SourceUnreadable,
                    "Secure (no-follow) source reading is supported only on Windows, and on macOS and Linux (x64, arm64)."),
                _ => new Diagnostic(DiagnosticCode.SourceRootNotFound, "Source directory does not exist or is not a directory."),
            });
        }

        using var tree = openedTree!;
        var manifestBytes = ReadManifest(tree);
        if (!manifestBytes.Succeeded)
        {
            return Fail<ImportOutcome>([.. manifestBytes.Diagnostics]);
        }

        var parsed = ManifestParser.Parse(manifestBytes.Value);
        if (!parsed.Succeeded)
        {
            return Fail<ImportOutcome>([.. parsed.Diagnostics]);
        }

        var entries = parsed.Value;
        if (entries.Count > limits.MaxMembers)
        {
            return Fail<ImportOutcome>(new Diagnostic(DiagnosticCode.TooManyMembers,
                $"Manifest declares {entries.Count} members; the limit is {limits.MaxMembers}."));
        }

        // Preflight every entry before writing anything.
        var diagnostics = new List<Diagnostic>();
        long declaredTotal = 0;
        foreach (var entry in entries)
        {
            var length = MeasureSourceFile(tree, entry, diagnostics);
            if (length is null)
            {
                continue;
            }

            if (length > limits.MaxMemberBytes)
            {
                diagnostics.Add(new(DiagnosticCode.MemberTooLarge,
                    $"File is {length} bytes; the per-member limit is {limits.MaxMemberBytes}.",
                    entry.Index, ManifestParser.ForDisplay(entry.RelativePath)));
            }

            declaredTotal += length.Value;
        }

        if (declaredTotal > limits.MaxImportBytes)
        {
            diagnostics.Add(new(DiagnosticCode.ImportTooLarge,
                $"Import totals {declaredTotal} bytes; the per-import limit is {limits.MaxImportBytes}."));
        }

        if (diagnostics.Count > 0)
        {
            return Fail<ImportOutcome>([.. diagnostics]);
        }

        FaultInjector?.Invoke(ImportStage.SourcesValidated);

        if (PrepareWorkspace(request.WorkspaceId) is { } storageProblem)
        {
            return Fail<ImportOutcome>(storageProblem);
        }

        var snapshotId = SnapshotId.New();
        var staging = Path.Combine(StagingPath(request.WorkspaceId), $"{snapshotId.Value}.{runId}");
        var published = false;
        try
        {
            PrivateStorage.CreateDirectory(staging, verifyExisting: true);
            PrivateStorage.CreateDirectory(Path.Combine(staging, MembersDirectory), verifyExisting: true);

            var members = new List<MemberDocument>(entries.Count);
            long total = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var display = ManifestParser.ForDisplay(entry.RelativePath);
                // Re-open through the no-follow walk: nothing validated above is trusted at read time.
                var status = tree.TryOpenFile(entry.RelativePath, out var handle);
                if (status != SourceOpenStatus.Opened)
                {
                    diagnostics.Add(SourceFileDiagnostic(status, entry));
                    continue;
                }

                byte[]? read;
                using (handle)
                {
                    read = SecureSourceTree.ReadBounded(handle!, limits.MaxMemberBytes);
                }

                if (read is null)
                {
                    diagnostics.Add(new(DiagnosticCode.MemberTooLarge,
                        $"File grew beyond the per-member limit of {limits.MaxMemberBytes} bytes while importing.", entry.Index, display));
                    continue;
                }

                total += read.Length;
                if (!Utf8Text.TryValidate(read, out var invalidOffset))
                {
                    diagnostics.Add(new(DiagnosticCode.InvalidUtf8,
                        $"File is not valid UTF-8 (CCSID 1208): first invalid byte at offset {invalidOffset}, line {SourceLines.LineNumberAt(read, invalidOffset)}.",
                        entry.Index, display));
                    continue;
                }

                if (diagnostics.Count > 0)
                {
                    continue; // keep validating to report every problem, but stop writing
                }

                WriteDurably(Path.Combine(staging, MemberFileName(entry.Index)), read);
                members.Add(new MemberDocument
                {
                    Ordinal = entry.Index,
                    Library = entry.Identity.Library,
                    SourceFile = entry.Identity.SourceFile,
                    Member = entry.Identity.Member,
                    SourceType = entry.Identity.SourceType,
                    Ccsid = entry.Ccsid,
                    SourcePath = entry.RelativePath,
                    ByteLength = read.Length,
                    Sha256 = Utf8Text.Sha256Hex(read),
                    LineCount = SourceLines.Index(read).Count,
                });
                FaultInjector?.Invoke(ImportStage.MemberStaged);
            }

            if (total > limits.MaxImportBytes)
            {
                diagnostics.Add(new(DiagnosticCode.ImportTooLarge,
                    $"Files grew beyond the per-import limit of {limits.MaxImportBytes} bytes while importing."));
            }

            if (diagnostics.Count > 0)
            {
                return Fail<ImportOutcome>([.. diagnostics]);
            }

            var metadata = new SnapshotMetadataDocument
            {
                SchemaVersion = FormatVersions.SnapshotSchema,
                ImportFormatVersion = FormatVersions.ImportFormat,
                ParserVersion = FormatVersions.ParserVersion,
                WorkspaceId = request.WorkspaceId.Value,
                SnapshotId = snapshotId.Value,
                AnalysisRunId = runId,
                CreatedUtc = _time.GetUtcNow(),
                ManifestSha256 = Utf8Text.Sha256Hex(manifestBytes.Value),
                Limits = new LimitsDocument
                {
                    MaxMemberBytes = limits.MaxMemberBytes,
                    MaxImportBytes = limits.MaxImportBytes,
                    MaxMembers = limits.MaxMembers,
                },
                TotalBytes = total,
                MemberCount = members.Count,
                Members = members,
            };
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, SnapshotJson.Options);
            WriteDurably(Path.Combine(staging, MetadataFile), metadataBytes);
            FaultInjector?.Invoke(ImportStage.MetadataWritten);

            // Verify what was written before declaring it complete.
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stored = ReadBounded(Path.Combine(staging, MemberFileName(member.Ordinal)), member.ByteLength);
                if (stored is null || stored.Length != member.ByteLength || Utf8Text.Sha256Hex(stored) != member.Sha256)
                {
                    return Fail<ImportOutcome>(new Diagnostic(DiagnosticCode.PublishFailed,
                        "Staged member bytes did not verify against the computed hash.", member.Ordinal, ManifestParser.ForDisplay(member.SourcePath)));
                }
            }

            var marker = new CompletionMarkerDocument
            {
                SchemaVersion = FormatVersions.SnapshotSchema,
                WorkspaceId = request.WorkspaceId.Value,
                SnapshotId = snapshotId.Value,
                MetadataSha256 = Utf8Text.Sha256Hex(metadataBytes),
                CompletedUtc = _time.GetUtcNow(),
            };
            WriteDurably(Path.Combine(staging, MarkerFile), JsonSerializer.SerializeToUtf8Bytes(marker, SnapshotJson.Options));
            FaultInjector?.Invoke(ImportStage.MarkerWritten);
            cancellationToken.ThrowIfCancellationRequested();

            MakeFilesReadOnly(staging);
            Directory.Move(staging, Path.Combine(SnapshotsPath(request.WorkspaceId), snapshotId.Value));
            published = true;

            var summary = LoadSnapshot(request.WorkspaceId, snapshotId);
            return summary.Succeeded
                ? OperationResult<ImportOutcome>.Success(new(summary.Value))
                : Fail<ImportOutcome>([.. summary.Diagnostics]);
        }
        catch (OperationCanceledException)
        {
            return Fail<ImportOutcome>(new Diagnostic(DiagnosticCode.ImportCancelled, "Import was cancelled; nothing was published."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail<ImportOutcome>(new Diagnostic(DiagnosticCode.PublishFailed,
                $"Import could not be staged or published ({ex.GetType().Name}); nothing was published."));
        }
        finally
        {
            if (!published)
            {
                RemoveStaging(staging, request.WorkspaceId, runId);
            }
        }
    }

    private static OperationResult<byte[]> ReadManifest(SecureSourceTree tree)
    {
        var status = tree.TryOpenFile(ManifestParser.FileName, out var handle);
        if (status != SourceOpenStatus.Opened)
        {
            return OperationResult<byte[]>.Failure(status switch
            {
                SourceOpenStatus.SymbolicLink => new Diagnostic(DiagnosticCode.SymlinkRejected, "Manifest must not be a symbolic link or reparse point.", Path: ManifestParser.FileName),
                SourceOpenStatus.AccessDenied => new Diagnostic(DiagnosticCode.SourceUnreadable, "Manifest is not readable.", Path: ManifestParser.FileName),
                SourceOpenStatus.NotRegularFile => new Diagnostic(DiagnosticCode.ManifestNotFound, "Manifest is not a regular file.", Path: ManifestParser.FileName),
                _ => new Diagnostic(DiagnosticCode.ManifestNotFound, $"Source directory has no {ManifestParser.FileName}."),
            });
        }

        using (handle)
        {
            var bytes = SecureSourceTree.ReadBounded(handle!, ImportLimits.MaxManifestBytes);
            return bytes is null
                ? OperationResult<byte[]>.Failure(new Diagnostic(DiagnosticCode.ManifestTooLarge, $"Manifest exceeds {ImportLimits.MaxManifestBytes} bytes."))
                : OperationResult<byte[]>.Success(bytes);
        }
    }

    /// <summary>Opens a member through the no-follow walk to check it and report its current length.</summary>
    private static long? MeasureSourceFile(SecureSourceTree tree, ManifestEntry entry, List<Diagnostic> diagnostics)
    {
        var status = tree.TryOpenFile(entry.RelativePath, out var handle);
        if (status != SourceOpenStatus.Opened)
        {
            diagnostics.Add(SourceFileDiagnostic(status, entry));
            return null;
        }

        using (handle)
        {
            return RandomAccess.GetLength(handle!);
        }
    }

    private static Diagnostic SourceFileDiagnostic(SourceOpenStatus status, ManifestEntry entry)
    {
        var display = ManifestParser.ForDisplay(entry.RelativePath);
        return status switch
        {
            SourceOpenStatus.SymbolicLink => new(DiagnosticCode.SymlinkRejected, "Path passes through or names a symbolic link, junction, or other reparse point.", entry.Index, display),
            SourceOpenStatus.AccessDenied => new(DiagnosticCode.SourceUnreadable, "Referenced file or one of its directories is not readable.", entry.Index, display),
            _ => new(DiagnosticCode.SourceFileNotFound, "Referenced file does not exist or is not a regular file.", entry.Index, display),
        };
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; returns null if the file is longer.</summary>
    private static byte[]? ReadBounded(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void WriteDurably(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void MakeFilesReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            PrivateStorage.MakeReadOnly(file);
        }
    }

    private void RemoveStaging(string staging, WorkspaceId workspaceId, string runId)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                if (OperatingSystem.IsWindows())
                {
                    // The ReadOnly attribute blocks deletion on Windows; the file DACL still allows clearing it.
                    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(OperationLogLevel.Warning, "import.staging_cleanup_failed", ("workspaceId", workspaceId.Value), ("analysisRunId", runId),
                ("exception", ex.GetType().Name));
        }
    }

    // ---- Reading ------------------------------------------------------------------------------

    private OperationResult<MemberInspection> InspectCore(WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity identity, LineRange? range)
    {
        var loaded = LoadMember(workspaceId, snapshotId, identity);
        if (!loaded.Succeeded)
        {
            return Fail<MemberInspection>([.. loaded.Diagnostics]);
        }

        return BuildInspection(loaded.Value, range);
    }

    private static OperationResult<MemberInspection> BuildInspection(
        (SnapshotSummary Snapshot, SnapshotMember Member, byte[] Bytes, IReadOnlyList<SourceLine> Lines) loaded, LineRange? range)
    {
        var (snapshot, member, bytes, sourceLines) = loaded;
        var first = range?.First ?? 1;
        var last = range?.Last ?? sourceLines.Count;
        if (range is not null && (first < 1 || last < first || last > sourceLines.Count))
        {
            return Fail<MemberInspection>(new Diagnostic(DiagnosticCode.LineOutOfRange,
                $"Requested lines {first}-{last}; member has {sourceLines.Count} line(s)."));
        }

        var lines = new List<EvidenceLine>(Math.Max(0, last - first + 1));
        for (var number = first; number <= last; number++)
        {
            var line = sourceLines[number - 1];
            var reference = new EvidenceReference(snapshot.SnapshotId, member.Identity, line.Number, member.Sha256, line.Sha256,
                line.ByteOffset, line.ByteLength, snapshot.ImportFormatVersion, snapshot.ParserVersion, snapshot.AnalysisRunId);
            lines.Add(new(reference, Utf8Text.Decode(bytes.AsSpan((int)line.ByteOffset, line.ByteLength))));
        }

        return OperationResult<MemberInspection>.Success(new(snapshot, member, lines));
    }

    private OperationResult<(SnapshotSummary Snapshot, SnapshotMember Member, byte[] Bytes, IReadOnlyList<SourceLine> Lines)> LoadMember(
        WorkspaceId workspaceId, SnapshotId snapshotId, MemberIdentity identity)
    {
        try
        {
            if (CheckStorage(workspaceId) is { } storageProblem)
            {
                return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>(storageProblem);
            }

            var snapshot = LoadSnapshot(workspaceId, snapshotId);
            if (!snapshot.Succeeded)
            {
                return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>([.. snapshot.Diagnostics]);
            }

            return LoadMemberBytes(workspaceId, snapshot.Value, identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>(StorageUnavailable(ex));
        }
    }

    /// <summary>Reads one member of an already verified snapshot and checks its bytes, UTF-8, and line count.</summary>
    private OperationResult<(SnapshotSummary Snapshot, SnapshotMember Member, byte[] Bytes, IReadOnlyList<SourceLine> Lines)> LoadMemberBytes(
        WorkspaceId workspaceId, SnapshotSummary snapshot, MemberIdentity identity)
    {
        try
        {
            var member = snapshot.Members.FirstOrDefault(m => m.Identity == identity);
            if (member is null)
            {
                return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>(
                    new Diagnostic(DiagnosticCode.MemberNotFound, $"Snapshot has no member {identity}."));
            }

            var path = Path.Combine(SnapshotsPath(workspaceId), snapshot.SnapshotId.Value, MemberFileName(member.Ordinal));
            var bytes = new FileInfo(path).LinkTarget is null ? ReadBounded(path, member.ByteLength) : null;
            if (bytes is null || bytes.Length != member.ByteLength || Utf8Text.Sha256Hex(bytes) != member.Sha256
                || !Utf8Text.TryValidate(bytes, out _))
            {
                return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>(
                    new Diagnostic(DiagnosticCode.IntegrityFailure, $"Stored bytes for {identity} do not match the recorded SHA-256."));
            }

            var lines = SourceLines.Index(bytes);
            if (lines.Count != member.LineCount)
            {
                return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>(
                    new Diagnostic(DiagnosticCode.IntegrityFailure, $"Stored line count for {identity} does not match metadata."));
            }

            return OperationResult<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>.Success((snapshot, member, bytes, lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail<(SnapshotSummary, SnapshotMember, byte[], IReadOnlyList<SourceLine>)>(StorageUnavailable(ex));
        }
    }

    /// <summary>
    /// Loads a published snapshot, requiring a real directory, a valid completion marker whose hash
    /// matches the metadata, and metadata that names this workspace and snapshot.
    /// </summary>
    private OperationResult<SnapshotSummary> LoadSnapshot(WorkspaceId workspaceId, SnapshotId snapshotId)
    {
        var directory = new DirectoryInfo(Path.Combine(SnapshotsPath(workspaceId), snapshotId.Value));
        if (!directory.Exists || directory.LinkTarget is not null)
        {
            return Fail<SnapshotSummary>(new Diagnostic(DiagnosticCode.SnapshotNotFound, $"Snapshot {snapshotId} was not found in workspace {workspaceId}."));
        }

        var integrity = new Diagnostic(DiagnosticCode.IntegrityFailure, $"Snapshot {snapshotId} is incomplete or its metadata failed verification.");
        try
        {
            var markerPath = Path.Combine(directory.FullName, MarkerFile);
            var metadataPath = Path.Combine(directory.FullName, MetadataFile);
            if (new FileInfo(markerPath).LinkTarget is not null || new FileInfo(metadataPath).LinkTarget is not null
                || !File.Exists(markerPath) || !File.Exists(metadataPath))
            {
                return Fail<SnapshotSummary>(integrity);
            }

            var markerBytes = ReadBounded(markerPath, MaxMarkerBytes);
            var metadataBytes = ReadBounded(metadataPath, MaxMetadataBytes);
            if (markerBytes is null || metadataBytes is null)
            {
                return Fail<SnapshotSummary>(integrity);
            }

            var marker = JsonSerializer.Deserialize<CompletionMarkerDocument>(markerBytes, SnapshotJson.Options);
            var metadata = JsonSerializer.Deserialize<SnapshotMetadataDocument>(metadataBytes, SnapshotJson.Options);
            if (marker is null || metadata is null
                || marker.SchemaVersion != FormatVersions.SnapshotSchema || metadata.SchemaVersion != FormatVersions.SnapshotSchema
                || marker.WorkspaceId != workspaceId.Value || metadata.WorkspaceId != workspaceId.Value
                || marker.SnapshotId != snapshotId.Value || metadata.SnapshotId != snapshotId.Value
                || marker.MetadataSha256 != Utf8Text.Sha256Hex(metadataBytes)
                || metadata.MemberCount != metadata.Members.Count)
            {
                return Fail<SnapshotSummary>(integrity);
            }

            var members = new List<SnapshotMember>(metadata.Members.Count);
            for (var i = 0; i < metadata.Members.Count; i++)
            {
                var m = metadata.Members[i];
                if (m.Ordinal != i || !MemberIdentity.IsValidName(m.Library) || !MemberIdentity.IsValidName(m.SourceFile)
                    || !MemberIdentity.IsValidName(m.Member) || !Enum.IsDefined(m.SourceType))
                {
                    return Fail<SnapshotSummary>(integrity);
                }

                members.Add(new(m.Ordinal, new MemberIdentity(m.Library, m.SourceFile, m.Member, m.SourceType), m.SourcePath,
                    m.Ccsid, m.ByteLength, m.Sha256, m.LineCount));
            }

            var limits = new ImportLimits(metadata.Limits.MaxMemberBytes, metadata.Limits.MaxImportBytes, metadata.Limits.MaxMembers);
            return OperationResult<SnapshotSummary>.Success(new(workspaceId, snapshotId, metadata.AnalysisRunId, metadata.CreatedUtc,
                metadata.ImportFormatVersion, metadata.ParserVersion, limits, metadata.TotalBytes, members));
        }
        catch (JsonException)
        {
            return Fail<SnapshotSummary>(integrity);
        }
    }

    // ---- Storage layout -----------------------------------------------------------------------

    private string WorkspacePath(WorkspaceId workspaceId) => Path.Combine(_root, WorkspacesDirectory, workspaceId.Value);

    private string SnapshotsPath(WorkspaceId workspaceId) => Path.Combine(WorkspacePath(workspaceId), SnapshotsDirectory);

    private string StagingPath(WorkspaceId workspaceId) => Path.Combine(WorkspacePath(workspaceId), StagingDirectory);

    private static string MemberFileName(int ordinal) =>
        Path.Combine(MembersDirectory, ordinal.ToString("D6", CultureInfo.InvariantCulture) + ".src");

    /// <summary>
    /// Read-side storage check: the root must exist and be readable, and no directory on the
    /// workspace path may be a symbolic link. A missing workspace is simply empty.
    /// </summary>
    private Diagnostic? CheckStorage(WorkspaceId workspaceId)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return new Diagnostic(DiagnosticCode.StorageUnavailable, "Storage root does not exist or is not a directory.");
            }

            using (var probe = Directory.EnumerateFileSystemEntries(_root).GetEnumerator())
            {
                probe.MoveNext();
            }

            foreach (var path in new[] { Path.Combine(_root, WorkspacesDirectory), WorkspacePath(workspaceId), SnapshotsPath(workspaceId) })
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null || (!info.Exists && File.Exists(path)))
                {
                    return new Diagnostic(DiagnosticCode.StorageUnavailable, "Workspace storage contains an unexpected link or file.");
                }

                if (!PrivateStorage.IsPrivateOrMissing(path))
                {
                    return UnsafeStorage();
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageUnavailable(ex);
        }
    }

    private Diagnostic? PrepareWorkspace(WorkspaceId workspaceId)
    {
        try
        {
            PrivateStorage.CreateDirectory(_root, verifyExisting: false); // an existing root is the operator's choice, as on Unix
            foreach (var path in new[] { Path.Combine(_root, WorkspacesDirectory), WorkspacePath(workspaceId), SnapshotsPath(workspaceId), StagingPath(workspaceId) })
            {
                if (new DirectoryInfo(path).LinkTarget is not null)
                {
                    return new Diagnostic(DiagnosticCode.StorageUnavailable, "Workspace storage contains an unexpected link.");
                }

                PrivateStorage.CreateDirectory(path, verifyExisting: true);
            }

            return CheckStorage(workspaceId);
        }
        catch (UnsafeStorageException)
        {
            return UnsafeStorage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StorageUnavailable(ex);
        }
    }

    private static Diagnostic UnsafeStorage() =>
        new(DiagnosticCode.StorageUnavailable, "Workspace storage is not private to the current user; refusing to use it.");

    private static Diagnostic StorageUnavailable(Exception ex) =>
        new(DiagnosticCode.StorageUnavailable, $"Storage is not accessible ({ex.GetType().Name}).");

    private static void RequireWorkspace(WorkspaceId workspaceId)
    {
        if (workspaceId.Value is null)
        {
            throw new ArgumentException("An explicit, validated workspace ID is required.", nameof(workspaceId));
        }
    }

    // ---- Logging ------------------------------------------------------------------------------

    private OperationResult<T> Denied<T>(string eventName, WorkspaceId workspaceId, params Diagnostic[] diagnostics)
    {
        LogDiagnostics(eventName, workspaceId, null, diagnostics);
        return OperationResult<T>.Failure(diagnostics);
    }

    private void LogDiagnostics(string eventName, WorkspaceId workspaceId, string? runId, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Log(OperationLogLevel.Warning, eventName, ("workspaceId", workspaceId.Value), ("analysisRunId", runId),
                ("code", diagnostic.Code.ToString()), ("entryIndex", diagnostic.EntryIndex?.ToString(CultureInfo.InvariantCulture)),
                ("path", diagnostic.Path), ("message", diagnostic.Message));
        }
    }

    private void Log(OperationLogLevel level, string name, params (string Key, string? Value)[] fields) =>
        _log.Write(new OperationLogEvent(_time.GetUtcNow(), level, name, fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value)));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static OperationResult<T> Fail<T>(params Diagnostic[] diagnostics) => OperationResult<T>.Failure(diagnostics);

    /// <summary>Member reads against a snapshot verified once by <see cref="OpenSnapshot"/>; bytes are re-hashed on every read.</summary>
    private sealed class SnapshotReader(FileSystemSnapshotStore store, WorkspaceId workspaceId, SnapshotSummary snapshot) : ISnapshotReader
    {
        public SnapshotSummary Snapshot => snapshot;

        public OperationResult<MemberInspection> ReadMember(MemberIdentity member)
        {
            ArgumentNullException.ThrowIfNull(member);
            var loaded = store.LoadMemberBytes(workspaceId, snapshot, member);
            return loaded.Succeeded ? BuildInspection(loaded.Value, null) : Fail<MemberInspection>([.. loaded.Diagnostics]);
        }

        public OperationResult<SnapshotMember> VerifyMember(MemberIdentity member)
        {
            ArgumentNullException.ThrowIfNull(member);
            var loaded = store.LoadMemberBytes(workspaceId, snapshot, member);
            return loaded.Succeeded
                ? OperationResult<SnapshotMember>.Success(loaded.Value.Member)
                : Fail<SnapshotMember>([.. loaded.Diagnostics]);
        }
    }
}
