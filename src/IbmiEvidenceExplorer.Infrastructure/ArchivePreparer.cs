using System.Globalization;
using System.Text.Json;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Parsing;
using IbmiEvidenceExplorer.Core.Preparation;

namespace IbmiEvidenceExplorer.Infrastructure;

/// <summary>
/// F1.5 source preparation (D-014). <see cref="Scan"/> inventories an explicitly selected archive
/// root into a private, editable plan; <see cref="Build"/> converts the confirmed plan entries into
/// a new, private, F0-compatible UTF-8 export with <c>manifest.json</c> and a private
/// <c>preparation-report.json</c>, published atomically. Neither step modifies the input tree.
/// Directory names are discovered with link-refusing enumeration (links and reparse points are
/// recorded and never descended); every byte is read through <see cref="SecureSourceTree"/>, so a
/// path replaced by a link after discovery is still refused. Logs and console summaries carry
/// counts and codes only, never source text, paths, or member identities.
/// </summary>
public sealed class ArchivePreparer
{
    internal const string MembersDirectory = "members";

    private readonly IOperationLog _log;
    private readonly TimeProvider _time;
    private readonly PreparationLimits _limits;

    public ArchivePreparer(IOperationLog? log = null, TimeProvider? time = null, PreparationLimits? limits = null)
    {
        _log = log ?? NullOperationLog.Instance;
        _time = time ?? TimeProvider.System;
        _limits = limits ?? PreparationLimits.Default;
    }

    /// <summary>Test hook: invoked at each build stage so tests can simulate failure or interruption.</summary>
    internal Action<PreparationStage>? FaultInjector { get; set; }

    // ---- Scan ---------------------------------------------------------------------------------

    public OperationResult<PreparationScanOutcome> Scan(string sourceRoot, string planPath, ScanDefaults defaults, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
        ArgumentNullException.ThrowIfNull(defaults);
        OperationResult<PreparationScanOutcome> result;
        try
        {
            result = ScanCore(sourceRoot, planPath, defaults, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = Fail<PreparationScanOutcome>(new Diagnostic(DiagnosticCode.PreparationCancelled, "Scan was cancelled; no plan was written."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = Fail<PreparationScanOutcome>(new Diagnostic(DiagnosticCode.SourceUnreadable, $"Scan failed ({ex.GetType().Name}); no plan was written."));
        }

        if (result.Succeeded)
        {
            var c = result.Value.Counts;
            Log(OperationLogLevel.Information, "prepare.scan.completed", ("discovered", Text(c.Discovered)), ("included", Text(c.Included)),
                ("skipped", Text(c.Skipped)), ("unconfirmed", Text(c.Unconfirmed)), ("projectedBytes", Text(c.ProjectedBytes)));
        }
        else
        {
            LogCodes("prepare.scan.rejected", result.Diagnostics);
        }

        return result;
    }

    private OperationResult<PreparationScanOutcome> ScanCore(string sourceRoot, string planPath, ScanDefaults defaults, CancellationToken cancellationToken)
    {
        if (ValidateDefaults(defaults) is { } invalidDefaults)
        {
            return Fail<PreparationScanOutcome>(invalidDefaults);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var plan = Path.GetFullPath(planPath);
        if (CheckNewPath(plan, root, "Plan file") is { } planProblem)
        {
            return Fail<PreparationScanOutcome>(planProblem);
        }

        if (OpenRoot(root, out var tree) is { } rootProblem)
        {
            return Fail<PreparationScanOutcome>(rootProblem);
        }

        using (tree)
        {
            var discovered = new List<(string Path, PreparationReason? Reason)>();
            if (!Discover(new DirectoryInfo(root), string.Empty, 1, discovered, cancellationToken))
            {
                return Fail<PreparationScanOutcome>(new Diagnostic(DiagnosticCode.TooManyEntries,
                    $"Archive has more than {_limits.MaxEntries} entries; select a narrower source root."));
            }

            discovered.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var entries = new List<PlanEntry>(discovered.Count);
            long includedBytes = 0;
            foreach (var (path, discoveryReason) in discovered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = ScanEntry(tree!, path, discoveryReason, defaults, ref includedBytes);
                entries.Add(entry);
            }

            // Never pick one of several inputs with the same identity: skip them all for the operator to resolve.
            var duplicates = entries.Where(e => e.State == PlanEntryState.Included).GroupBy(e => e.Identity!).Where(g => g.Count() > 1)
                .SelectMany(g => g).ToHashSet();
            var final = entries.Select(e => duplicates.Contains(e) ? Skip(e, PreparationReason.DuplicateIdentity) : e).ToList();

            var document = new PreparationPlan
            {
                FormatVersion = PreparationFormats.PlanFormatVersion,
                Layout = PreparationFormats.ArchivedLibraryTree,
                TransformVersion = PreparationFormats.TransformVersion,
                Entries = final,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, PreparationJson.Options);
            cancellationToken.ThrowIfCancellationRequested();
            if (CheckResolved(plan, root, "Plan file") is { } moved)
            {
                return Fail<PreparationScanOutcome>(moved);
            }

            PrivateStorage.WriteNewPrivateFile(plan, bytes);
            return OperationResult<PreparationScanOutcome>.Success(new(plan, Utf8Text.Sha256Hex(bytes), PreparationCounts.Of(final)));
        }
    }

    /// <summary>
    /// Walks directory names without following links. Depth 1-3 directories (LIB, SRCF, TYPE) are
    /// descended; deeper directories, links, and reparse points are recorded and not entered.
    /// Returns false when the entry bound is exceeded.
    /// </summary>
    private bool Discover(DirectoryInfo directory, string prefix, int depth, List<(string, PreparationReason?)> found, CancellationToken cancellationToken)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (prefix.Length == 0)
            {
                throw;
            }

            found.Add((prefix.TrimEnd('/'), PreparationReason.Unreadable));
            return found.Count <= _limits.MaxEntries;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = prefix + child.Name;
            var isLink = child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isLink)
            {
                found.Add((path, PreparationReason.SymbolicLink));
            }
            else if (child is DirectoryInfo subdirectory)
            {
                if (depth > ArchivePaths.MaxDirectoryDepth)
                {
                    found.Add((path, PreparationReason.UnknownLayout));
                }
                else if (!Discover(subdirectory, path + "/", depth + 1, found, cancellationToken))
                {
                    return false;
                }
            }
            else
            {
                found.Add((path, null));
            }

            if (found.Count > _limits.MaxEntries)
            {
                return false;
            }
        }

        return true;
    }

    private PlanEntry ScanEntry(SecureSourceTree tree, string path, PreparationReason? discoveryReason, ScanDefaults defaults, ref long includedBytes)
    {
        var (identity, pathReason) = ManifestParser.ValidateRelativePath(path, 0) is null ? ArchivePaths.Derive(path) : (null, PreparationReason.InvalidName);
        var entry = new PlanEntry
        {
            Path = path,
            Library = identity?.Library,
            SourceFile = identity?.SourceFile,
            Member = identity?.Member,
            SourceType = identity?.SourceType,
            State = PlanEntryState.Skipped,
            Ccsid = defaults.Ccsid ?? (identity is not null && ArchivePaths.SuggestsUtf8(path) ? PreparationFormats.Utf8Ccsid : null),
            RecordLayout = defaults.RecordLayout,
            RecordWidth = defaults.RecordWidth,
            Confirmed = false,
        };

        if ((discoveryReason ?? pathReason) is { } early)
        {
            return Skip(entry, early);
        }

        var status = tree.TryOpenFile(path, out var handle);
        if (status != SourceOpenStatus.Opened)
        {
            return Skip(entry, OpenReason(status));
        }

        byte[]? bytes;
        long length;
        using (handle)
        {
            length = RandomAccess.GetLength(handle!);
            bytes = length > _limits.MaxMemberBytes ? null : SecureSourceTree.ReadBounded(handle!, _limits.MaxMemberBytes);
        }

        if (bytes is null)
        {
            return Skip(entry, PreparationReason.MemberTooLarge) with { InputBytes = length };
        }

        var observed = entry with
        {
            InputBytes = bytes.Length,
            InputSha256 = Utf8Text.Sha256Hex(bytes),
            Observations = new PlanObservations
            {
                Utf8Bom = bytes.AsSpan().StartsWith(Utf8Text.Bom),
                ValidUtf8 = Utf8Text.TryValidate(bytes, out _),
                LineSeparators = bytes.AsSpan().IndexOfAny((byte)'\n', (byte)'\r') >= 0,
            },
        };

        if (includedBytes + bytes.Length > _limits.MaxTotalBytes)
        {
            return Skip(observed, PreparationReason.TotalLimitExceeded);
        }

        includedBytes += bytes.Length;
        return observed with { State = PlanEntryState.Included, Reason = null };
    }

    // ---- Build --------------------------------------------------------------------------------

    public OperationResult<PreparationBuildOutcome> Build(string sourceRoot, string planPath, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var runId = AnalysisRunId.New();
        OperationResult<PreparationBuildOutcome> result;
        try
        {
            result = BuildCore(sourceRoot, planPath, outputPath, runId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.SourceUnreadable,
                $"Preparation failed ({ex.GetType().Name}); no export was published."));
        }

        if (result.Succeeded)
        {
            var o = result.Value;
            Log(OperationLogLevel.Information, "prepare.build.published", ("preparationRunId", runId), ("status", Labels.Of(o.Status)),
                ("planEntries", Text(o.PlanEntries)), ("prepared", Text(o.Prepared)), ("rejected", Text(o.Rejected)), ("skipped", Text(o.Skipped)),
                ("preparedBytes", Text(o.PreparedBytes)));
            foreach (var (reason, count) in o.RejectedByReason)
            {
                Log(OperationLogLevel.Warning, "prepare.build.member_rejected", ("preparationRunId", runId), ("reason", reason), ("count", Text(count)));
            }
        }
        else
        {
            LogCodes("prepare.build.rejected", result.Diagnostics);
        }

        return result;
    }

    private OperationResult<PreparationBuildOutcome> BuildCore(string sourceRoot, string planPath, string outputPath, string runId, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        if (CheckNewPath(output, root, "Output directory") is { } outputProblem)
        {
            return Fail<PreparationBuildOutcome>(outputProblem);
        }

        var planBytes = ReadPlan(planPath);
        if (!planBytes.Succeeded)
        {
            return Fail<PreparationBuildOutcome>([.. planBytes.Diagnostics]);
        }

        var parsed = PreparationJson.ParsePlan(planBytes.Value);
        if (!parsed.Succeeded)
        {
            return Fail<PreparationBuildOutcome>([.. parsed.Diagnostics]);
        }

        var plan = parsed.Value;
        if (plan.Entries.Count > _limits.MaxEntries)
        {
            return Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.TooManyEntries, $"Plan has more than {_limits.MaxEntries} entries."));
        }

        if (OpenRoot(root, out var tree) is { } rootProblem)
        {
            return Fail<PreparationBuildOutcome>(rootProblem);
        }

        using (tree)
        {
            var staging = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.prepare-staging-{runId}");
            var published = false;
            try
            {
                if (CheckResolved(staging, root, "Output directory") is { } beforeStaging)
                {
                    return Fail<PreparationBuildOutcome>(beforeStaging);
                }

                PrivateStorage.CreateDirectory(staging, verifyExisting: true);
                if (CheckResolved(staging, root, "Output directory") is { } afterStaging)
                {
                    return Fail<PreparationBuildOutcome>(afterStaging); // the empty staging directory is removed in finally
                }

                PrivateStorage.CreateDirectory(Path.Combine(staging, MembersDirectory), verifyExisting: true);
                var outcome = Convert(tree!, plan, staging, cancellationToken);
                var prepared = outcome.Where(o => o.Outcome == ReportOutcome.Prepared).ToList();
                var summary = Summarize(output, runId, plan, outcome);
                if (prepared.Count == 0)
                {
                    return Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.NothingToPrepare,
                        $"No plan entry could be prepared ({summary.Included} included, {summary.Rejected} rejected, {summary.Skipped} skipped); " +
                        "no export was published. Rejection counts by reason: " + Describe(summary.RejectedByReason)));
                }

                FaultInjector?.Invoke(PreparationStage.MembersWritten);
                var manifest = new PreparedManifestDocument
                {
                    FormatVersion = FormatVersions.ManifestFormat,
                    Members = prepared.Select(p => new PreparedManifestEntry
                    {
                        Path = p.PreparedPath!,
                        Library = p.Library!,
                        SourceFile = p.SourceFile!,
                        Member = p.Member!,
                        SourceType = p.SourceType!.Value,
                        Ccsid = PreparationFormats.Utf8Ccsid,
                    }).ToList(),
                };
                WriteDurably(Path.Combine(staging, ManifestParser.FileName), JsonSerializer.SerializeToUtf8Bytes(manifest, PreparationJson.Options));
                FaultInjector?.Invoke(PreparationStage.ManifestWritten);
                cancellationToken.ThrowIfCancellationRequested();

                var report = new PreparationReportDocument
                {
                    FormatVersion = PreparationFormats.ReportFormatVersion,
                    TransformVersion = PreparationFormats.TransformVersion,
                    Layout = plan.Layout,
                    PreparationRunId = runId,
                    CreatedUtc = _time.GetUtcNow(),
                    PlanSha256 = Utf8Text.Sha256Hex(planBytes.Value),
                    Status = summary.Status,
                    LineMapping = "Prepared line N (F0 line numbering) is original source record N of the same entry; records are lines for 'lines' and fixed-width records for 'fixed-source-data'.",
                    Entries = outcome,
                };
                WriteDurably(Path.Combine(staging, PreparationFormats.PlanReportFileName), JsonSerializer.SerializeToUtf8Bytes(report, PreparationJson.Options));
                FaultInjector?.Invoke(PreparationStage.ReportWritten);

                // Verify what was written before publishing it.
                foreach (var member in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stored = File.ReadAllBytes(Path.Combine(staging, member.PreparedPath!));
                    if (Utf8Text.Sha256Hex(stored) != member.PreparedSha256)
                    {
                        return Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.PublishFailed, "A staged member did not verify; no export was published."));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    PrivateStorage.MakeReadOnly(file);
                }

                if (CheckResolved(output, root, "Output directory") is { } beforePublish)
                {
                    return Fail<PreparationBuildOutcome>(beforePublish);
                }

                Directory.Move(staging, output);
                published = true;
                return OperationResult<PreparationBuildOutcome>.Success(summary);
            }
            catch (OperationCanceledException)
            {
                return Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.PreparationCancelled, "Preparation was cancelled; no export was published."));
            }
            catch (UnsafeStorageException)
            {
                return Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.StorageUnavailable, "Output staging is not private to the current user; no export was published."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail<PreparationBuildOutcome>(new Diagnostic(DiagnosticCode.PublishFailed,
                    $"Export could not be staged or published ({ex.GetType().Name}); no export was published."));
            }
            finally
            {
                if (!published)
                {
                    RemoveStaging(staging, runId);
                }
            }
        }
    }

    /// <summary>Converts every included entry, in plan order; per-entry failures never stop the others.</summary>
    private List<ReportEntryDocument> Convert(SecureSourceTree tree, PreparationPlan plan, string staging, CancellationToken cancellationToken)
    {
        // Duplicates are judged among included entries whose path and identity are themselves valid,
        // so a malformed hand-written entry cannot knock out the genuine member it imitates.
        var duplicates = plan.Entries.Where(e => e.State == PlanEntryState.Included && HasValidIdentity(e))
            .GroupBy(e => e.Identity!).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        var report = new List<ReportEntryDocument>(plan.Entries.Count);
        long total = 0;
        var ordinal = 0;
        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = new ReportEntryDocument
            {
                OriginalPath = entry.Path,
                Outcome = ReportOutcome.Skipped,
                Reason = entry.Reason,
                Library = entry.Library,
                SourceFile = entry.SourceFile,
                Member = entry.Member,
                SourceType = entry.SourceType,
                Ccsid = entry.Ccsid,
                RecordLayout = entry.RecordLayout,
                RecordWidth = entry.RecordWidth,
            };

            if (entry.State == PlanEntryState.Skipped)
            {
                report.Add(baseline);
                continue;
            }

            if (Precheck(entry, duplicates) is { } planReason)
            {
                report.Add(Rejected(baseline, planReason));
                continue;
            }

            var status = tree.TryOpenFile(entry.Path, out var handle);
            if (status != SourceOpenStatus.Opened)
            {
                report.Add(Rejected(baseline, OpenReason(status)));
                continue;
            }

            byte[]? input;
            using (handle)
            {
                input = SecureSourceTree.ReadBounded(handle!, _limits.MaxMemberBytes);
            }

            if (input is null)
            {
                report.Add(Rejected(baseline, PreparationReason.MemberTooLarge));
                continue;
            }

            var inputSha = Utf8Text.Sha256Hex(input);
            var withInput = Copy(baseline, input.Length, inputSha);
            if (inputSha != entry.InputSha256 || input.Length != entry.InputBytes)
            {
                report.Add(Rejected(withInput, PreparationReason.InputChanged));
                continue;
            }

            var converted = SourceTranscoder.Transcode(input, entry.Ccsid!.Value, entry.RecordLayout!.Value, entry.RecordWidth);
            if (!converted.Succeeded)
            {
                report.Add(Rejected(withInput, converted.Reason!.Value, converted.ReasonOffset));
                continue;
            }

            var bytes = converted.Bytes!;
            if (bytes.Length > _limits.MaxMemberBytes || total + bytes.Length > _limits.MaxTotalBytes)
            {
                report.Add(Rejected(withInput, bytes.Length > _limits.MaxMemberBytes ? PreparationReason.MemberTooLarge : PreparationReason.TotalLimitExceeded));
                continue;
            }

            var preparedPath = $"{MembersDirectory}/{ordinal.ToString("D6", CultureInfo.InvariantCulture)}.src";
            WriteDurably(Path.Combine(staging, MembersDirectory, $"{ordinal.ToString("D6", CultureInfo.InvariantCulture)}.src"), bytes);
            total += bytes.Length;
            report.Add(new ReportEntryDocument
            {
                OriginalPath = withInput.OriginalPath,
                Outcome = ReportOutcome.Prepared,
                Library = entry.Library,
                SourceFile = entry.SourceFile,
                Member = entry.Member,
                SourceType = entry.SourceType,
                Ccsid = entry.Ccsid,
                RecordLayout = entry.RecordLayout,
                RecordWidth = entry.RecordWidth,
                OriginalBytes = input.Length,
                OriginalSha256 = inputSha,
                ManifestIndex = ordinal,
                PreparedPath = preparedPath,
                PreparedBytes = bytes.Length,
                PreparedSha256 = Utf8Text.Sha256Hex(bytes),
                Transform = converted.Transform,
                RecordCount = converted.RecordCount,
            });
            ordinal++;
        }

        return report;
    }

    /// <summary>Plan-level checks for one included entry, before any byte is read.</summary>
    private static PreparationReason? Precheck(PlanEntry entry, HashSet<PlanEntry> duplicates)
    {
        if (ManifestParser.ValidateRelativePath(entry.Path, 0) is not null)
        {
            return PreparationReason.PathEscape;
        }

        if (!HasValidIdentity(entry))
        {
            return PreparationReason.IdentityMismatch;
        }

        if (entry.Reason is not null)
        {
            return PreparationReason.ContradictoryChoice; // an included entry cannot also carry a skip reason
        }

        if (duplicates.Contains(entry))
        {
            return PreparationReason.DuplicateIdentity;
        }

        if (entry.Ccsid is null || entry.RecordLayout is null || entry.InputSha256 is null || entry.InputBytes is null)
        {
            return PreparationReason.MissingChoice;
        }

        return entry.Confirmed ? null : PreparationReason.Unconfirmed;
    }

    /// <summary>The path is a plain relative path and the entry's identity is exactly the one derived from it.</summary>
    private static bool HasValidIdentity(PlanEntry entry) =>
        ManifestParser.ValidateRelativePath(entry.Path, 0) is null && ArchivePaths.Derive(entry.Path).Identity is { } derived && entry.Identity == derived;

    private static PreparationBuildOutcome Summarize(string output, string runId, PreparationPlan plan, List<ReportEntryDocument> report)
    {
        var prepared = report.Where(r => r.Outcome == ReportOutcome.Prepared).ToList();
        var rejected = report.Where(r => r.Outcome == ReportOutcome.Rejected).ToList();
        var skipped = report.Where(r => r.Outcome == ReportOutcome.Skipped).ToList();
        return new PreparationBuildOutcome(
            output,
            runId,
            rejected.Count == 0 ? PreparationStatus.Complete : PreparationStatus.Partial,
            plan.Entries.Count,
            prepared.Count + rejected.Count,
            prepared.Count,
            rejected.Count,
            skipped.Count,
            prepared.Sum(p => p.OriginalBytes ?? 0),
            prepared.Sum(p => p.PreparedBytes ?? 0),
            PreparationCounts.Group(prepared.Select(p => p.SourceType!.Value.ToString())),
            PreparationCounts.Group(rejected.Select(r => Labels.Of(r.Reason!.Value))),
            PreparationCounts.Group(skipped.Select(s => s.Reason is { } reason ? Labels.Of(reason) : "unspecified")));
    }

    // ---- Shared -------------------------------------------------------------------------------

    private static Diagnostic? ValidateDefaults(ScanDefaults defaults)
    {
        if (defaults.Ccsid is { } ccsid && !PreparationFormats.SupportedCcsids.Contains(ccsid))
        {
            return new(DiagnosticCode.UnsupportedCcsid, "CCSID must be one of 1208, 37, 1140; no other CCSID is converted.");
        }

        if (defaults.RecordLayout == RecordLayout.FixedSourceData ? defaults.RecordWidth != PreparationFormats.SourceDataRecordWidth : defaults.RecordWidth is not null)
        {
            return new(DiagnosticCode.InvalidArgument, "fixed-source-data requires --record-width 80; --record-width applies only to fixed-source-data.");
        }

        return defaults.RecordLayout == RecordLayout.RawSourceRecord
            ? new(DiagnosticCode.InvalidArgument, "raw-source-record is not supported in F1.5 and cannot be a scan default.")
            : null;
    }

    /// <summary>
    /// A new plan or output path: absent (not even a link), with an existing parent, and outside the
    /// source root both as written and with every link or junction in its parent and in the root
    /// resolved, so a linked parent cannot place output inside the input tree.
    /// </summary>
    private static Diagnostic? CheckNewPath(string path, string root, string what)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            return InsideRoot(what);
        }

        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            return new(DiagnosticCode.PreparationOutputExists, $"{what} already exists; preparation never overwrites. Choose a new path.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return new(DiagnosticCode.InvalidArgument, $"{what} parent directory does not exist.");
        }

        return CheckResolved(path, root, what);
    }

    /// <summary>Re-run before each write that depends on the destination: a parent swapped for a link since the first check is refused.</summary>
    private static Diagnostic? CheckResolved(string path, string root, string what) => PathContainment.IsWithin(path, root) switch
    {
        false => null,
        true => InsideRoot(what),
        null => Directory.Exists(root)
            ? new(DiagnosticCode.InvalidArgument, $"{what} parent directory could not be resolved through its links; no output was written.")
            : null, // a missing root is reported by the source-root check
    };

    private static Diagnostic InsideRoot(string what) =>
        new(DiagnosticCode.InvalidArgument, $"{what} must be outside the source root, including through links or junctions; preparation never writes into the input tree.");

    private static Diagnostic? OpenRoot(string root, out SecureSourceTree? tree)
    {
        var status = SecureSourceTree.TryOpen(root, out tree);
        return status switch
        {
            SourceOpenStatus.Opened => null,
            SourceOpenStatus.SymbolicLink => new Diagnostic(DiagnosticCode.SymlinkRejected, "Source root must not be a symbolic link, junction, or other reparse point."),
            SourceOpenStatus.AccessDenied => new Diagnostic(DiagnosticCode.SourceUnreadable, "Source root is not readable."),
            SourceOpenStatus.UnsupportedPlatform => new Diagnostic(DiagnosticCode.SourceUnreadable,
                "Secure (no-follow) source reading is supported only on Windows, and on macOS and Linux (x64, arm64)."),
            _ => new Diagnostic(DiagnosticCode.SourceRootNotFound, "Source root does not exist or is not a directory."),
        };
    }

    private static OperationResult<byte[]> ReadPlan(string planPath)
    {
        var path = Path.GetFullPath(planPath);
        if (new FileInfo(path).LinkTarget is not null || !File.Exists(path))
        {
            return OperationResult<byte[]>.Failure(new Diagnostic(DiagnosticCode.PreparationPlanInvalid, "Plan file does not exist or is a link."));
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > PreparationLimits.MaxPlanBytes)
        {
            return OperationResult<byte[]>.Failure(new Diagnostic(DiagnosticCode.PreparationPlanInvalid, $"Plan file exceeds {PreparationLimits.MaxPlanBytes} bytes."));
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return OperationResult<byte[]>.Success(bytes);
    }

    private static PreparationReason OpenReason(SourceOpenStatus status) => status switch
    {
        SourceOpenStatus.SymbolicLink => PreparationReason.SymbolicLink,
        SourceOpenStatus.NotRegularFile => PreparationReason.NotRegularFile,
        SourceOpenStatus.AccessDenied or SourceOpenStatus.UnsupportedPlatform => PreparationReason.Unreadable,
        _ => PreparationReason.SourceFileNotFound,
    };

    private static PlanEntry Skip(PlanEntry entry, PreparationReason reason) => entry with { State = PlanEntryState.Skipped, Reason = reason };

    private static ReportEntryDocument Rejected(ReportEntryDocument entry, PreparationReason reason, long? offset = null) =>
        Copy(entry, entry.OriginalBytes, entry.OriginalSha256, ReportOutcome.Rejected, reason, offset);

    private static ReportEntryDocument Copy(ReportEntryDocument entry, long? bytes, string? sha256, ReportOutcome? outcome = null,
        PreparationReason? reason = null, long? offset = null) => new()
        {
            OriginalPath = entry.OriginalPath,
            Outcome = outcome ?? entry.Outcome,
            Reason = reason ?? entry.Reason,
            ReasonOffset = offset ?? entry.ReasonOffset,
            Library = entry.Library,
            SourceFile = entry.SourceFile,
            Member = entry.Member,
            SourceType = entry.SourceType,
            Ccsid = entry.Ccsid,
            RecordLayout = entry.RecordLayout,
            RecordWidth = entry.RecordWidth,
            OriginalBytes = bytes,
            OriginalSha256 = sha256,
        };

    private static string Describe(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"));

    private static void WriteDurably(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void RemoveStaging(string staging, string runId)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(OperationLogLevel.Warning, "prepare.staging_cleanup_failed", ("preparationRunId", runId), ("exception", ex.GetType().Name));
        }
    }

    private void LogCodes(string name, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Log(OperationLogLevel.Warning, name, ("code", diagnostic.Code.ToString()));
        }
    }

    private void Log(OperationLogLevel level, string name, params (string Key, string? Value)[] fields) =>
        _log.Write(new OperationLogEvent(_time.GetUtcNow(), level, name, fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value)));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static OperationResult<T> Fail<T>(params Diagnostic[] diagnostics) => OperationResult<T>.Failure(diagnostics);
}
