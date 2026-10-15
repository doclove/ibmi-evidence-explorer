using System.Text;
using System.Text.Json.Nodes;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Indexing;
using IbmiEvidenceExplorer.Core.Preparation;
using IbmiEvidenceExplorer.Infrastructure;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1.5 criteria 1 and 2: archive preparation, record/CCSID conversion, and evidence mapping.</summary>
public sealed class PreparationTests : IDisposable
{
    private static readonly WorkspaceId Acme = TestEnvironment.Workspace("acme");

    private readonly IndexEnvironment _index = new();
    private readonly ArchiveFixture _archive;

    public PreparationTests() => _archive = new ArchiveFixture(_index.Env);

    public void Dispose() => _index.Dispose();

    // ---- Built-in EBCDIC tables -----------------------------------------------------------------

    [Theory]
    [InlineData(37)]
    [InlineData(1140)]
    public void BuiltInEbcdicTableMatchesThePlatformCodePageAndRejectsControls(int ccsid)
    {
        var platform = ArchiveFixture.Ebcdic(ccsid);
        for (var value = 0; value <= 0xFF; value++)
        {
            var mapped = SourceTranscoder.MapEbcdic((byte)value, ccsid);
            if (value is >= 0x40 and <= 0xFE)
            {
                Assert.Equal(platform.GetString([(byte)value]), mapped.ToString());
            }
            else
            {
                Assert.Null(mapped); // control code points (and 0xFF) have no source-data meaning; never replaced
            }
        }

        Assert.Equal(ccsid == 1140 ? '€' : '¤', SourceTranscoder.MapEbcdic(0x9F, ccsid));
    }

    // ---- Transcoding rules ----------------------------------------------------------------------

    [Fact]
    public void FixedUtf8RemovesTheBomKeepsTrailingSpacesAndSplitsByScalarValues()
    {
        var input = ArchiveFixture.Utf8Fixed(bom: true, "first é", "second €𝄞");
        var result = SourceTranscoder.Transcode(input, 1208, RecordLayout.FixedSourceData, 80);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(ArchiveFixture.Pad("first é") + "\n" + ArchiveFixture.Pad("second €𝄞"), Encoding.UTF8.GetString(result.Bytes!));
        Assert.Equal("utf8-fixed-80", result.Transform);
    }

    [Fact]
    public void LineDelimitedUtf8KeepsBytesExceptTheBom()
    {
        byte[] input = [.. Utf8Text.Bom, .. "A\r\nB\nC"u8.ToArray()];
        var result = SourceTranscoder.Transcode(input, 1208, RecordLayout.Lines, null);
        Assert.Equal("A\r\nB\nC"u8.ToArray(), result.Bytes);
        Assert.Equal(3, result.RecordCount);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1140)]
    public void FixedEbcdicSplitsExact80ByteRecords(int ccsid)
    {
        var input = ArchiveFixture.EbcdicFixed(ccsid, "CALL PGM(X) /* ¢ */", "ENDPGM");
        var result = SourceTranscoder.Transcode(input, ccsid, RecordLayout.FixedSourceData, 80);
        Assert.Equal(ArchiveFixture.Pad("CALL PGM(X) /* ¢ */") + "\n" + ArchiveFixture.Pad("ENDPGM"), Encoding.UTF8.GetString(result.Bytes!));
        Assert.Equal(2, result.RecordCount);
    }

    [Fact]
    public void EmptyFixedInputHasNoRecords()
    {
        var result = SourceTranscoder.Transcode([], 37, RecordLayout.FixedSourceData, 80);
        Assert.Equal((0, 0), (result.Bytes!.Length, result.RecordCount));
    }

    public static TheoryData<byte[], int, RecordLayout, int?, PreparationReason> Rejections() => new()
    {
        { [.. ArchiveFixture.Utf8Fixed(false, "ok"), .. "short"u8.ToArray()], 1208, RecordLayout.FixedSourceData, 80, PreparationReason.ShortFinalRecord },
        { ArchiveFixture.EbcdicFixed(37, "ok").Take(79).ToArray(), 37, RecordLayout.FixedSourceData, 80, PreparationReason.ShortFinalRecord },
        { Encoding.UTF8.GetBytes(ArchiveFixture.Pad("A").Remove(10, 1).Insert(10, "\n")), 1208, RecordLayout.FixedSourceData, 80, PreparationReason.EmbeddedLineSeparator },
        { Encoding.UTF8.GetBytes(ArchiveFixture.Pad("A").Remove(10, 1).Insert(10, "\r")), 1208, RecordLayout.FixedSourceData, 80, PreparationReason.EmbeddedLineSeparator },
        { [.. ArchiveFixture.EbcdicFixed(37, "A").Take(79), 0x25], 37, RecordLayout.FixedSourceData, 80, PreparationReason.EmbeddedLineSeparator },
        { [.. ArchiveFixture.EbcdicFixed(37, "A").Take(79), 0x05], 37, RecordLayout.FixedSourceData, 80, PreparationReason.UnmappableCharacter },
        { [.. ArchiveFixture.EbcdicFixed(1140, "A").Take(79), 0xFF], 1140, RecordLayout.FixedSourceData, 80, PreparationReason.UnmappableCharacter },
        { [0x41, 0xC3, 0x28], 1208, RecordLayout.Lines, null, PreparationReason.InvalidEncoding },
        { [0x41, 0xC3, 0x28], 1208, RecordLayout.FixedSourceData, 80, PreparationReason.InvalidEncoding },
        { new byte[92], 37, RecordLayout.RawSourceRecord, 92, PreparationReason.UnsupportedRecordLayout },
        { new byte[92], 1208, RecordLayout.FixedSourceData, 92, PreparationReason.UnsupportedRecordWidth },
        { new byte[80], 37, RecordLayout.Lines, null, PreparationReason.UnsupportedRecordLayout },
        { new byte[80], 1208, RecordLayout.Lines, 80, PreparationReason.UnsupportedRecordWidth },
        { new byte[80], 500, RecordLayout.FixedSourceData, 80, PreparationReason.UnsupportedCcsid },
        { new byte[80], 0, RecordLayout.FixedSourceData, 80, PreparationReason.UnsupportedCcsid },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void UnsupportedOrMalformedInputIsRejectedWithoutReplacement(byte[] input, int ccsid, RecordLayout layout, int? width, PreparationReason reason)
    {
        var result = SourceTranscoder.Transcode(input, ccsid, layout, width);
        Assert.False(result.Succeeded);
        Assert.Null(result.Bytes);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void FileLengthDivisibleBy80IsNeverTakenAsFixedWidth()
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Fixed(bom: true, "PGM", "ENDPGM"));
        var (_, plan) = _archive.Scan();
        var entry = ArchiveFixture.Entry(plan, "LIB/QSRC/A.clle");
        Assert.Null((string?)entry["recordLayout"]);
        Assert.Null((int?)entry["recordWidth"]);
        Assert.Null((int?)entry["ccsid"]); // no .utf8 suffix and no operator default: no CCSID either
        Assert.False((bool)entry["confirmed"]!);
    }

    // ---- Criterion 1: mixed archive end to end ---------------------------------------------------

    [Fact]
    public void MixedArchivePreparesImportsIndexesAndCitesTheCorrectRecord()
    {
        var rpg = _archive.Add("DEMOLIB/QRPGLESRC/CUSTINQ.rpgle.utf8", ArchiveFixture.Utf8Fixed(bom: true,
            "**free", "// Prepared inquiry. FIXTURE-CANARY-PREP", "ctl-opt dftactgrp(*no);", "dcl-f CUSTMAS keyed usage(*input);", "chain custId CUSTMAS;"));
        var cl = _archive.Add("DEMOLIB/QCLSRC/CUSTJOB.clle", ArchiveFixture.EbcdicFixed(37,
            "PGM", "  DCL VAR(&CUSTID) TYPE(*DEC) LEN(7 0)", "  CALL PGM(CUSTINQ) PARM(&CUSTID)", "ENDPGM"));
        var dds = _archive.Add("DEMOLIB/QDDSSRC/CUSTMAS.pf.utf8", ArchiveFixture.Utf8Lines(
            Parse.Dds('R', "CUSTREC"), Parse.Dds(' ', "CMID", "7", 'P', "0"), Parse.Dds('K', "CMID")));
        var sql = _archive.Add("DEMOLIB/QSQLSRC/CUSTVW.sql", ArchiveFixture.EbcdicFixed(1140,
            "-- Customer prices in € only", "CREATE VIEW CUSTLIST AS", "  SELECT CMID FROM CUSTMAS;"));
        var before = _archive.Hashes();

        var (preparer, plan) = _archive.Scan();
        ArchiveFixture.Choose(plan, "DEMOLIB/QRPGLESRC/CUSTINQ.rpgle.utf8", 1208, "fixed-source-data", 80);
        ArchiveFixture.Choose(plan, "DEMOLIB/QCLSRC/CUSTJOB.clle", 37, "fixed-source-data", 80);
        ArchiveFixture.Choose(plan, "DEMOLIB/QDDSSRC/CUSTMAS.pf.utf8", 1208, "lines");
        ArchiveFixture.Choose(plan, "DEMOLIB/QSQLSRC/CUSTVW.sql", 1140, "fixed-source-data", 80);
        var output = _archive.NewPath("prepared");
        var built = preparer.Build(_archive.Root, plan, output);
        Assert.True(built.Succeeded, string.Join("; ", built.Diagnostics));
        Assert.Equal((PreparationStatus.Complete, 4, 0), (built.Value.Status, built.Value.Prepared, built.Value.Rejected));

        // Unchanged F0 import and the F1.5 index.
        var snapshot = _index.Env.Import("acme", output).Value.Snapshot;
        Assert.Equal(IndexStatus.Complete, _index.BuildIndex("acme", snapshot.SnapshotId).Status);
        var service = _index.Service();

        // The SQL view definition cites prepared line 2, which the report maps to original 80-byte record 2.
        var view = Assert.Single(service.Search(Acme, snapshot.SnapshotId, "CUSTLIST").Value.Hits);
        Assert.Equal(2, view.Evidence.LineNumber);
        AssertRecordMapping(view.Evidence, "DEMOLIB/QSQLSRC/CUSTVW.sql", sql, output,
            original => ArchiveFixture.Ebcdic(1140).GetString(original.AsSpan(80, 80)));
        var euro = _index.Env.Store.Inspect(Acme, snapshot.SnapshotId, view.Member, new LineRange(1, 1)).Value.Lines[0].Text;
        Assert.Contains("€", euro, StringComparison.Ordinal);
        Assert.DoesNotContain("�", euro, StringComparison.Ordinal);

        // The CL call edge cites prepared line 3 = CCSID 37 record 3, and resolves to the RPG member.
        var call = Assert.Single(service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("DEMOLIB", "QCLSRC", "CUSTJOB", SourceType.CL)).Value.Edges);
        Assert.Equal((3, ResolutionStatus.Resolved), (call.ReferenceEvidence.LineNumber, call.Resolution));
        AssertRecordMapping(call.ReferenceEvidence, "DEMOLIB/QCLSRC/CUSTJOB.clle", cl, output,
            original => ArchiveFixture.Ebcdic(37).GetString(original.AsSpan(160, 80)));

        // The RPG file declaration cites prepared line 4 = UTF-8 record 4 (80 scalar values after the BOM).
        var rpgEdges = service.Dependencies(Acme, snapshot.SnapshotId, Members.Of("DEMOLIB", "QRPGLESRC", "CUSTINQ", SourceType.RPG)).Value.Edges;
        Assert.Equal([4, 5], rpgEdges.Select(e => e.ReferenceEvidence.LineNumber));
        Assert.All(rpgEdges, e => Assert.Equal(ResolutionStatus.Resolved, e.Resolution));
        AssertRecordMapping(rpgEdges[0].ReferenceEvidence, "DEMOLIB/QRPGLESRC/CUSTINQ.rpgle.utf8", rpg, output,
            original => string.Concat(Encoding.UTF8.GetString(original.AsSpan(3)).EnumerateRunes().Skip(240).Take(80)));

        // A line-delimited member keeps F0's line numbering: prepared line 2 is original line 2.
        var field = Assert.Single(service.Search(Acme, snapshot.SnapshotId, "CMID").Value.Hits);
        Assert.Equal(2, field.Evidence.LineNumber);
        AssertRecordMapping(field.Evidence, "DEMOLIB/QDDSSRC/CUSTMAS.pf.utf8", dds, output,
            original => Encoding.UTF8.GetString(original).Split('\n')[1]);

        // Input bytes are unchanged, and the manifest is plain F0 (CCSID 1208, no extensions).
        Assert.Equal(before, _archive.Hashes());
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        Assert.All(manifest["members"]!.AsArray(), m => Assert.Equal(1208, (int)m!["ccsid"]!));
    }

    private void AssertRecordMapping(EvidenceReference evidence, string originalPath, string originalFile, string output, Func<byte[], string> originalRecord)
    {
        var report = ArchiveFixture.ReportEntry(output, originalPath);
        var original = File.ReadAllBytes(originalFile);
        Assert.Equal("prepared", (string?)report["outcome"]);
        Assert.Equal(Utf8Text.Sha256Hex(original), (string?)report["originalSha256"]);
        Assert.Equal(evidence.MemberSha256, (string?)report["preparedSha256"]);
        Assert.True(evidence.LineNumber <= (int)report["recordCount"]!);

        // Prepared line N (resolved through F0) is exactly original record N.
        var resolved = _index.Env.Store.Resolve(Acme, evidence);
        Assert.True(resolved.Succeeded, string.Join("; ", resolved.Diagnostics));
        Assert.Equal(originalRecord(original), resolved.Value.Text);
    }

    [Fact]
    public void PreparationIsDeterministicApartFromRunMetadata()
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.EbcdicFixed(37, "PGM", "ENDPGM"));
        _archive.Add("LIB/QSRC/B.clle.utf8", ArchiveFixture.Utf8Lines("PGM", "ENDPGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(null, null, null));
        ArchiveFixture.Choose(plan, "LIB/QSRC/A.clle", 37, "fixed-source-data", 80);
        ArchiveFixture.Choose(plan, "LIB/QSRC/B.clle.utf8", 1208, "lines");
        var first = _archive.NewPath("out");
        var second = _archive.NewPath("out");
        Assert.True(preparer.Build(_archive.Root, plan, first).Succeeded);
        Assert.True(preparer.Build(_archive.Root, plan, second).Succeeded);

        foreach (var file in new[] { "manifest.json", "members/000000.src", "members/000001.src" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(first, file)), File.ReadAllBytes(Path.Combine(second, file)));
        }

        var (_, plan2) = _archive.Scan();
        Assert.Equal(ArchiveFixture.Entries(plan2).ToJsonString(), ArchiveFixture.Entries(_archive.Scan().Plan).ToJsonString());
    }

    // ---- Criterion 2: typed skips and rejections -------------------------------------------------

    [Fact]
    public void DiscoverySkipsUnknownLayoutsExtensionsNamesAndDuplicatesWithTypedReasons()
    {
        _archive.Add("GOODLIB/QSRC/OK.clle", ArchiveFixture.Utf8Lines("PGM", "ENDPGM"));
        _archive.Add("lowlib/qsrc/lower.clle", ArchiveFixture.Utf8Lines("PGM", "ENDPGM")); // folds to LOWLIB/QSRC/LOWER
        _archive.Add("DUPLIB/QSRC/DUP.clle", ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("DUPLIB/QSRC/CLLE/DUP.clle.utf8", ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("LIB/QSRC/README.txt", "notes"u8.ToArray());
        _archive.Add("LIB/QSRC/NOEXT", "x"u8.ToArray());
        _archive.Add("LIB/QSRC/1BAD.clle", ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("LIB/QSRCTOOLONGNAME/X.clle", ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("TOP.clle", ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("LIB/ONLYTWO.clle", ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("LIB/QSRC/T/DEEP/X.clle", ArchiveFixture.Utf8Lines("PGM"));
        var (_, plan) = _archive.Scan();

        string? Reason(string path) => (string?)ArchiveFixture.Entry(plan, path)["reason"];
        Assert.Equal("included", (string?)ArchiveFixture.Entry(plan, "GOODLIB/QSRC/OK.clle")["state"]);
        Assert.Equal("LOWER", (string?)ArchiveFixture.Entry(plan, "lowlib/qsrc/lower.clle")["member"]);
        Assert.Equal("duplicate-identity", Reason("DUPLIB/QSRC/DUP.clle"));
        Assert.Equal("duplicate-identity", Reason("DUPLIB/QSRC/CLLE/DUP.clle.utf8"));
        Assert.Equal("unknown-extension", Reason("LIB/QSRC/README.txt"));
        Assert.Equal("unknown-extension", Reason("LIB/QSRC/NOEXT"));
        Assert.Equal("invalid-name", Reason("LIB/QSRC/1BAD.clle"));
        Assert.Equal("invalid-name", Reason("LIB/QSRCTOOLONGNAME/X.clle"));
        Assert.Equal("unknown-layout", Reason("TOP.clle"));
        Assert.Equal("unknown-layout", Reason("LIB/ONLYTWO.clle"));
        Assert.Equal("unknown-layout", Reason("LIB/QSRC/T/DEEP"));
        Assert.Equal(1208, (int)ArchiveFixture.Entry(plan, "DUPLIB/QSRC/CLLE/DUP.clle.utf8")["ccsid"]!); // .utf8 suggests, never confirms
        Assert.All(ArchiveFixture.Entries(plan), e => Assert.False((bool)e!["confirmed"]!));
    }

    [Fact]
    public void BuildRejectsBadEntriesWithTypedReasonsAndStillPreparesValidSiblings()
    {
        var limits = new PreparationLimits(MaxMemberBytes: 4096, MaxTotalBytes: 1_000_000, MaxEntries: 1000);
        _archive.Add("LIB/QSRC/GOOD.clle", ArchiveFixture.EbcdicFixed(37, "PGM", "ENDPGM"));
        _archive.Add("LIB/QSRC/BADUTF.clle", [0x50, 0x47, 0x4D, 0xC3, 0x28]);
        _archive.Add("LIB/QSRC/CONTROL.clle", [.. ArchiveFixture.EbcdicFixed(37, "PGM").Take(79), 0x05]);
        _archive.Add("LIB/QSRC/SHORT.clle", [.. ArchiveFixture.Utf8Fixed(false, "PGM"), .. "ENDPGM"u8.ToArray()]);
        _archive.Add("LIB/QSRC/NEWLINE.clle", ArchiveFixture.Utf8Lines("PGM", "ENDPGM"));
        _archive.Add("LIB/QSRC/RAW92.clle", new byte[184]);
        _archive.Add("LIB/QSRC/CCSID500.clle", ArchiveFixture.EbcdicFixed(37, "PGM"));
        _archive.Add("LIB/QSRC/HUGE.clle", new byte[5000]);
        _archive.Add("LIB/QSRC/CHANGED.clle", ArchiveFixture.EbcdicFixed(37, "PGM"));
        _archive.Add("LIB/QSRC/UNCONF.clle", ArchiveFixture.EbcdicFixed(37, "PGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(37, RecordLayout.FixedSourceData, 80), limits);

        foreach (var path in new[] { "GOOD", "CONTROL", "CHANGED" })
        {
            ArchiveFixture.Choose(plan, $"LIB/QSRC/{path}.clle", 37, "fixed-source-data", 80);
        }

        ArchiveFixture.Choose(plan, "LIB/QSRC/BADUTF.clle", 1208, "lines");
        ArchiveFixture.Choose(plan, "LIB/QSRC/SHORT.clle", 1208, "fixed-source-data", 80);
        ArchiveFixture.Choose(plan, "LIB/QSRC/NEWLINE.clle", 1208, "fixed-source-data", 80);
        ArchiveFixture.Choose(plan, "LIB/QSRC/RAW92.clle", 37, "raw-source-record", 92);
        ArchiveFixture.Choose(plan, "LIB/QSRC/CCSID500.clle", 500, "fixed-source-data", 80);
        ArchiveFixture.EditPlan(plan, root =>
        {
            // A hand-added entry that escapes the root, and one that renames an identity: both rejected, never read.
            var entries = root["entries"]!.AsArray();
            var good = entries.Single(e => (string?)e!["path"] == "LIB/QSRC/GOOD.clle")!;
            var escape = good.DeepClone().AsObject();
            escape["path"] = "../outside/LIB/QSRC/GOOD.clle";
            entries.Add(escape);
            var renamed = good.DeepClone().AsObject();
            renamed["path"] = "LIB/QSRC/CHANGED.clle.copy";
            entries.Add(renamed);
        });
        File.WriteAllBytes(Path.Combine(_archive.Root, "LIB", "QSRC", "CHANGED.clle"), ArchiveFixture.EbcdicFixed(37, "PGX"));

        var output = _archive.NewPath("prepared");
        var built = preparer.Build(_archive.Root, plan, output);
        Assert.True(built.Succeeded, string.Join("; ", built.Diagnostics));
        var outcome = built.Value;
        Assert.Equal(PreparationStatus.Partial, outcome.Status);
        Assert.Equal(1, outcome.Prepared);
        Assert.Equal(
            new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["embedded-line-separator"] = 1,
                ["identity-mismatch"] = 1,
                ["input-changed"] = 1,
                ["invalid-encoding"] = 1,
                ["path-escape"] = 1,
                ["short-final-record"] = 1,
                ["unconfirmed"] = 1,
                ["unmappable-character"] = 1,
                ["unsupported-ccsid"] = 1,
                ["unsupported-record-layout"] = 1,
            },
            outcome.RejectedByReason);
        Assert.Equal(1, outcome.SkippedByReason["member-too-large"]);

        // Only the valid sibling is in the export; nothing fabricated, nothing replaced.
        var snapshot = _index.Env.Import("acme", output).Value.Snapshot;
        Assert.Equal("GOOD", Assert.Single(snapshot.Members).Identity.Member);
        Assert.DoesNotContain("�", File.ReadAllText(Path.Combine(output, "members", "000000.src")), StringComparison.Ordinal);
        Assert.Equal("rejected", (string?)ArchiveFixture.ReportEntry(output, "LIB/QSRC/CONTROL.clle")["outcome"]);
        Assert.Equal(79, (long?)ArchiveFixture.ReportEntry(output, "LIB/QSRC/CONTROL.clle")["reasonOffset"]);
    }

    [Fact]
    public void ScanRefusesTooManyEntriesInsteadOfTruncating()
    {
        for (var i = 0; i < 3; i++)
        {
            _archive.Add($"LIB/QSRC/M{i}.clle", ArchiveFixture.Utf8Lines("PGM"));
        }

        var result = new ArchivePreparer(limits: new PreparationLimits(1000, 1000, 2)).Scan(_archive.Root, _archive.NewPath("plan"), new(null, null, null));
        Assert.Equal(DiagnosticCode.TooManyEntries, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void TotalLimitSkipsLaterEntriesInPathOrder()
    {
        _archive.Add("LIB/QSRC/A.clle", new byte[600]);
        _archive.Add("LIB/QSRC/B.clle", new byte[600]);
        var (_, plan) = _archive.Scan(limits: new PreparationLimits(1000, 1000, 100));
        Assert.Equal("included", (string?)ArchiveFixture.Entry(plan, "LIB/QSRC/A.clle")["state"]);
        Assert.Equal("total-limit-exceeded", (string?)ArchiveFixture.Entry(plan, "LIB/QSRC/B.clle")["reason"]);
    }

    [SkippableFact]
    public void SymbolicLinksAreNeverFollowedAtScanOrBuild()
    {
        var outside = Path.Combine(_index.Env.Root, "outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "SECRET.clle"), ArchiveFixture.Utf8Lines("PGM"));
        _archive.Add("LIB/QSRC/OK.clle", ArchiveFixture.Utf8Lines("PGM", "ENDPGM"));
        Links.FileSymlink(Path.Combine(_archive.Root, "LIB", "QSRC", "LINK.clle"), Path.Combine(outside, "SECRET.clle"));
        Directory.CreateDirectory(Path.Combine(_archive.Root, "LINKLIB"));
        Links.DirectorySymlink(Path.Combine(_archive.Root, "LINKLIB", "QSRC"), outside);

        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        Assert.Equal("symbolic-link", (string?)ArchiveFixture.Entry(plan, "LIB/QSRC/LINK.clle")["reason"]);
        Assert.Equal("symbolic-link", (string?)ArchiveFixture.Entry(plan, "LINKLIB/QSRC")["reason"]);
        Assert.DoesNotContain(ArchiveFixture.Entries(plan), e => ((string?)e!["path"])!.Contains("SECRET", StringComparison.Ordinal));

        // A path swapped for a link after the scan is refused at build time.
        ArchiveFixture.Choose(plan, "LIB/QSRC/OK.clle", 1208, "lines");
        var ok = Path.Combine(_archive.Root, "LIB", "QSRC", "OK.clle");
        File.Delete(ok);
        Links.FileSymlink(ok, Path.Combine(outside, "SECRET.clle"));
        var built = preparer.Build(_archive.Root, plan, _archive.NewPath("out"));
        Assert.Equal(DiagnosticCode.NothingToPrepare, Assert.Single(built.Diagnostics).Code);
        Assert.Contains("symbolic-link 1", built.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void WindowsJunctionsAreNeverFollowed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Junctions exist only on Windows");
        var outside = Path.Combine(_index.Env.Root, "outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(outside, "QSRC"));
        File.WriteAllBytes(Path.Combine(outside, "QSRC", "SECRET.clle"), ArchiveFixture.Utf8Lines("PGM"));
        Links.Junction(Path.Combine(_archive.Root, "JLIB"), outside);
        _archive.Add("LIB/QSRC/OK.clle", ArchiveFixture.Utf8Lines("PGM"));

        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        Assert.Equal("symbolic-link", (string?)ArchiveFixture.Entry(plan, "JLIB")["reason"]);

        // A hand-written entry through the junction is refused by the no-follow reader.
        ArchiveFixture.EditPlan(plan, root =>
        {
            var copy = root["entries"]!.AsArray().Single(e => (string?)e!["path"] == "LIB/QSRC/OK.clle")!.DeepClone().AsObject();
            copy["path"] = "JLIB/QSRC/SECRET.clle";
            copy["library"] = "JLIB";
            copy["member"] = "SECRET";
            copy["confirmed"] = true;
            root["entries"]!.AsArray().Add(copy);
        });
        var output = _archive.NewPath("out");
        ArchiveFixture.Choose(plan, "LIB/QSRC/OK.clle", 1208, "lines");
        var built = preparer.Build(_archive.Root, plan, output);
        Assert.Equal(1, built.Value.RejectedByReason["symbolic-link"]);
    }

    // ---- Plan validation and output safety ------------------------------------------------------

    [Theory]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 1, \"extra\": true")]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1")]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 2")]
    [InlineData("\"layout\": \"archived-library-tree\"", "\"layout\": \"flat\"")]
    [InlineData("\"state\": \"included\"", "\"state\": 0")]
    [InlineData("\"state\": \"included\"", "\"state\": \"maybe\"")]
    [InlineData("\"confirmed\": false", "\"confirmed\": null")]
    [InlineData("\"entries\": [", "\"entries\": [null, ")]
    public void MalformedPlansAreRejectedWholesale(string find, string replace)
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Lines("PGM"));
        var (preparer, plan) = _archive.Scan();
        var text = File.ReadAllText(plan);
        Assert.Contains(find, text, StringComparison.Ordinal);
        File.WriteAllText(plan, text.Replace(find, replace, StringComparison.Ordinal));

        var output = _archive.NewPath("out");
        var result = preparer.Build(_archive.Root, plan, output);
        Assert.Equal(DiagnosticCode.PreparationPlanInvalid, Assert.Single(result.Diagnostics).Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void UnconfirmedIncompleteAndContradictoryEntriesAreNotConverted()
    {
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            _archive.Add($"LIB/QSRC/{name}.clle", ArchiveFixture.EbcdicFixed(37, "PGM"));
        }

        var (preparer, plan) = _archive.Scan(new ScanDefaults(37, RecordLayout.FixedSourceData, 80));
        ArchiveFixture.Choose(plan, "LIB/QSRC/A.clle", 37, "fixed-source-data", 80);
        ArchiveFixture.EditPlan(plan, root =>
        {
            var entries = root["entries"]!.AsArray();
            JsonObject Get(string name) => entries.Single(e => (string?)e!["path"] == $"LIB/QSRC/{name}.clle")!.AsObject();
            Get("B")["ccsid"] = null;
            Get("B")["confirmed"] = true;
            Get("C")["reason"] = "duplicate-identity";
            Get("C")["confirmed"] = true;
            Get("D")["member"] = "OTHER";
            Get("D")["confirmed"] = true;
        });

        var built = preparer.Build(_archive.Root, plan, _archive.NewPath("out")).Value;
        Assert.Equal(1, built.Prepared);
        Assert.Equal(["contradictory-choice", "identity-mismatch", "missing-choice"], built.RejectedByReason.Keys);
    }

    [Fact]
    public void ExistingOrInTreeOutputsAndPlansAreRefused()
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Lines("PGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        ArchiveFixture.Choose(plan, "LIB/QSRC/A.clle", 1208, "lines");

        var existing = _archive.NewPath("exists");
        Directory.CreateDirectory(existing);
        Assert.Equal(DiagnosticCode.PreparationOutputExists, Assert.Single(preparer.Build(_archive.Root, plan, existing).Diagnostics).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(existing));
        Assert.Equal(DiagnosticCode.InvalidArgument, Assert.Single(preparer.Build(_archive.Root, plan, Path.Combine(_archive.Root, "out")).Diagnostics).Code);
        Assert.Equal(DiagnosticCode.PreparationOutputExists, Assert.Single(preparer.Scan(_archive.Root, plan, new(null, null, null)).Diagnostics).Code);
        Assert.Equal(DiagnosticCode.InvalidArgument, Assert.Single(preparer.Scan(_archive.Root, Path.Combine(_archive.Root, "p.json"), new(null, null, null)).Diagnostics).Code);
        Assert.False(File.Exists(Path.Combine(_archive.Root, "p.json")));
    }

    /// <summary>
    /// A parent that is, or passes through, a link (a junction on Windows) into the source tree is
    /// refused for both the build output and the scan plan; nothing appears in the source tree.
    /// A link that leads outside the source tree is still allowed.
    /// </summary>
    [SkippableFact]
    public void OutputsAndPlansThroughLinkedParentsIntoTheSourceAreRefused()
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Lines("PGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        ArchiveFixture.Choose(plan, "LIB/QSRC/A.clle", 1208, "lines");
        var before = _archive.Hashes();

        var toRoot = _archive.NewPath("alias-root");
        Links.DirectoryLink(toRoot, _archive.Root);
        var toLibrary = _archive.NewPath("alias-lib");
        Links.DirectoryLink(toLibrary, Path.Combine(_archive.Root, "LIB"));
        var toRootParent = _archive.NewPath("alias-parent");
        Links.DirectoryLink(toRootParent, Path.GetDirectoryName(_archive.Root)!);
        var throughParent = Path.Combine(toRootParent, Path.GetFileName(_archive.Root));

        foreach (var parent in new[] { toRoot, toLibrary, throughParent, Path.Combine(toRoot, "LIB", "..") })
        {
            var built = preparer.Build(_archive.Root, plan, Path.Combine(parent, "prepared"));
            Assert.Equal(DiagnosticCode.InvalidArgument, Assert.Single(built.Diagnostics).Code);
            Assert.Contains("including through links or junctions", built.Diagnostics[0].Message, StringComparison.Ordinal);
            var scanned = preparer.Scan(_archive.Root, Path.Combine(parent, "plan.json"), new(null, null, null));
            Assert.Equal(DiagnosticCode.InvalidArgument, Assert.Single(scanned.Diagnostics).Code);
        }

        if (!OperatingSystem.IsWindows())
        {
            // A relative link target is resolved from the link's own directory.
            var relative = _archive.NewPath("alias-relative");
            Directory.CreateSymbolicLink(relative, Path.GetFileName(_archive.Root));
            Assert.Equal(DiagnosticCode.InvalidArgument, Assert.Single(preparer.Build(_archive.Root, plan, Path.Combine(relative, "prepared")).Diagnostics).Code);
        }

        Assert.Equal(before, _archive.Hashes());
        Assert.Equal(["LIB"], Directory.EnumerateFileSystemEntries(_archive.Root).Select(Path.GetFileName));

        var outside = _archive.NewPath("outside");
        Directory.CreateDirectory(outside);
        var toOutside = _archive.NewPath("alias-outside");
        Links.DirectoryLink(toOutside, outside);
        Assert.True(preparer.Scan(_archive.Root, Path.Combine(toOutside, "plan.json"), new(null, null, null)).Succeeded);
        var published = preparer.Build(_archive.Root, plan, Path.Combine(toOutside, "prepared"));
        Assert.True(published.Succeeded, string.Join("; ", published.Diagnostics));
        Assert.True(File.Exists(Path.Combine(outside, "prepared", "manifest.json")));
    }

    [Theory]
    [InlineData(500, null, null)]
    [InlineData(null, RecordLayout.FixedSourceData, null)]
    [InlineData(null, RecordLayout.FixedSourceData, 92)]
    [InlineData(null, RecordLayout.Lines, 80)]
    [InlineData(null, RecordLayout.RawSourceRecord, 92)]
    public void InvalidScanDefaultsAreRejected(int? ccsid, RecordLayout? layout, int? width)
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Lines("PGM"));
        var plan = _archive.NewPath("plan");
        Assert.False(new ArchivePreparer().Scan(_archive.Root, plan, new(ccsid, layout, width)).Succeeded);
        Assert.False(File.Exists(plan));
    }

    [Theory]
    [InlineData(PreparationStage.MembersWritten)]
    [InlineData(PreparationStage.ManifestWritten)]
    [InlineData(PreparationStage.ReportWritten)]
    public void InterruptedBuildLeavesNoExportAndRetrySucceeds(PreparationStage failAt)
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Lines("PGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        ArchiveFixture.Choose(plan, "LIB/QSRC/A.clle", 1208, "lines");
        var output = _archive.NewPath("out");
        preparer.FaultInjector = stage =>
        {
            if (stage == failAt)
            {
                throw new IOException("simulated");
            }
        };

        Assert.Equal(DiagnosticCode.PublishFailed, Assert.Single(preparer.Build(_archive.Root, plan, output).Diagnostics).Code);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(output)!, "*prepare-staging*"));

        preparer.FaultInjector = null;
        Assert.True(preparer.Build(_archive.Root, plan, output).Succeeded);
    }

    [Fact]
    public void CancelledBuildPublishesNothing()
    {
        _archive.Add("LIB/QSRC/A.clle", ArchiveFixture.Utf8Lines("PGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        ArchiveFixture.Choose(plan, "LIB/QSRC/A.clle", 1208, "lines");
        var output = _archive.NewPath("out");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(DiagnosticCode.PreparationCancelled, Assert.Single(preparer.Build(_archive.Root, plan, output, cancelled.Token).Diagnostics).Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PlanAndExportArePrivateAndLogsCarryNoSourceDerivedText()
    {
        _archive.Add("SECRETLIB/QSRC/HIDDENMBR.clle", ArchiveFixture.Utf8Lines("PGM /* FIXTURE-CANARY-PRIVATE */", "ENDPGM"));
        var (preparer, plan) = _archive.Scan(new ScanDefaults(1208, RecordLayout.Lines, null));
        ArchiveFixture.Choose(plan, "SECRETLIB/QSRC/HIDDENMBR.clle", 1208, "lines");
        var output = _archive.NewPath("out");
        Assert.True(preparer.Build(_archive.Root, plan, output).Succeeded);

        if (OperatingSystem.IsWindows())
        {
            StoragePrivacy.AssertOnlyCurrentUser(new FileInfo(plan).GetAccessControl(), plan);
            foreach (var directory in Directory.EnumerateDirectories(output, "*", SearchOption.AllDirectories).Prepend(output))
            {
                StoragePrivacy.AssertOnlyCurrentUser(new DirectoryInfo(directory).GetAccessControl(), directory);
            }

            Assert.All(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), f => Assert.True(File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(plan));
            foreach (var directory in Directory.EnumerateDirectories(output, "*", SearchOption.AllDirectories).Prepend(output))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            }

            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(file));
            }
        }

        var log = _index.Env.Log.AllText();
        Canaries.AssertAbsent(log);
        Assert.DoesNotContain("SECRETLIB", log, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDENMBR", log, StringComparison.Ordinal);
        Assert.Contains("prepare.build.published", log, StringComparison.Ordinal);
    }
}
