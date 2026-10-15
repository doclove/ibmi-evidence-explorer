using System.Diagnostics;
using System.Globalization;
using System.Text;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using Xunit.Abstractions;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// F1.5 resource-ceiling measurement (D-014): a synthetic corpus at the opt-in ceilings (about 510
/// MiB, 50,000 members, 500,000 facts and edges) is imported, indexed, and queried while peak
/// working set and managed heap are sampled. Skipped unless IBMI_EXPLORER_STRESS=1 because it writes
/// roughly 1.5 GB of temporary data; results are recorded in docs, not asserted as a benchmark.
/// </summary>
public sealed class StressMeasurementTests(ITestOutputHelper output) : IDisposable
{
    private const int Members = 50_000;
    private const int FieldsPerMember = 7; // member + record + 7 fields + 1 key edge = 10 items per member
    private const int TargetBytesPerMember = 10_700;

    private readonly IndexEnvironment _index = new();

    public void Dispose() => _index.Dispose();

    [SkippableFact]
    public void CeilingCorpusFitsWithinMeasuredMemory()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("IBMI_EXPLORER_STRESS") == "1", "Set IBMI_EXPLORER_STRESS=1 to run the F1.5 ceiling measurement.");
        var source = Generate();
        using var sampler = new PeakSampler();
        var clock = Stopwatch.StartNew();

        var importLimits = ImportLimits.Create(maxImportBytes: ImportLimits.UpperBoundImportBytes, maxMembers: Members).Value;
        var imported = _index.Env.Import("stress", source, importLimits);
        Assert.True(imported.Succeeded, string.Join("; ", imported.Diagnostics));
        var snapshot = imported.Value.Snapshot;
        var importSeconds = clock.Elapsed.TotalSeconds;
        var afterImport = sampler.Snapshot();

        clock.Restart();
        var limits = IndexLimits.Create(IndexLimits.UpperBoundTotalBytes, IndexLimits.UpperBoundMembers, IndexLimits.UpperBoundItems).Value;
        var built = _index.Service(limits).Build(TestEnvironment.Workspace("stress"), snapshot.SnapshotId);
        Assert.True(built.Succeeded, string.Join("; ", built.Diagnostics));
        var indexSeconds = clock.Elapsed.TotalSeconds;
        var afterIndex = sampler.Snapshot();

        clock.Restart();
        var search = _index.Service(limits).Search(TestEnvironment.Workspace("stress"), snapshot.SnapshotId, "FLD3");
        Assert.True(search.Succeeded, string.Join("; ", search.Diagnostics));
        var searchSeconds = clock.Elapsed.TotalSeconds;
        clock.Restart();
        var dependencies = _index.Service(limits).Dependencies(TestEnvironment.Workspace("stress"), snapshot.SnapshotId, snapshot.Members[^1].Identity);
        Assert.True(dependencies.Succeeded);
        var dependencySeconds = clock.Elapsed.TotalSeconds;
        var afterQueries = sampler.Snapshot();

        var run = _index.RunDirectory("stress", built.Value.Index);
        var counts = built.Value.Index.Counts;
        string Mib(long bytes) => (bytes / (double)ImportLimits.MiB).ToString("F1", CultureInfo.InvariantCulture);
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"os {Environment.OSVersion} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}")
            .AppendLine(CultureInfo.InvariantCulture, $"corpus: {snapshot.Members.Count} members, {Mib(snapshot.TotalBytes)} MiB, {snapshot.Members.Sum(m => (long)m.LineCount)} lines")
            .AppendLine(CultureInfo.InvariantCulture, $"index: status {built.Value.Index.Status}, artifacts {counts.Artifacts}, edges {counts.Edges}, facts and edges {counts.Artifacts + counts.Edges}")
            .AppendLine(CultureInfo.InvariantCulture, $"facts.json {Mib(new FileInfo(Path.Combine(run, "facts.json")).Length)} MiB, index.json {new FileInfo(Path.Combine(run, "index.json")).Length} bytes")
            .AppendLine(CultureInfo.InvariantCulture, $"time: import {importSeconds:F1} s, index {indexSeconds:F1} s, search {searchSeconds:F1} s, dependencies {dependencySeconds:F1} s")
            .AppendLine(CultureInfo.InvariantCulture, $"peak working set: after import {Mib(afterImport.WorkingSet)} MiB, after index {Mib(afterIndex.WorkingSet)} MiB, after queries {Mib(afterQueries.WorkingSet)} MiB")
            .AppendLine(CultureInfo.InvariantCulture, $"peak managed heap: after import {Mib(afterImport.Heap)} MiB, after index {Mib(afterIndex.Heap)} MiB, after queries {Mib(afterQueries.Heap)} MiB")
            .ToString();
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("IBMI_EXPLORER_STRESS_REPORT") is { Length: > 0 } path)
        {
            File.WriteAllText(path, report);
        }

        Assert.Equal(IndexStatus.Complete, built.Value.Index.Status);
    }

    /// <summary>DDS physical files: one record, seven fields, one key, padded with comment lines to the target size.</summary>
    private string Generate()
    {
        var root = Path.Combine(_index.Env.Root, "sources", "stress");
        Directory.CreateDirectory(Path.Combine(root, "m"));
        var manifest = new StringBuilder("{ \"formatVersion\": 1, \"members\": [\n");
        var comment = "     A* " + new string('c', 72) + "\n";
        for (var i = 0; i < Members; i++)
        {
            var text = new StringBuilder();
            text.Append(Parse.Dds('R', "REC")).Append('\n');
            for (var f = 1; f <= FieldsPerMember; f++)
            {
                text.Append(Parse.Dds(' ', $"FLD{f}", "9", 'P', "0")).Append('\n');
            }

            text.Append(Parse.Dds('K', "FLD1")).Append('\n');
            while (text.Length + comment.Length <= TargetBytesPerMember)
            {
                text.Append(comment);
            }

            var name = $"M{i:D5}";
            File.WriteAllText(Path.Combine(root, "m", name + ".pf"), text.ToString(), new UTF8Encoding(false));
            manifest.Append(CultureInfo.InvariantCulture,
                $"{(i == 0 ? string.Empty : ",\n")}{{ \"path\": \"m/{name}.pf\", \"library\": \"STRESS{i % 50:D2}\", \"sourceFile\": \"QDDSSRC\", \"member\": \"{name}\", \"sourceType\": \"DDS\", \"ccsid\": 1208 }}");
        }

        manifest.Append("\n] }\n");
        TestEnvironment.WriteManifest(root, manifest.ToString());
        return root;
    }

    /// <summary>Samples working set and managed heap every 25 ms and keeps the peaks.</summary>
    private sealed class PeakSampler : IDisposable
    {
        private readonly Timer _timer;
        private long _workingSet;
        private long _heap;

        public PeakSampler() => _timer = new Timer(_ => Sample(), null, 0, 25);

        public (long WorkingSet, long Heap) Snapshot()
        {
            Sample();
            return (Interlocked.Read(ref _workingSet), Interlocked.Read(ref _heap));
        }

        public void Dispose() => _timer.Dispose();

        private void Sample()
        {
            Max(ref _workingSet, Environment.WorkingSet);
            Max(ref _heap, GC.GetTotalMemory(forceFullCollection: false));
        }

        private static void Max(ref long target, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
