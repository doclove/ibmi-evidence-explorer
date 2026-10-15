# Local AI drafts (experimental, optional)

Everything else in IBM i Evidence Explorer works without AI. `explain` is an experimental, opt-in command that asks
a model already installed in a **local** Ollama service to draft an explanation of one member from
the `explore` dossier. The result is always labeled `draft-requires-human-review`.

**What validation does and does not prove.** The explorer checks the reply's structure, that every cited
fact or line ID was in the packet it sent, that each label is allowed by its citations, and that every
cited line still resolves unchanged in the snapshot. It **cannot** check that a sentence is true. A
reply can cite a real line and still misdescribe it, miss a dependency, or fail to admit a gap. A
person who can read the source must review every finding against its cited lines before relying on it.

## Preview first (no model, no network)

```sh
ibmi-explorer explain --workspace <id> --snapshot <snapshot-id> \
  --library <lib> --source-file <file> --member <mbr> --type <type> --preview-context [--json]
```

This prints the exact two messages `explain` would send: fixed system instructions and a user message
containing the reply JSON Schema and the context packet between `<<<IBMI_EXPLORER_CONTEXT_BEGIN>>>` and
`<<<IBMI_EXPLORER_CONTEXT_END>>>`, with the packet's SHA-256 and size. The packet is the dossier's facts
and at most 64 source lines (16 KiB). `<`, `>`, `&`, quotes, control characters, and non-ASCII
characters are escaped, so source text cannot contain the delimiters; source comments stay quoted data.

**The preview prints source text to stdout** (JSON-escaped inside the packet), just as `explore` and
`inspect` do. Nothing is sent anywhere, but with real source treat the output as confidential; see
[Storage and privacy](storage-and-privacy.md#logs-and-output).

## Setting up the local service

The explorer never installs, configures, downloads, or chooses anything.

1. Install Ollama yourself and pull a model you are permitted to use with this source
   (`ollama pull <model>`).
2. Keep the service on loopback (Ollama listens on `127.0.0.1:11434` by default; do not point
   `OLLAMA_HOST` at another address) and **disable Ollama cloud features** (`OLLAMA_NO_CLOUD=1`, or
   `"disable_ollama_cloud": true` in `~/.ollama/server.json`, then restart Ollama). Do not use a cloud
   or remote model.
3. Run `ollama list` and choose the exact model name.

**Residual risk:** a loopback connection does not prove the service keeps data local. Ollama can
forward requests for cloud models or through its own proxy settings. The explorer refuses models the
service reports as remote or cloud, but you must control the service's configuration.

## Running `explain`

```sh
ibmi-explorer explain --workspace <id> --snapshot <snapshot-id> \
  --library <lib> --source-file <file> --member <mbr> --type <type> \
  --model <installed-model> --acknowledge-local-service [--ollama-port 11434] [--timeout-seconds 180] [--json]
```

In order (any failure is a typed diagnostic, exit code `1`, and no draft):

1. Without `--acknowledge-local-service` the command is refused (`LocalAiNotAcknowledged`) before any
   network client exists.
2. The dossier and packet are built with the same checks as `explore`. A prompt over 96 KiB is `ContextTooLarge`.
3. `GET /api/tags` (no source) confirms the model is installed. A missing or ambiguous name is
   `LocalAiModelNotFound`; remote metadata or a cloud marker is `LocalAiModelNotLocal`.
4. One `POST /api/chat` is sent with `stream: false`, the packet's reply schema as `format`,
   temperature 0, and no tools. The client connects only to `127.0.0.1` (no name resolution), uses no
   proxy, refuses redirects, sends no cookies or credentials, and caps the request at 128 KiB and the
   response at 512 KiB. `--timeout-seconds` (default 180, maximum 600) bounds the whole exchange.
5. The reply must name the model that was checked (`LocalAiModelMismatch` otherwise).
6. The reply is validated strictly: one JSON object with exactly `schemaVersion`, `status`,
   `snapshotId`, `member`, and `claims`; the packet's own snapshot and member; 1-10 claims of at most
   400 characters, each citing at most 8 distinct IDs from the packet. `observed` may cite only `F`
   facts; `inference` must cite at least one `F` or `S` ID; `unknown` may cite nothing. There is no
   free-text summary field. **One invalid claim rejects the whole reply**; the explorer never relabels,
   repairs, drops, or displays an invalid claim.
7. Every cited line is re-read and re-hashed through the snapshot. Only then is the draft printed.

Other typed failures: `LocalAiUnavailable`, `LocalAiTimeout`, `LocalAiHttpError` (the body is never
shown), `LocalAiResponseTooLarge`, `LocalAiInvalidResponse`, `LocalAiInvalidOutput`,
`LocalAiCitationInvalid`, `LocalAiIdentityMismatch`, `LocalAiRefused`, `ExplainCancelled`.

The reply schema is generated per packet: evidence lists are `enum`s of the packet's own IDs and each
claim matches an `observed`, `inference`, or `unknown` branch. A runtime may enforce these keywords
while decoding, but enforcement depends on the runtime and model, so the explorer never relies on it;
the validator checks every rule itself. A constrained model can still pick a permitted
but irrelevant ID, which is one more reason for human review.

## Reading a draft

- **Overview**, written by the explorer (not the model): counts of observed, inference, and unknown
  claims, the dossier's completeness, and its gap count.
- **Findings**: `[observed]` claims restate a cited index fact; `[inference]` claims are the model's
  interpretation of cited lines. Each shows its citations, which you can open with `inspect`.
- **Unknowns stated by the model (unverified, uncited; not findings).**

The dossier's own completeness and gaps are shown with every draft. Provenance includes the
explanation run ID, snapshot, member, parser, index run, prompt, schema and packet versions, packet
SHA-256, endpoint, the model name as requested, listed, and reported, and its digest. Nothing is
cached or written; each `explain` is a new run.

## Privacy

Source text, packets, prompts, and replies stay in the explorer process except for the single
loopback request and what is printed to stdout (the draft, and with `--preview-context` the packet's
source lines). `explain` logs on stderr carry IDs, counts, the packet
hash, endpoint, model name, codes, and elapsed time only: no source text, prompts, replies, or member
names.

## Evaluation on synthetic cases

The test suite runs five synthetic cases (from `fixtures/synthetic/f2-explorer`, `f1-complete`, and
`f1-edge-cases`) against scripted model behaviors through the real adapter and a fake loopback
endpoint. It measures citation validity, missed direct links, false `observed` claims, and abstention
when the dossier has gaps. Scripted behaviors that invent citations, obey injected instructions, cite
a source line as `observed`, answer for the wrong member, decline, or reply in prose are all rejected.
Behaviors that **omit** links, never abstain, or make a false claim that cites a real fact all **pass
validation**: they can only be measured, which is why human review is required.

To evaluate a model you have already installed, on the same synthetic cases only:

```sh
IBMI_EXPLORER_EVAL_OLLAMA_MODEL=<model> dotnet test IbmiEvidenceExplorer.slnx --filter ExplanationEvaluationTests
```

This preview publishes no live-model result for the current prompt template. A run of this evaluation
covers five synthetic cases on one machine; it is not a measure of accuracy on real source.
