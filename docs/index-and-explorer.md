# Index and Explorer

`ibmi-explorer index` parses one imported snapshot and publishes a separate, versioned index. `search`,
`dependencies`, `gaps`, and `explore` read the newest index for that snapshot. None of them use a
model or the network.

## Supported syntax

IBM i Evidence Explorer is a narrow extractor for exported source, not a compiler. Unquoted identifiers match
case-insensitively; output keeps the source spelling. Comments and string literals never produce facts.

| Type | Recognized | Facts and edges |
|---|---|---|
| RPG (free) | `**FREE` on line 1. `ctl-opt ...;`, `dcl-f NAME [keywords];`, `dcl-pi ... end-pi;`, `chain KEY NAME;`. Statements end at `;`; `//` comments. | Program member; declared file; `uses-file` from each `dcl-f` and each `chain` of a file declared in the same member. `dcl-f` with `EXTFILE`, `EXTDESC`, `LIKEFILE`, `TEMPLATE`, or `HANDLER` stays unresolved. |
| RPG (fixed) | Members without `**FREE`, by RPG IV column position: `H` specs; `F` specs (externally or program described, `DISK PRINTER WORKSTN SPECIAL SEQ`, keyword continuation lines); `C` specs `CALL` (literal or name), `PARM` directly after `CALL`, and `CHAIN`. Comments (`*` in position 7), `/EJECT /TITLE /SPACE`. | Declared file and `uses-file` for each valid F spec (externally described DISK/PRINTER/WORKSTN resolve; others stay unresolved); `calls-program` for a literal `CALL` (`'NAME'` or `'LIB/NAME'`); `uses-file` for a `CHAIN` of a declared file. D, I, O, P specs, other operations, extended factor 2, `/COPY`, embedded `/FREE`, and RPG III layouts are unsupported. |
| CL | `PGM [PARM(...)]`, `ENDPGM`, `DCL VAR(&X) ...`, `CALL PGM(NAME)` or `CALL PGM(LIB/NAME)` with optional `PARM(...)`, optional `LABEL:`. One command per line, `+`/`-` continuation, `/* */` comments. | Program member; variable; `calls-program`. `CALL PGM(&VAR)` is unresolved `dynamic-target`; `*LIBL/NAME` is `unsupported-qualifier`. |
| DDS | Fixed-format physical file: `R` record, fields (name, length, type, decimals), `K` key, `*` comments. Keywords `TEXT COLHDG EDTCDE EDTWRD UNIQUE DFT ALWNULL VARLEN DESCEND`. | File member, record, field; `keys-field` from a record to a field of the same record. |
| SQL | One `CREATE [OR REPLACE] VIEW [SCHEMA.]NAME AS SELECT ... FROM [SCHEMA.]NAME [WHERE ...]` per member: one base table, no join, alias, subquery, set operation, CTE, column list, `LIB/NAME`, or quoted identifier. `--` and nested `/* */` comments. | View; `selects-from` from the view to its base table. |

Anything else is reported per statement (`unsupported-statement`, `unsupported-form`,
`malformed-statement`, or a lexical problem such as `unterminated-string`) and yields no facts. A
member with no statements is `unsupported` with a `no-statements` diagnostic. `ibmi-explorer gaps` prints a
report of unsupported CL commands and DDS keywords grouped by name, to help judge coverage of your
own source; it is shown only on request and is never written to logs.

## Resolution

- An edge resolves only when exactly one target exists in the snapshot. More than one is
  `ambiguous-target`. With none, the reason is `target-in-other-library` (a member with that name
  exists only under another library) or `target-not-in-snapshot`. No library list is inferred.
- `calls-program` targets CL or RPG members; `uses-file` and `selects-from` target DDS members.
- An unqualified target uses the calling member's manifest library and is marked
  `[unqualified: assumed caller's manifest library]`. This is not IBM i library-list resolution;
  `CHGCURLIB`, `ADDLIBLE`, and `SET SCHEMA` are not modeled.
- Only direct edges exist. There is no transitive or inferred edge.

## Status meanings

| Member coverage | Meaning |
|---|---|
| `complete` | Every meaningful statement is a supported form. This does **not** mean behavior is modeled. |
| `partial` | Some statements were understood; others were unsupported or malformed and produced nothing. |
| `unsupported` | Not parsed (a resource limit), nothing understood, or no statements. |

An index is `complete` only when every member is complete and every edge resolved; otherwise it is
`partial`. A partial index is still published (exit code `0`) and is always labeled.

## Integrity

Before returning results, queries re-hash the stored bytes of every member they cite and check each
citation against the snapshot's own line evidence (line number, byte offset, length, and line hash).
A tampered member is `IntegrityFailure`; an index whose citations do not match is
`IndexIntegrityFailure`. A failing newest index is reported, never silently replaced by an older one.

## Index limits

| Limit | Default | Opt-in ceiling | When exceeded |
|---|---|---|---|
| Bytes per member | 10 MiB | fixed | Member skipped (`member-too-large`) |
| Total source bytes (`--max-index-bytes`) | 100 MiB | 512 MiB | Later members skipped |
| Members (`--max-index-members`) | 10,000 | 50,000 | Later members skipped |
| Facts and edges (`--max-index-items`) | 100,000 | 500,000 | Member's facts dropped |
| Query results | 1,000 | fixed | Truncated, with total count shown |
| Stored diagnostics per member | 50 | fixed | Remainder counted; totals by code are exact |

A skipped member is `unsupported` and makes the index `partial`. At the opt-in ceilings, a synthetic
corpus of 50,000 members (509 MiB) indexed in about 30 seconds on an Apple silicon Mac but used about
2.5 GiB of memory, because each query loads the whole index.

## `explore`

```sh
ibmi-explorer explore --workspace <id> --snapshot <snapshot-id> \
  --library <lib> --source-file <file> --member <mbr> --type <RPG|CL|DDS|SQL> [--json]
```

`explore` answers one question for one exact member: *what does this member appear to call or use,
who calls it, and where can I inspect the supporting lines?* The dossier shows:

- snapshot, index run, parser and format versions, member hash, line count, coverage, diagnostics;
- the member's definition (`F1`), up to 40 direct outgoing and up to 40 direct incoming edges as
  numbered facts, each with its resolution basis;
- numbered source lines `S1…` (at most 64 lines and 16 KiB): the definition, reference lines,
  callers' lines, target definitions, diagnostic lines, then the rest of the member. Lines are never
  cut; a line that does not fit is counted as omitted;
- labeled gaps and standing notes.

Before display, every cited member is re-hashed, and the displayed facts are **re-derived from
source**: the member, each displayed caller, and each displayed target are re-parsed and their edges
rebuilt. Any mismatch is `IndexIntegrityFailure`.

**Completeness** is `complete` only when the member and index are complete, every outgoing edge
resolved, and nothing was omitted. Otherwise it is `partial`, with gap codes such as
`member-partial`, `index-partial`, `unresolved-outgoing`, `assumed-caller-library`,
`outgoing-truncated`, `incoming-truncated`, `unresolved-name-matches` (unresolved references elsewhere
that use this member's name; counted, never resolved), `evidence-omitted`, and `not-rederived`
(index-wide facts that a single-member check cannot confirm).

Incoming links are only references that the index resolved to this member. Dynamic calls,
unsupported syntax, and callers outside the snapshot are not visible.

## Citations

Every citation prints as `ibmi-explorer:<snapshot>/<library>/<file>/<member>/<type>#L<n>`. Open it with:

```sh
ibmi-explorer inspect --workspace <id> --snapshot <snapshot> \
  --library <library> --source-file <file> --member <member> --type <type> --lines <n>
```

`inspect` prints the requested source lines, with control characters shown as `\uXXXX`. `--json`
output carries the full evidence reference.

## Source text on stdout

`inspect`, `explore` (its numbered `S…` evidence lines), and `explain --preview-context` (the prompt's
context packet) print source text to stdout, and an `explain` draft prints model text derived from
it. `search`, `dependencies`, `gaps`, and `index` print names, counts, and citations only. With real
source, treat stdout from the first group as confidential; see
[Storage and privacy](storage-and-privacy.md#logs-and-output).

## Exit codes

`0` success (including a labeled `partial` result), `1` rejected with typed diagnostics, `2` usage error.
