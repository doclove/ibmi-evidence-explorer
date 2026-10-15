# Storage and privacy

## Data boundary

IBM i Evidence Explorer is a single-user, local tool. It has no accounts, server, network listener,
telemetry, or encryption at rest; it relies on your operating system's file permissions. Import,
preparation, indexing, search, dependencies, `explore`, and `explain --preview-context` make no
network connection. The only network use is the optional `explain` request to a model service on
`127.0.0.1`, sent only after you pass `--acknowledge-local-service` (see [Local AI drafts](local-ai.md)).

Only analyze source you are authorized to use. Do not attach real source, snapshots, indexes, logs
that name your members, or stdout from `explore`, `inspect`, or `explain` to a public issue.

## Where data is stored

The storage root is `--storage <dir>`, otherwise `$IBMI_EXPLORER_STORAGE`, otherwise
`~/.ibmi-explorer` (`%USERPROFILE%\.ibmi-explorer` on Windows).

```text
<storage>/workspaces/<workspace-id>/
  snapshots/<snapshot-id>/snapshot.json        metadata, hashes, line counts
  snapshots/<snapshot-id>/complete.json        completion marker
  snapshots/<snapshot-id>/members/NNNNNN.src   original member bytes, unchanged
  indexes/<snapshot-id>/<parser-version>/<index-run-id>/
    facts.json, index.json, complete.json      derived index; no source text
  staging/, index-staging/                     in-progress work only
```

Imports and index builds are staged, verified, marked complete, made read-only, and published with
one directory rename. A failed or cancelled run removes its own staging directory. Workspaces are
separate directories; queries never cross workspaces.

**Permissions.** On macOS and Linux, directories are created `0700` and published files `0400`. On
Windows, directories get a protected access list that grants Full Control only to the current user,
and published files get read-only access for the current user plus the ReadOnly attribute; an
existing `workspaces` directory with a non-private access list is refused. Administrators (or root)
can still take ownership.

Snapshot IDs (`snap-…`) and run IDs (`run-…`) are generated. Re-importing unchanged content creates a
new snapshot with identical hashes and line evidence. Re-indexing creates a new index run; queries
use the newest.

## Logs and output

- Command results go to **stdout**. These commands print source text there, because you asked for it:
  - `inspect`: the requested lines;
  - `explore`: the dossier's numbered evidence lines (`S1`…, up to 64 lines from the member, its
    callers, and its targets);
  - `explain --preview-context`: the exact prompt, whose context packet contains those same source
    lines;
  - `explain` with a model: a draft whose claims are model text derived from those source lines.

  Control characters are escaped so source cannot drive your terminal. With real source, treat this
  stdout, and any file or terminal scrollback that captures it, as confidential as the source itself.
  `search`, `dependencies`, `index`, `gaps`, `list`, `import`, and `prepare` print names, counts,
  hashes, and citations, not source lines.
- Structured operation logs (JSON lines) go to **stderr**. They contain IDs, counts, hashes,
  diagnostic codes, and (for import and index operations) member identities and manifest paths. They
  never contain source text. Logs for `explore` and `explain` also omit member names. Search queries
  are not logged.
- The explorer writes no log file; redirect stderr if you want one, and treat it as confidential.

## Retention and deletion

The explorer never deletes anything automatically: not snapshots, older index runs, or staging left
by a killed process. Deleting a snapshot does not delete its indexes. To remove data, stop any
running command and delete the directory:

```sh
rm -rf ~/.ibmi-explorer/workspaces/<workspace-id>/snapshots/<snapshot-id>   # one snapshot
rm -rf ~/.ibmi-explorer/workspaces/<workspace-id>/indexes/<snapshot-id>     # its indexes
rm -rf ~/.ibmi-explorer/workspaces/<workspace-id>                           # a whole workspace
```

On Windows (PowerShell; `-Force` clears the ReadOnly attribute):

```powershell
Remove-Item -Recurse -Force "$env:USERPROFILE\.ibmi-explorer\workspaces\<workspace-id>"
```

A prepared export directory (from `prepare build`) and its `preparation-report.json` are yours to
delete the same way. `explain` stores nothing: no packets, prompts, or replies are cached or written.
