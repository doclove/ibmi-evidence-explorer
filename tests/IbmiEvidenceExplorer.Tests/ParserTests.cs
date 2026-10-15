using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Parsing;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>F1 extractor behavior per language (criteria 3 and 4 at the parser level).</summary>
public class ParserTests
{
    private static string[] Names(ParsedMember parsed) => parsed.Artifacts.Select(a => a.Name.ToUpperInvariant()).ToArray();

    private static string[] Targets(ParsedMember parsed) => parsed.References.Select(r => r.Target.ToUpperInvariant()).ToArray();

    private static ParseDiagnosticCode[] Codes(ParsedMember parsed) => parsed.Diagnostics.Select(d => d.Code).ToArray();

    // ---- RPG ------------------------------------------------------------------------------------

    [Fact]
    public void RpgSupportedFormsAreCompleteAndCiteLines()
    {
        var parsed = Parse.Lines(SourceType.RPG,
            "**FREE", "Ctl-Opt dftactgrp(*no);", "DCL-F ordhdr keyed usage(*input);", "dcl-pi *n;", "  id packed(9:0) const;", "end-pi;",
            "chain (id:1) OrdHdr;", "chain id ORDHDR;");

        Assert.Equal(CoverageStatus.Complete, parsed.Coverage);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(1, parsed.DefinitionLine);
        var file = Assert.Single(parsed.Artifacts);
        Assert.Equal((ArtifactKind.DeclaredFile, "ordhdr", 3), (file.Kind, file.Name, file.Line));
        Assert.Equal([3, 7, 8], parsed.References.Select(r => r.Line));
        Assert.All(parsed.References, r => Assert.Equal((EdgeKind.UsesFile, (UnresolvedReason?)null), (r.Kind, r.FixedReason)));
        Assert.Equal("OrdHdr", parsed.References[1].Target); // display keeps source spelling
    }

    [Fact]
    public void RpgCommentsStringsAndMultiLineStatementsYieldNoFalseFacts()
    {
        var parsed = Parse.Lines(SourceType.RPG,
            "**free",
            "// dcl-f HIDDEN1; chain k HIDDEN1;",
            "dcl-f REAL // dcl-f HIDDEN2;",
            "   keyed;",
            "dcl-s s varchar(40) inz('dcl-f HIDDEN3; chain k HIDDEN3;');",
            "chain k REAL; // chain k HIDDEN4;");

        Assert.Equal(["REAL"], Names(parsed));
        Assert.Equal(["REAL", "REAL"], Targets(parsed));
        Assert.Equal(3, parsed.References[0].Line);
        Assert.Equal([ParseDiagnosticCode.UnsupportedStatement], Codes(parsed)); // dcl-s is outside the subset
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);
    }

    [Fact]
    public void RpgChainLinksOnlyToAFileDeclaredInTheSameMember()
    {
        var parsed = Parse.Lines(SourceType.RPG, "**free", "chain k NOTDECLARED;", "dcl-f A;", "dcl-f A;", "chain k A;");
        var chains = parsed.References.Where(r => r.Line is 2 or 5).ToList();
        Assert.Equal(UnresolvedReason.NotDeclaredInMember, chains[0].FixedReason);
        Assert.Equal(UnresolvedReason.AmbiguousTarget, chains[1].FixedReason);
    }

    [Fact]
    public void RpgUnsupportedAndMalformedFormsAreDiagnosed()
    {
        var parsed = Parse.Lines(SourceType.RPG,
            "**free", "dcl-f X extfile('LIB/OTHER');", "chain(e) k X;", "chain k X extra;", "dcl-f;", "/copy QRPGLESRC,PROTOS",
            "chain k X", "dcl-s y 'unterminated;");

        Assert.Equal(UnresolvedReason.UnsupportedForm, parsed.References[0].FixedReason);
        Assert.Equal(
            [ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.UnsupportedForm,
             ParseDiagnosticCode.MalformedStatement, ParseDiagnosticCode.UnsupportedStatement, ParseDiagnosticCode.MissingTerminator],
            Codes(parsed));
        Assert.Single(parsed.References); // only the dcl-f; no chain reference from unsupported shapes
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);
    }

    [Fact]
    public void RpgUnterminatedStringAndControlCharactersProduceNoFacts()
    {
        // The open literal also hides its ';', so its statement runs to the next terminator on line 3.
        var parsed = Parse.Lines(SourceType.RPG, "**free", "dcl-f A 'open;", "x;", "dcl-f B\r;", "dcl-f C;");
        Assert.Equal(["C"], Names(parsed));
        Assert.Equal([ParseDiagnosticCode.UnterminatedString, ParseDiagnosticCode.InvalidCharacter], Codes(parsed));
    }

    [Fact]
    public void RpgWithoutFreeOnLineOneUsesTheFixedSliceAndCompileTimeDataStops()
    {
        // F1 reported fixed form as unsupported; F1.5 parses its narrow fixed slice (see FixedRpgTests).
        var fixedForm = Parse.Lines(SourceType.RPG, "     FORDHDR    IF   E           K DISK");
        Assert.Equal(CoverageStatus.Complete, fixedForm.Coverage);
        Assert.Equal(["ORDHDR"], Targets(fixedForm));

        var data = Parse.Lines(SourceType.RPG, "**free", "dcl-f A;", "**CTDATA tbl", "dcl-f HIDDEN;");
        Assert.Equal(["A"], Names(data));
        Assert.Equal([ParseDiagnosticCode.CompileTimeDataUnsupported], Codes(data));
    }

    [Fact]
    public void EmptyBlankAndCommentOnlyMembersAreUnsupportedWithoutFacts()
    {
        foreach (var type in Enum.GetValues<SourceType>())
        {
            AssertNoStatements(SourceParser.Parse(type, []));
            AssertNoStatements(SourceParser.Parse(type, ["", "  "]));
        }

        AssertNoStatements(Parse.Lines(SourceType.CL, "/* only a comment */", ""));
        AssertNoStatements(Parse.Lines(SourceType.SQL, "-- only a comment", "/* and a block */"));
        AssertNoStatements(Parse.Lines(SourceType.DDS, "     A* only a comment"));

        static void AssertNoStatements(ParsedMember parsed)
        {
            Assert.Equal(CoverageStatus.Unsupported, parsed.Coverage);
            Assert.Null(parsed.DefinitionLine);
            Assert.Empty(parsed.Artifacts);
            Assert.Empty(parsed.References);
            var diagnostic = Assert.Single(parsed.Diagnostics);
            Assert.Equal((ParseDiagnosticCode.NoStatements, (int?)null), (diagnostic.Code, diagnostic.Line));
        }
    }

    // ---- CL -------------------------------------------------------------------------------------

    [Fact]
    public void ClSupportedFormsAreComplete()
    {
        var parsed = Parse.Lines(SourceType.CL,
            "/* header */", "pgm parm(&A)", "  DCL VAR(&A) TYPE(*CHAR) LEN(10)", "START: CALL PGM(lib1/Prog1) PARM(&A)",
            "  CALL PGM(PROG2)", "  CALL +", "    PGM(PROG3)", "ENDPGM");

        Assert.Equal(CoverageStatus.Complete, parsed.Coverage);
        Assert.Equal(2, parsed.DefinitionLine);
        var variable = Assert.Single(parsed.Artifacts);
        Assert.Equal((ArtifactKind.Variable, "&A", 3), (variable.Kind, variable.Name, variable.Line));
        Assert.Equal(["LIB1/PROG1", "PROG2", "PROG3"], Targets(parsed));
        Assert.Equal(("lib1", "Prog1"), (parsed.References[0].TargetLibrary, parsed.References[0].TargetName));
        Assert.Null(parsed.References[1].TargetLibrary);
        Assert.Equal(7, parsed.References[2].Line); // the line holding the target, after continuation
    }

    [Fact]
    public void ClDynamicQualifiedAndQuotedCallsAreUnresolvedObservations()
    {
        var parsed = Parse.Lines(SourceType.CL,
            "CALL PGM(&P)", "CALL PGM(&L/NAME)", "CALL PGM(*LIBL/NAME)", "CALL PGM('NAME')", "CALL NAME", "CALL PGM(A/B/C)", "CALL PGM()");

        Assert.Equal(
            [UnresolvedReason.DynamicTarget, UnresolvedReason.DynamicTarget, UnresolvedReason.UnsupportedQualifier, UnresolvedReason.UnsupportedForm],
            parsed.References.Select(r => r.FixedReason!.Value));
        Assert.Equal("(string literal)", parsed.References[3].Target); // literal text is never copied
        Assert.Equal(
            [ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.UnsupportedForm,
             ParseDiagnosticCode.MalformedStatement, ParseDiagnosticCode.MalformedStatement],
            Codes(parsed));
    }

    [Fact]
    public void ClCommentsStringsAndValuesThatLookLikeCommentsAreHandled()
    {
        var parsed = Parse.Lines(SourceType.CL,
            "/* CALL PGM(HIDDEN1) */ CALL PGM(REAL1) /* CALL PGM(HIDDEN2) */",
            "SNDPGMMSG MSG('CALL PGM(HIDDEN3)')",
            "DSPOBJD OBJ(QGPL/*ALL) OBJTYPE(*PGM)",
            "CHGVAR VAR(&X) VALUE('a +",
            "   CALL PGM(HIDDEN4)')",
            "CALL PGM(REAL2)");

        Assert.Equal(["REAL1", "REAL2"], Targets(parsed));
        Assert.Equal(6, parsed.References[1].Line);
        Assert.Equal(3, parsed.Diagnostics.Count); // three unsupported commands, nothing hidden became an edge
        Assert.All(parsed.Diagnostics, d => Assert.Equal(ParseDiagnosticCode.UnsupportedStatement, d.Code));
    }

    [Fact]
    public void ClUnclosedCommentSwallowsLinesAndIsDiagnosed()
    {
        var parsed = Parse.Lines(SourceType.CL, "CALL PGM(A)", "/* opens", "CALL PGM(HIDDEN) */", "CALL PGM(B)", "/* never closed", "CALL PGM(HIDDEN2)");
        Assert.Equal(["A", "B"], Targets(parsed));
        Assert.Contains(ParseDiagnosticCode.UnterminatedComment, Codes(parsed));
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);
    }

    [Fact]
    public void ClStructureErrorsAreDiagnosed()
    {
        var parsed = Parse.Lines(SourceType.CL, "DCL VAR(&A) TYPE(*CHAR)", "PGM", "DCL &B *CHAR 10", "CALL PGM(X", "ENDPGM", "CALL PGM(AFTER)", "CALL PGM(Y) +");
        Assert.Equal(
            [ParseDiagnosticCode.MalformedStatement, ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.MalformedStatement,
             ParseDiagnosticCode.StatementAfterEnd, ParseDiagnosticCode.MissingTerminator],
            Codes(parsed));
        Assert.Empty(parsed.References);
    }

    // ---- DDS ------------------------------------------------------------------------------------

    [Fact]
    public void DdsRecordFieldsAndKeysAreRecordedByColumn()
    {
        var parsed = Parse.Lines(SourceType.DDS,
            "     A* comment R NOTREC",
            Parse.Dds(' ', "", keywords: "UNIQUE"),
            Parse.Dds('R', "REC1", keywords: "TEXT('R K A')"),
            Parse.Dds(' ', "FLD1", "9", 'P', "0", "TEXT('K FLD9')"),
            Parse.Dds(' ', "FLD2", "10", 'A'),
            Parse.Dds('K', "FLD1"),
            Parse.Dds('K', "FLD2", keywords: "DESCEND"));

        Assert.Equal(CoverageStatus.Complete, parsed.Coverage);
        Assert.Equal(2, parsed.DefinitionLine);
        Assert.Equal([(ArtifactKind.Record, "REC1", (string?)null), (ArtifactKind.Field, "FLD1", "REC1"), (ArtifactKind.Field, "FLD2", "REC1")],
            parsed.Artifacts.Select(a => (a.Kind, a.Name, a.Qualifier)));
        Assert.Equal([6, 7], parsed.References.Select(r => r.Line));
        Assert.All(parsed.References, r => Assert.Equal((EdgeKind.KeysField, ArtifactKind.Record, "REC1"), (r.Kind, r.SourceKind, r.SourceName)));
    }

    [Fact]
    public void DdsMalformedAndUnsupportedEntriesAreDiagnosed()
    {
        var parsed = Parse.Lines(SourceType.DDS,
            Parse.Dds('K', "EARLY"),
            Parse.Dds('R', "REC"),
            "     A   01       FLD1          1A",
            Parse.Dds(' ', "FLD2", "1", 'A', keywords: "REFFLD(X)"),
            Parse.Dds(' ', "FLD3", "1", 'Q'),
            Parse.Dds('S', "FLD2", keywords: "COMP(EQ 'A')"),
            "     A\t          R REC2",
            Parse.Dds(' ', "FLD4", "1", 'A', keywords: "TEXT('open"),
            Parse.Dds(' ', "FLD5", "1", 'A') + new string(' ', 60) + "X");

        Assert.Equal(["REC", "FLD2"], Names(parsed)); // a line with unparseable keywords yields nothing
        Assert.Equal(
            [ParseDiagnosticCode.MalformedStatement, ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.UnsupportedForm,
             ParseDiagnosticCode.MalformedStatement, ParseDiagnosticCode.UnsupportedStatement, ParseDiagnosticCode.InvalidCharacter,
             ParseDiagnosticCode.UnsupportedForm, ParseDiagnosticCode.MalformedStatement],
            Codes(parsed));
        Assert.Empty(parsed.References);
    }

    // ---- SQL ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("CREATE VIEW V1 AS SELECT A FROM T1;", null, "V1", null, "T1")]
    [InlineData("create or replace view s.v1 as select a, f(b) from s2.t1 where a = 'x' and b in (1, 2);", "s", "v1", "s2", "t1")]
    [InlineData("CREATE VIEW V1 AS SELECT EXTRACT(YEAR FROM D) AS Y FROM T1;", null, "V1", null, "T1")]
    public void SqlSupportedViewIsComplete(string sql, string? schema, string view, string? tableSchema, string table)
    {
        var parsed = Parse.Lines(SourceType.SQL, sql);
        Assert.Equal(CoverageStatus.Complete, parsed.Coverage);
        var artifact = Assert.Single(parsed.Artifacts);
        Assert.Equal((ArtifactKind.View, view, schema), (artifact.Kind, artifact.Name, artifact.Qualifier));
        var reference = Assert.Single(parsed.References);
        Assert.Equal((EdgeKind.SelectsFrom, ArtifactKind.View, view, tableSchema, table), (reference.Kind, reference.SourceKind, reference.SourceName, reference.TargetLibrary, reference.TargetName));
    }

    [Theory]
    [InlineData("CREATE VIEW V AS SELECT A FROM T1 JOIN T2 ON T1.A = T2.A;")]
    [InlineData("CREATE VIEW V AS SELECT A FROM T1, T2;")]
    [InlineData("CREATE VIEW V AS SELECT A FROM T1 WHERE A IN (SELECT B FROM T2);")]
    [InlineData("CREATE VIEW V AS SELECT (SELECT 1 FROM T3) FROM T1;")]
    [InlineData("CREATE VIEW V AS WITH C AS (SELECT A FROM T1) SELECT A FROM C;")]
    [InlineData("CREATE VIEW V AS SELECT A FROM T1 UNION SELECT A FROM T2;")]
    [InlineData("CREATE VIEW V AS SELECT \"a\" FROM T1;")]
    [InlineData("CREATE VIEW V AS SELECT A FROM T1 X;")]
    [InlineData("CREATE VIEW V AS SELECT A FROM LIB/T1;")]
    public void SqlViewOutsideTheSubsetKeepsTheViewButNoBaseTable(string sql)
    {
        var parsed = Parse.Lines(SourceType.SQL, sql);
        Assert.Equal("V", Assert.Single(parsed.Artifacts).Name);
        Assert.Empty(parsed.References);
        Assert.Equal([ParseDiagnosticCode.UnsupportedForm], Codes(parsed));
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);
    }

    [Theory]
    [InlineData("CREATE VIEW \"V\" AS SELECT A FROM T1;", ParseDiagnosticCode.UnsupportedForm)]
    [InlineData("CREATE VIEW V (C1) AS SELECT A FROM T1;", ParseDiagnosticCode.MalformedStatement)]
    [InlineData("CREATE VIEW AS SELECT;", ParseDiagnosticCode.MalformedStatement)]
    [InlineData("CREATE TABLE T (A INT);", ParseDiagnosticCode.UnsupportedStatement)]
    [InlineData("CREATE VIEW V AS SELECT 'open FROM T1", ParseDiagnosticCode.UnterminatedString)]
    [InlineData("CREATE VIEW V AS SELECT A FROM T1 /* open", ParseDiagnosticCode.UnterminatedComment)]
    public void SqlStatementsWithoutAViewHeaderYieldNoFacts(string sql, ParseDiagnosticCode code)
    {
        var parsed = Parse.Lines(SourceType.SQL, sql);
        Assert.Empty(parsed.Artifacts);
        Assert.Empty(parsed.References);
        Assert.Equal([code], Codes(parsed));
        Assert.Equal(CoverageStatus.Unsupported, parsed.Coverage);
    }

    [Fact]
    public void SqlCommentsStringsAndExtraStatements()
    {
        var parsed = Parse.Lines(SourceType.SQL,
            "-- CREATE VIEW HIDDEN1 AS SELECT A FROM HIDDENT",
            "/* CREATE VIEW HIDDEN2 AS", "   SELECT A FROM HIDDENT */",
            "CREATE VIEW V AS SELECT 'FROM HIDDENT; JOIN' AS C",
            "  FROM T1; -- trailing",
            "CREATE VIEW SECOND AS SELECT A FROM T2;",
            "SET SCHEMA X;");

        Assert.Equal(["V"], Names(parsed));
        Assert.Equal(["T1"], Targets(parsed));
        Assert.Equal(5, parsed.References[0].Line);
        Assert.Equal(4, parsed.DefinitionLine);
        Assert.Equal([ParseDiagnosticCode.UnsupportedStatement, ParseDiagnosticCode.UnsupportedStatement], Codes(parsed));
    }

    [Fact]
    public void SqlBlockCommentsNestSoADecoyInsideANestedCommentYieldsNothing()
    {
        var parsed = Parse.Lines(SourceType.SQL,
            "/* outer",
            "   /* inner */",
            "   CREATE VIEW FAKEVW AS SELECT A FROM DECOYT;",
            "   /* second /* deeper */ level */ CREATE VIEW FAKE2 AS SELECT A FROM DECOYT;",
            "*/",
            "CREATE VIEW REALVW AS SELECT A FROM T1; /* trailing /* nested */ */");

        Assert.Equal(CoverageStatus.Complete, parsed.Coverage);
        Assert.Equal(["REALVW"], Names(parsed));
        Assert.Equal(["T1"], Targets(parsed));
        Assert.Equal((6, 6), (parsed.Artifacts[0].Line, parsed.References[0].Line));
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void SqlUnclosedNestedCommentYieldsNoFacts()
    {
        // The first */ closes only the inner comment, so the view text is still inside the outer one.
        var parsed = Parse.Lines(SourceType.SQL, "/* outer /* inner */", "CREATE VIEW FAKEVW AS SELECT A FROM DECOYT;");
        Assert.Empty(parsed.Artifacts);
        Assert.Empty(parsed.References);
        Assert.Equal([ParseDiagnosticCode.UnterminatedComment], Codes(parsed));
        Assert.Equal(CoverageStatus.Unsupported, parsed.Coverage);
    }

    [Fact]
    public void SqlStatementUnfinishedAtEndOfMemberYieldsNoFacts()
    {
        var parsed = Parse.Lines(SourceType.SQL, "CREATE VIEW V1 AS SELECT A FROM T1;", "CREATE VIEW V2 AS", "  SELECT A FROM T2", "-- no terminator");
        Assert.Equal(["V1"], Names(parsed));
        Assert.Equal(["T1"], Targets(parsed));
        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal((ParseDiagnosticCode.MissingTerminator, (int?)2), (diagnostic.Code, diagnostic.Line));
        Assert.Equal(CoverageStatus.Partial, parsed.Coverage);

        var only = Parse.Lines(SourceType.SQL, "CREATE VIEW V AS SELECT A FROM T1");
        Assert.Empty(only.Artifacts);
        Assert.Empty(only.References);
        Assert.Equal([ParseDiagnosticCode.MissingTerminator], Codes(only));
        Assert.Equal(CoverageStatus.Unsupported, only.Coverage);
    }

    [Fact]
    public void DiagnosticsAreCappedPerMemberWithACount()
    {
        var lines = Enumerable.Repeat("x = 1;", 80).Prepend("**free").ToArray();
        var parsed = Parse.Lines(SourceType.RPG, lines);
        Assert.Equal(SourceParser.MaxDiagnosticsPerMember + 1, parsed.Diagnostics.Count);
        Assert.Equal(30, parsed.SuppressedDiagnostics);
        Assert.Equal(ParseDiagnosticCode.DiagnosticsTruncated, parsed.Diagnostics[^1].Code);
    }

    [Fact]
    public void ParsingIsDeterministic()
    {
        var lines = File.ReadAllLines(Path.Combine(TestEnvironment.FixturePath("f1-edge-cases"), "QCLSRC", "CALLS.clle"));
        Assert.Equivalent(SourceParser.Parse(SourceType.CL, lines), SourceParser.Parse(SourceType.CL, lines), strict: true);
    }
}
