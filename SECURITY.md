# Security policy

## Reporting a vulnerability

Please report vulnerabilities **privately** using GitHub's private vulnerability reporting: open the
repository's **Security** tab and choose **Report a vulnerability**. Do not open a public issue, pull
request, or discussion for a suspected vulnerability.

Include the IBM i Evidence Explorer commit, operating system and architecture, the command you ran, and a
reproduction that uses **synthetic** source only. Never send real IBM i source, snapshots, indexes,
logs that name your members, stdout from `explore`, `inspect`, or `explain` (they print source
lines), credentials, or model output derived from real source.

This is a preview maintained on a best-effort basis. We will acknowledge a report, investigate it,
and coordinate disclosure with you; we cannot promise a fixed response time or offer a bug bounty.

## Supported versions

Only the latest commit on the default branch is supported during the preview. There are no released
binaries.

## Scope

In scope, for example:

- Reading a file outside the chosen source directory, or following a symbolic link, junction, or
  other reparse point during import or preparation.
- Snapshot or index storage that is readable or writable by other local users, or a tampered
  snapshot or index accepted as valid.
- Source text, prompts, or model replies appearing in operation logs, or terminal control sequences
  from source reaching the terminal unescaped.
- `explain` connecting anywhere other than the loopback address, sending data without
  `--acknowledge-local-service`, or accepting a model reported as remote.
- Source content or model output causing the explorer to run a command, call a tool, or display an
  invalid citation as valid.

Out of scope: the security of your Ollama installation and its configuration, a model's factual
accuracy (drafts always require human review), attacks that already require control of your user
account or administrator rights, and the documented limits in the README (for example hard links and
trusted ancestors of the source directory).
