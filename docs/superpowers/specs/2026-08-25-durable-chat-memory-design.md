# Durable Chat Memory Design

## Objective

Make the .NET 9 sample reproducible from a fresh clone and make its behavior match the paired Cool Solution articles. The implementation must preserve a complete transcript without an unbounded embedded MongoDB array, persist summarization state across HTTP requests and process restarts, resolve concurrent chat turns deterministically, follow Federico's base rules, and expose honest diagnostics.

The work spans two repositories:

- `/Users/federicoscamuzzi/Documents/_mio/chat-memory-llm-dotnet-mongodb`
- `/Users/federicoscamuzzi/Documents/_mio/coolsolution-blog`

All changes remain uncommitted. No deployment, push, production mutation, or history rewrite is in scope.

## Architectural decision

Store conversation metadata and completed turns separately.

- `Conversation` remains a small document containing id, title, and creation time.
- `ConversationTurn` is an immutable document containing one completed user/assistant pair, its sequence, timestamp, and an optional newly generated durable summary checkpoint.
- A unique compound index on `(ConversationId, Sequence)` provides optimistic serialization across API processes.
- Transcript reads are paginated by turn sequence.
- Summarizing model-input reads load the latest checkpoint and only the unsummarized tail. Counting reads load only the latest required turns. The explicit `None` baseline loads the complete transcript.

This replaces the unbounded `Conversation.Messages` array. It preserves every completed turn in bounded-size documents, but it does not implement data expiry. A production retention, archive, or TTL policy remains an explicit operational decision rather than an undocumented promise.

Rejected alternatives:

1. A bounded embedded array with `$slice` would be simpler but would discard old raw turns and keep conversation state concentrated in one document.
2. A MongoDB transaction would require a replica set and add transaction retry behavior to a sample focused on chat memory.
3. A distributed lease would require token ownership, expiry, renewal, and crash recovery, distracting from the reducer behavior.

## Domain model

### Conversation

`Conversation` contains:

- `Id`: MongoDB ObjectId string.
- `Title`: normalized display title.
- `CreatedAt`: UTC creation timestamp.

`UpdatedAt` is derived from the latest turn timestamp when a transcript is read; it is not maintained through a second cross-document write.

### ConversationTurn

`ConversationTurn` contains:

- `Id`: MongoDB ObjectId string.
- `ConversationId`: owning conversation id.
- `Sequence`: one-based monotonic turn number.
- `UserText`: validated user message.
- `AssistantText`: completed model response.
- `CreatedAt`: UTC completion timestamp.
- `CheckpointCreated`: optional `ReducerCheckpoint`, populated only when this turn refreshed the summary.

One turn document is the atomic persistence unit. The user message is not stored if the model call fails or is cancelled.

### ReducerCheckpoint

`ReducerCheckpoint` contains:

- `SummaryText`: application-owned durable summary.
- `SummarizedThroughSequence`: highest completed turn covered by the summary.

The application never serializes `ChatMessage.AdditionalProperties` or depends on the private `__summary__` marker used internally by `SummarizingChatReducer`.

## Repository boundaries

`IConversationRepository` exposes behavior-oriented operations:

- create a conversation;
- verify/load conversation metadata;
- load the latest turn and latest durable checkpoint;
- load turns after a checkpoint for prompt construction;
- insert an immutable turn at an expected sequence;
- return a paginated transcript page.

MongoDB reads use `AsQueryable()` with LINQ. Native driver APIs are limited to `InsertOneAsync` and index creation, which LINQ cannot express.

`InsertTurnAsync` reports an explicit result enum:

- `Succeeded`;
- `SequenceConflict`.

Conversation absence is established by the state read before the model call; the API exposes no delete operation. The immutable insert replaces the former unchecked `UpdateOneAsync`, so there is no zero-match write to misreport. The unique compound index maps duplicate-key errors to `SequenceConflict`; other database errors propagate.

An index initializer creates one unique ascending `(ConversationId, Sequence)` index. MongoDB can traverse that compound index in reverse for latest-turn and backward-pagination reads, so a duplicate descending index is unnecessary.

Index initialization completes before the API accepts traffic.

## Durable reduction

An application-level `ConversationMemoryReducer` returns a `ReductionResult` containing:

- the exact prompt messages to send to the answer model;
- an optional newly generated checkpoint to persist with the completed turn;
- whether the summary was refreshed during this request.

Strategies are represented by a `ChatMemoryStrategy` enum:

- `None` intentionally loads and sends the complete raw transcript as an unsafe measurement baseline;
- `Counting` queries only enough latest turns for the target, then uses `MessageCountingChatReducer` to retain the first system message and the configured recent non-system messages;
- `Summarizing` reuses the durable checkpoint until the unsummarized tail exceeds `TargetMessageCount + SummarizationThreshold`.

When summarization refreshes, the adapter invokes the framework `SummarizingChatReducer` with the configured target and threshold, supplying the previous summary as an assistant message prefixed `Previous summary:` followed by the eligible tail. It maps the retained original message instances back to their turn sequences, produces a new checkpoint through the last fully summarized turn, and uses the threshold window to keep complete user/assistant pairs at the cutoff. When the threshold is not crossed, it reuses the checkpoint without invoking the summarization model.

Durable checkpoint ownership and threshold decisions remain application code. The adapter never reads, writes, or serializes the framework's private summary metadata key. The summary is persisted only with the same completed turn as the answer, so a failed or cancelled answer cannot advance memory state.

## Concurrent-turn behavior

For each send attempt:

1. Load conversation state, latest sequence, checkpoint, and unsummarized tail.
2. Build and reduce the prompt.
3. Call the model.
4. Attempt to insert sequence `latest + 1` as one immutable turn.
5. On `SequenceConflict`, discard the stale response, reload the winning turn, rebuild the prompt, and regenerate once.
6. If the retry also conflicts, return typed HTTP `409 Conflict`.

Only a response whose turn was persisted is returned as successful. This allows cross-process optimistic serialization without a lease or transaction. Genuine contention may consume one redundant model call; the README and article state this trade-off.

## Configuration and validation

`ChatMemoryOptions` contains:

- `Strategy`: `ChatMemoryStrategy`, default `Summarizing`;
- `TargetMessageCount`: positive, default `10`;
- `SummarizationThreshold`: non-negative in general and at least `1` for `Summarizing`, default `5`;
- `MaxInputCharacters`: positive, default `4000`;
- `MaxTitleCharacters`: positive, default `120`;
- `MaxConcurrencyRetries`: configurable default `1`, allowed range `0..3`;
- `SystemPrompt`: non-empty.

Options bind through `AddOptions`, validate on startup, and reject unknown enum values or inconsistent counts. Invalid configuration must stop startup rather than silently select no reduction.

MongoDB uses host port `27018` mapped to container port `27017` to avoid silently connecting to a common native MongoDB installation. Ollama remains on `11434`.

A repository-local `global.json` selects .NET 9 with feature-band roll-forward so an ancestor SDK policy cannot silently select an unsupported SDK. Every test project declares `IsTestProject` explicitly, making missing restore assets or incompatible SDK selection fail closed instead of returning a successful zero-test run.

The optional OllamaSharp source generator is removed through a narrowly targeted `Directory.Build.targets` entry because the sample does not consume it and it is incompatible with the compiler supplied by the documented .NET 9 SDK. No other analyzer is suppressed beyond the deliberate experimental `MEAI001` warning.

Default logging is `Warning`; project code emits only warnings and errors, never routine information/debug/trace messages.

## HTTP API

The API remains an ASP.NET Core Minimal API. Every boundary payload is an explicit record.

### Health

`GET /api/health` returns `HealthResponse("ok")`.

### Create

`POST /api/conversations` returns `ConversationCreatedResponse`. A missing or blank title uses `"New conversation"`; a title longer than `MaxTitleCharacters` returns typed `400`.

### Send

`POST /api/conversations/{id}/messages` returns `SendMessageResponse`:

- `Reply`;
- `StoredMessageCount`: persisted user and assistant messages after the successful turn;
- `MessagesSentToModel`: exact number of prompt messages passed to the answer model;
- `PromptPreview`: role/text excerpts, each truncated to 80 characters;
- `SummaryRefreshed`: whether this request generated a new durable summary.

Error responses use `ErrorResponse`:

- `400` for blank or oversized text;
- `404` for an invalid or missing conversation id;
- `409` after exhausted optimistic concurrency retries.

### Transcript

`GET /api/conversations/{id}?limit=20&beforeSequence=<n>` returns a `ConversationResponse` containing metadata, flattened messages for the selected turn page, `StoredMessageCount`, and an optional `NextBeforeSequence` cursor. `limit` is a turn count, defaults to `20`, and is capped at `100`; `beforeSequence` is exclusive. Returned messages remain chronological within each page.

## Tests

Implementation follows red-green-refactor.

### Unit tests

- no-reduction and counting prompt behavior;
- first summary creation;
- checkpoint reuse without a second summary call;
- refresh only after the configured threshold;
- summary cutoff never splits a user/assistant turn;
- service recreation using an existing checkpoint;
- conflict reload/regenerate success;
- retry exhaustion mapped to conflict;
- cancellation and model failure persist no turn;
- invalid strategies and numeric options fail validation;
- explicit response metrics and preview truncation.

### Integration tests

A separate `ChatMemory.IntegrationTests` project uses `WebApplicationFactory` and an isolated MongoDB test container. Ollama is replaced at the `IChatClient` boundary by a deterministic fake.

Integration coverage includes:

- index initialization and uniqueness;
- Mongo LINQ prompt-state reads;
- checkpoint survival across API-host recreation;
- concurrent HTTP requests yielding consecutive stored sequences after retry;
- typed `400`, `404`, and `409` response shapes;
- transcript pagination and chronological flattening;
- cancellation without persistence.

The README distinguishes fast unit tests from Docker-backed integration tests. The full documented validation path starts Docker before the complete solution test command.

## Blog and visual alignment

The paired article files keep their existing slug, metadata, block order, fourteen paired emoji headings, first-person-singular voice, and technical non-marketing tone.

Both languages are rewritten to state:

- completed pairs are immutable turn documents rather than an embedded array;
- separate documents remove the 16 MiB conversation-document failure mode but do not create a retention policy;
- the checkpoint is application-owned and durable across requests/restarts;
- concurrent turns use unique sequences plus one reload/regenerate retry;
- `storedMessageCount`, `messagesSentToModel`, `promptPreview`, and `summaryRefreshed` have precise semantics;
- previews are truncated diagnostics, not the exact complete prompt;
- unit and Mongo/HTTP integration tests have different infrastructure requirements;
- shell commands are copied without prompt characters and pin documented versions;
- MongoDB is published on `27018`.

With defaults `target=10` and `threshold=5`, the first refresh after eight completed prior turns must report `18 stored -> 13 sent`: eighteen user/assistant messages exist after the successful ninth turn, while the model receives system + summary + eleven retained messages. A deterministic test locks this calculation, and the final live audit must reproduce it before the value appears in article copy or SVGs.

Visual changes:

- rewrite the Italian reduction and terminal SVGs;
- add English `reduction-strategies-en.svg` and `terminal-stats-en.svg`;
- translate visible text, titles, and accessibility labels;
- remove every `23 -> 6`, unbounded-array, and constant-ceiling claim.

Site corrections included because they directly affect this article:

- add opaque inline-code parsing and semantic `<code>` rendering;
- use canonical `/blog/...` identities for English curated links;
- emit HowTo fragment URLs only when a step references an existing localized heading;
- add focused bilingual content, renderer, JSON-LD, and SVG tests.

## Verification and acceptance

Backend acceptance requires:

- clean restore/build under .NET SDK 9 with no warnings;
- test discovery executes the expected unit and integration assemblies rather than accepting a zero-test success;
- unit and Docker-backed integration tests passing with no skips;
- `dotnet format --verify-no-changes`;
- package vulnerability scan;
- fresh-clone Compose startup, init completion, real Ollama response, negative HTTP paths, concurrency exercise, transcript pagination, checkpoint reuse, and backing MongoDB inspection;
- no audit-created containers, volumes, processes, or database records left behind.

Blog acceptance requires:

- focused new tests passing before the implementation is generalized;
- full `yarn test`, lint/type checks supported by the repository, and serialized `yarn build`;
- XML validation for all four SVGs;
- rendered EN/IT route checks for one H1, paired heading hierarchy, localized images, internal links, inline `<code>`, valid HowTo fragments, responsive overflow, and no console errors;
- final source and rendered claims matching the audited backend behavior.

Both Git worktrees must remain uncommitted, with unrelated user changes preserved and final status/diffs reported separately.

## Non-goals

- No deployment or production data migration.
- No controller conversion, frontend redesign, authentication, streaming, semantic/vector memory, or automatic retention policy.
- No distributed lease or MongoDB replica-set transaction.
- No compatibility promise for the current unpublished response-field names or embedded-array schema.
