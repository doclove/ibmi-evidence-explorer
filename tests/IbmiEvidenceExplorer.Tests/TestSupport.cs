using System.Text;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>An isolated temporary directory holding a storage root and source exports.</summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "ibmi-explorer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        StorageRoot = Path.Combine(Root, "storage");
        Log = new CapturingLog();
        Store = new FileSystemSnapshotStore(StorageRoot, Log);
    }

    public string Root { get; }

    public string StorageRoot { get; }

    public CapturingLog Log { get; }

    public FileSystemSnapshotStore Store { get; }

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic", name);

    public static WorkspaceId Workspace(string id) =>
        WorkspaceId.TryParse(id, out var workspace) ? workspace : throw new ArgumentException("Invalid test workspace.", nameof(id));

    /// <summary>Copies a committed fixture into a fresh, writable source directory.</summary>
    public string CopyFixture(string name)
    {
        var target = Path.Combine(Root, "sources", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        var source = FixturePath(name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    /// <summary>Creates a source export from (path, bytes) pairs with a generated manifest.</summary>
    public string CreateExport(params (string Path, string Member, string Type, byte[] Bytes)[] members)
    {
        var target = Path.Combine(Root, "sources", "generated-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        foreach (var member in members)
        {
            var file = Path.Combine(target, member.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, member.Bytes);
        }

        var entries = members.Select(m =>
            $$"""{ "path": "{{m.Path}}", "library": "TESTLIB", "sourceFile": "QSRC", "member": "{{m.Member}}", "sourceType": "{{m.Type}}", "ccsid": 1208 }""");
        WriteManifest(target, $$"""{ "formatVersion": 1, "members": [ {{string.Join(", ", entries)}} ] }""");
        return target;
    }

    public static void WriteManifest(string sourceRoot, string json) =>
        File.WriteAllText(Path.Combine(sourceRoot, ManifestParser.FileName), json, new UTF8Encoding(false));

    public OperationResult<ImportOutcome> Import(string workspace, string source, ImportLimits? limits = null, CancellationToken cancellationToken = default) =>
        Store.Import(new ImportRequest(Workspace(workspace), source, limits ?? ImportLimits.Default), cancellationToken);

    public string WorkspaceDirectory(string workspace) => Path.Combine(StorageRoot, "workspaces", workspace);

    public string StagingDirectory(string workspace) => Path.Combine(WorkspaceDirectory(workspace), "staging");

    public string SnapshotsDirectory(string workspace) => Path.Combine(WorkspaceDirectory(workspace), "snapshots");

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        if (OperatingSystem.IsWindows())
        {
            // Recursive Directory.Delete calls DeleteVolumeMountPoint on junctions, which needs
            // administrator rights; unlink every link first (removes the link, not its target).
            RemoveLinks(Root);

            // Published files carry the ReadOnly attribute, which blocks deletion on Windows.
            foreach (var file in Directory.EnumerateFiles(Root, "*", options))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
        }
        else
        {
            foreach (var directory in Directory.EnumerateDirectories(Root, "*", options).Prepend(Root))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(Root, recursive: true);
    }

    private static void RemoveLinks(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                RemoveLinks(child.FullName);
            }
        }
    }
}

public sealed class CapturingLog : IOperationLog
{
    private readonly List<OperationLogEvent> _events = [];

    public IReadOnlyList<OperationLogEvent> Events => _events;

    public void Write(OperationLogEvent logEvent) => _events.Add(logEvent);

    /// <summary>Every name and field value, flattened, for content assertions.</summary>
    public string AllText() =>
        string.Join("\n", _events.Select(e => e.Name + " " + string.Join(" ", e.Fields.Select(f => $"{f.Key}={f.Value}"))));
}

public static class Canaries
{
    public const string Prefix = "FIXTURE-CANARY";

    public static void AssertAbsent(string text) =>
        Assert.DoesNotContain(Prefix, text, StringComparison.Ordinal);
}
