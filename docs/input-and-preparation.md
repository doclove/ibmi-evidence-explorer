# Input and preparation

IBM i Evidence Explorer reads source only from a local directory you choose. It never connects to IBM i, never
fetches source, and never edits the directory it reads.

## The import directory

`ibmi-explorer import --workspace <id> --source <dir>` reads a directory containing `manifest.json` and
the source files it lists:

```json
{
  "formatVersion": 1,
  "members": [
    { "path": "QRPGLESRC/ORDINQ.rpgle", "library": "DEMOLIB", "sourceFile": "QRPGLESRC",
      "member": "ORDINQ", "sourceType": "RPG", "ccsid": 1208 }
  ]
}
```

- The manifest is strict UTF-8 JSON (a BOM is allowed): no comments, trailing commas, unknown or
  duplicate properties; at least one member. All six entry fields are required.
- `path` is relative to the source directory, `/`-separated, without empty, `.` or `..` segments,
  backslashes, colons, or control characters. It must name a regular file.
- `library`, `sourceFile`, and `member` must be IBM i unquoted system names: 1-10 characters of
  `A-Z 0-9 $ # @ _ .`, starting with `A-Z $ # @`. Values are stored exactly as declared.
- `sourceType` is `RPG`, `CL`, `DDS`, or `SQL`; it selects the parser.
- `ccsid` must be `1208` and every file must be valid UTF-8. An invalid byte is reported with its
  offset and line number, not its content. For other encodings, use `prepare` (below).
- A member's identity is library / source file / member / source type. Duplicate identities are rejected.

Every problem is reported as a typed diagnostic and nothing is stored.

**Links are refused.** The source directory, every directory on a member's path, and every file are
opened without following symbolic links (and, on Windows, junctions and other reparse points). Bytes
are read from the handle that was checked, so a path swapped for a link during import is refused, not
followed. The source directory's own parent directories are trusted, as chosen by you. Hard links are
not detected.

### Lines

Lines end at LF or CRLF; the terminator is not part of the line, and a lone CR is content. An empty
file has zero lines, a final terminator does not add an empty line, and a UTF-8 BOM is excluded from
line 1. Line numbers in every citation follow these rules.

### Import limits

| Limit | Default | Option | Upper bound |
|---|---|---|---|
| Bytes per member | 10 MiB | `--max-member-bytes` | 64 MiB |
| Bytes per import | 100 MiB | `--max-import-bytes` | 1 GiB |
| Members per import | 10,000 | `--max-members` | 100,000 |
| Manifest size | 8 MiB | fixed | — |

Limits are checked before and while reading. The applied values are printed and recorded in the
snapshot. `ibmi-explorer limits` prints all defaults.

## Preparing an archived export

Many IBM i exports are not line-delimited UTF-8. `ibmi-explorer prepare` turns an archived library tree
into a **new** import directory, converting only what you explicitly confirm. It never edits the
archive, never guesses a CCSID, record layout, record width, library list, or identity, and sends
nothing anywhere.

```sh
ibmi-explorer prepare scan  --source ARCHIVE --layout archived-library-tree --plan PLAN.json \
                       [--ccsid 1208|37|1140] [--record-layout lines|fixed-source-data] [--record-width 80]
# Review PLAN.json: for each entry set ccsid, recordLayout, recordWidth, state, then "confirmed": true.
ibmi-explorer prepare build --source ARCHIVE --plan PLAN.json --output NEW_DIR
ibmi-explorer import --workspace W --source NEW_DIR
```

**Archive layout.** Paths are `LIBRARY/SOURCEFILE/[TYPE/]MEMBER.extension[.utf8]`. The first two
segments are the library and source file; an optional third directory is ignored; the file name
gives the member. Names match case-insensitively and are recorded in upper case. Extensions:
`rpg`/`rpgle` RPG, `clp`/`clle` CL, `dds`/`pf`/`lf`/`dspf`/`prtf` DDS, `sql` SQL. An extension does
not prove semantics (a `.lf` is parsed with the physical-file subset and stays partial). Links are
recorded as `symbolic-link` and never entered.

**The plan** lists every discovered entry with its path, derived identity, `included`/`skipped`
state, reason, `ccsid`, `recordLayout`, `recordWidth`, `confirmed`, input size and SHA-256, and
read-only observations (BOM, valid UTF-8, line separators). Every entry starts **unconfirmed**: a
`.utf8` suffix or a scan option only suggests a value. Entries with the same identity are all skipped
until you choose one. The plan is written privately, never over an existing file, and never inside
the archive; it is read strictly, so an unknown field or changed version rejects the whole plan.

**Conversion.** Prepared line N is always original record N.

| CCSID | Record layout | Rule |
|---|---|---|
| 1208 | `lines` | Strict UTF-8; an initial BOM is removed; all other bytes, including LF/CRLF, are kept. |
| 1208 | `fixed-source-data`, width 80 | Strict UTF-8; BOM removed; exact 80-character records joined by LF; trailing spaces kept. |
| 37, 1140 | `fixed-source-data`, width 80 | Exact 80-byte records, each byte mapped by a built-in table (1140 adds the euro sign), joined by LF. |

Anything else is rejected per entry and never approximated, for example `unsupported-ccsid`,
`unsupported-record-layout` (including source records with sequence and date fields), `invalid-encoding`,
`unmappable-character`, `embedded-line-separator`, `short-final-record`, `input-changed` (the file
changed since the scan), `unconfirmed`, and `duplicate-identity`. A file length divisible by 80 is never
taken as evidence of fixed records.

**Output.** `NEW_DIR` must not exist and must be outside the archive. The build is staged, verified,
made read-only, and published with one rename; interruption publishes nothing. It contains
`manifest.json`, `members/NNNNNN.src`, and a private `preparation-report.json` with per-entry
outcomes. If some entries are rejected, the export is published as `partial` without them; if none
can be prepared, nothing is published. Console output and logs carry counts and reason codes only.

Preparation limits (fixed): 10 MiB per member, 1 GiB total, 100,000 archive entries (a larger archive
is refused, not truncated), 64 MiB plan file.

Line numbers and text in later citations are those of the prepared UTF-8 snapshot. Confirm the
original record layout and CCSID of your export from its own documentation; the explorer cannot verify them.
