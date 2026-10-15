# Feedback and contributions

IBM i Evidence Explorer is a public preview. The most useful contribution right now is feedback from people who
work with IBM i source.

## Before you post anything: no real source

Issues are public. Use **synthetic or redacted** examples only. Do not paste or attach real IBM i
source, snapshots, indexes, output of `explore`, `inspect`, or `explain` (they print source lines), preparation plans or reports, member or library names
you consider confidential, credentials, or logs from real runs. A few made-up lines that reproduce
the same syntax are enough; the files in `fixtures/synthetic` show the style.

Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Kinds of issues

- **Bug report:** a command failed, crashed, or produced a wrong result on synthetic input.
- **Source coverage request:** a construct the explorer reports as unsupported or misses (for example
  a CL command, DDS keyword, or RPG form) that matters for your work. `ibmi-explorer gaps` and the per-member
  diagnostics show what was not understood; share the codes and counts, not the source.
- **Usefulness feedback:** what you tried to find out, whether the explorer helped, how long it took
  compared with your usual approach, and what was missing or wrong.

## Code changes

Please open an issue to discuss a change before writing a pull request. Changes must keep the
project's principles: source stays local by default, deterministic results come before AI, every
result cites source evidence, partial results are labeled rather than hidden, and generated output
is never executed. Tests use synthetic fixtures only.

Before submitting, run:

```sh
dotnet restore IbmiEvidenceExplorer.slnx --locked-mode
dotnet build IbmiEvidenceExplorer.slnx --no-restore
dotnet test IbmiEvidenceExplorer.slnx --no-build
dotnet format IbmiEvidenceExplorer.slnx --verify-no-changes
```
