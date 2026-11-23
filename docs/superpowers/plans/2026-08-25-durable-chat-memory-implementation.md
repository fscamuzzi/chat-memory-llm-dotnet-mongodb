# Durable Chat Memory Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Apply `superpowers:test-driven-development` for every behavior change. Do not commit, push, stage, stash, or restore files.

**Goal:** Make a fresh clone of the .NET 9 sample preserve complete chat transcripts in bounded MongoDB turn documents, reuse durable reducer checkpoints across requests and restarts, serialize concurrent turns optimistically, and expose typed, truthful API diagnostics.

**Architecture:** Keep small `Conversation` metadata documents and immutable `ConversationTurn` documents. Read through Mongo LINQ, use native driver calls only for inserts and index creation, and enforce `(ConversationId, Sequence)` uniqueness. An application-owned `ConversationMemoryReducer` coordinates the official Microsoft reducers and durable checkpoints; `ChatService` retries one stale generation and returns a typed outcome to Minimal API endpoints.

**Tech Stack:** .NET 9, ASP.NET Core Minimal API, Microsoft.Extensions.AI 10.9.0, OllamaSharp 5.4.30, MongoDB.Driver 3.11.0, xUnit, WebApplicationFactory 9.0.19, Testcontainers.MongoDb 4.14.0, Docker Compose.

---

## Task 1: Establish configuration, domain, and boundary contracts

**Files:**

- Create: `global.json`
- Create: `Directory.Build.targets`
- Create: `src/ChatMemory.Api/Models/ChatMemoryStrategy.cs`
- Create: `src/ChatMemory.Api/Models/ConversationTurn.cs`
- Modify: `src/ChatMemory.Api/Models/Conversation.cs`
- Modify: `src/ChatMemory.Api/Options/AppOptions.cs`
- Create: `src/ChatMemory.Api/Options/ChatMemoryOptionsValidator.cs`
- Modify: `src/ChatMemory.Api/Contracts/ChatContracts.cs`
- Create: `tests/ChatMemory.Tests/ChatMemoryOptionsValidatorTests.cs`
- Modify: `tests/ChatMemory.Tests/ChatMemory.Tests.csproj`

### 1.1 Write the failing options tests

- [x] Add one theory covering invalid enum values through configuration binding, non-positive target/input/title limits, summarizing threshold below one, blank system prompt, and retries outside `0..3`.
- [x] Add a passing default-options test.
- [x] Run:

```bash
dotnet test tests/ChatMemory.Tests/ChatMemory.Tests.csproj --filter ChatMemoryOptionsValidatorTests
```

Expected: compile/test failure because the enum and validator do not exist.

### 1.2 Add explicit models and validation

- [x] Add a repo-local `global.json` with SDK `9.0.100`, `rollForward: latestFeature`, and `allowPrerelease: false` so an ancestor SDK policy cannot select .NET 6/10 for this .NET 9 sample.
- [x] Add `<IsTestProject>true</IsTestProject>` to the unit-test project so `dotnet test` fails closed instead of silently skipping tests when package-generated props are unavailable.
- [x] Define `ChatMemoryStrategy { None, Counting, Summarizing }` in its own model file.
- [x] Replace the embedded messages on `Conversation` with only `Id`, `Title`, and `CreatedAt`.
- [x] Add `ConversationTurn`, `ReducerCheckpoint`, and the explicit repository/service enums and records needed later:

```csharp
public sealed class ConversationTurn
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string UserText { get; set; } = string.Empty;
    public string AssistantText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public ReducerCheckpoint? CheckpointCreated { get; set; }
}

public sealed class ReducerCheckpoint
{
    public string SummaryText { get; set; } = string.Empty;
    public long SummarizedThroughSequence { get; set; }
}
```

- [x] Add `MaxInputCharacters = 4000`, `MaxTitleCharacters = 120`, and `MaxConcurrencyRetries = 1`; make the strategy enum default to `Summarizing` and threshold default to `5`.
- [x] Implement `IValidateOptions<ChatMemoryOptions>` with explicit failures and method/step comments required by `base-rules.md`.
- [x] Replace every anonymous HTTP body with explicit records:

```csharp
public sealed record HealthResponse(string Status);
public sealed record ErrorResponse(string Message);
public sealed record SendMessageResponse(
    string Reply,
    int StoredMessageCount,
    int MessagesSentToModel,
    IReadOnlyList<string> PromptPreview,
    bool SummaryRefreshed);
public sealed record ConversationResponse(
    string Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MessageResponse> Messages,
    int StoredMessageCount,
    long? NextBeforeSequence);
```

- [x] Add a narrowly targeted MSBuild target that removes only the OllamaSharp analyzer/source-generator item. Retain only the deliberate `MEAI001` suppression.

### 1.3 Prove the configuration layer

- [x] Rerun the focused tests and expect green.
- [x] Run `dotnet build ChatMemory.sln --no-restore` and expect zero warnings.
- [x] Inspect `git diff --check`; do not commit.

## Task 2: Replace the embedded-array repository with immutable turns

**Files:**

- Modify: `src/ChatMemory.Api/Data/ConversationRepository.cs`
- Create: `src/ChatMemory.Api/Data/ConversationIndexInitializer.cs`
- Modify: `tests/ChatMemory.Tests/Fakes.cs`
- Create: `tests/ChatMemory.Tests/InMemoryConversationRepositoryTests.cs`

### 2.1 Specify repository semantics in tests

- [x] Write tests for create/get, latest state, tail-after-checkpoint, latest-N turns, exclusive backward pagination, chronological page results, successful insert, and duplicate-sequence conflict.
- [x] Keep the fake behavior identical to Mongo behavior; do not weaken tests with fake-only shortcuts.
- [x] Run the focused tests and confirm they fail against the old repository interface.

### 2.2 Introduce behavior-oriented repository types

- [x] Use an interface shaped around the behavior, not raw Mongo primitives:

```csharp
public interface IConversationRepository
{
    Task<Conversation> CreateAsync(string title, CancellationToken ct);
    Task<Conversation?> GetConversationAsync(string id, CancellationToken ct);
    Task<ConversationState?> GetStateAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ConversationTurn>> GetLatestTurnsAsync(
        string conversationId, int limit, CancellationToken ct);
    Task<IReadOnlyList<ConversationTurn>> GetTurnsAfterAsync(
        string conversationId, long sequence, CancellationToken ct);
    Task<InsertTurnResult> InsertTurnAsync(ConversationTurn turn, CancellationToken ct);
    Task<ConversationPage?> GetPageAsync(
        string id, int limit, long? beforeSequence, CancellationToken ct);
}
```

- [x] Make every Mongo read start from `AsQueryable()` and use LINQ operators. Materialize asynchronously through the Mongo LINQ provider.
- [x] Use native driver syntax only for `InsertOneAsync` and index creation.
- [x] Catch only duplicate-key exceptions for the unique sequence and return `SequenceConflict`; allow all other failures to propagate.
- [x] Derive total stored-message count as `turnCount * 2` and `UpdatedAt` from the latest turn, falling back to `CreatedAt`.

### 2.3 Initialize the single required index

- [x] Implement `IHostedService`/startup initializer that creates one unique ascending compound index on `(ConversationId, Sequence)` before traffic is accepted.
- [x] Do not add a redundant descending index; MongoDB can reverse-scan the compound index.
- [x] Rerun focused repository tests, then all unit tests. Expect green.
- [x] Run `rg -n '\.Find\(|Builders<.*>\.Filter|UpdateOne|ReplaceOne' src tests` and expect no Mongo read/update violations.
- [x] Inspect diff; do not commit.

## Task 3: Build durable reduction test-first

**Files:**

- Replace: `src/ChatMemory.Api/Services/ChatReducerFactory.cs`
- Create: `src/ChatMemory.Api/Services/ConversationMemoryReducer.cs`
- Replace: `tests/ChatMemory.Tests/ChatReducerTests.cs`
- Modify: `tests/ChatMemory.Tests/Fakes.cs`

### 3.1 Write reducer behavior tests

- [x] Cover `None`: system + all raw completed turns + current user.
- [x] Cover `Counting`: repository loads only the necessary latest turns and the official `MessageCountingChatReducer` preserves the system message and configured recent non-system messages.
- [x] Cover first summary creation, persisted checkpoint content/cutoff, complete-pair cutoff, and a prompt containing system + summary + retained tail.
- [x] Cover checkpoint reuse under threshold with zero summarizer calls.
- [x] Cover refresh only once the configured `TargetMessageCount + SummarizationThreshold` tail is exceeded.
- [x] Cover reducer recreation with a persisted checkpoint.
- [x] Lock the documented default metric: after eight prior completed turns, the ninth answer request produces `18` stored messages and sends exactly `13` messages (`system + summary + 11 retained`) to the answer model.
- [x] Assert no code or serialized state depends on `__summary__`.
- [x] Run focused tests and verify red.

### 3.2 Implement the durable adapter

- [x] Define:

```csharp
public sealed record ReductionResult(
    IReadOnlyList<ChatMessage> Prompt,
    ReducerCheckpoint? NewCheckpoint,
    bool SummaryRefreshed);
```

- [x] Keep durable threshold/checkpoint ownership in `ConversationMemoryReducer`.
- [x] For first/refresh summarization, call the official `SummarizingChatReducer` using configured target and threshold. Supply a prior checkpoint as an assistant message prefixed `Previous summary:` plus the eligible raw tail.
- [x] Map retained original `ChatMessage` instances back to source turn sequences and advance `SummarizedThroughSequence` only through a complete user/assistant pair.
- [x] Store only application-owned summary text/cutoff; never inspect or persist reducer `AdditionalProperties` or private keys.
- [x] Use LINQ and functional transformations instead of index/foreach loops where practical; use early returns and base-rule comments.

### 3.3 Prove reducer behavior

- [x] Run the focused reducer tests twice to expose state leakage.
- [x] Run all unit tests.
- [x] Search for stale `ChatReducerFactory`, private summary marker usage, and string strategy switches; expect none outside negative tests/docs.
- [x] Inspect diff; do not commit.

## Task 4: Serialize chat turns and expose typed outcomes

**Files:**

- Modify: `src/ChatMemory.Api/Services/ChatService.cs`
- Replace: `tests/ChatMemory.Tests/ChatServiceTests.cs`
- Modify: `tests/ChatMemory.Tests/Fakes.cs`

### 4.1 Write service tests before implementation

- [x] Test successful persistence and all response metrics, including 80-character preview truncation.
- [x] Test unknown/invalid conversation outcome without a model call.
- [x] Test model failure and cancellation persist no partial turn/checkpoint.
- [x] Test one sequence conflict discards the stale response, reloads, regenerates, persists the retry, and returns only the persisted reply.
- [x] Test a second conflict returns a typed `Conflict` outcome and never returns either stale reply.
- [x] Test `MaxConcurrencyRetries = 0` performs no retry.
- [x] Run focused tests and verify red.

### 4.2 Implement the retrying service

- [x] Return an explicit service result instead of `null`:

```csharp
public enum SendMessageStatus
{
    Succeeded,
    ConversationNotFound,
    Conflict,
}

public sealed record SendMessageResult(
    SendMessageStatus Status,
    SendMessageResponse? Response = null);
```

- [x] Per attempt: load state/tail, reduce, call the answer model, construct sequence `latest + 1`, and atomically insert the completed turn with any newly generated checkpoint.
- [x] On sequence conflict, discard all stale response data and regenerate from reloaded state up to the configured retry count.
- [x] Never persist a user-only message or a checkpoint if the answer call fails/cancels.
- [x] Build preview entries from the exact answer-model prompt and truncate text to 80 characters plus an ellipsis.
- [x] Use only warning/error logging; routine success paths remain silent.

### 4.3 Prove service behavior

- [x] Run focused service tests, then the full unit suite.
- [x] Run tests with `--blame-hang-timeout 2m` to detect deadlocks.
- [x] Inspect diff; do not commit.

## Task 5: Keep the HTTP surface Minimal and explicit

**Files:**

- Modify: `src/ChatMemory.Api/Endpoints/ConversationEndpoints.cs`
- Modify: `src/ChatMemory.Api/Program.cs`
- Modify: `src/ChatMemory.Api/appsettings.json`
- Create: `tests/ChatMemory.Tests/EndpointValidationTests.cs`

### 5.1 Write boundary validation tests

- [x] Add pure endpoint/validation tests for blank/oversized title, blank/oversized message, invalid object id, transcript limit default/max, and non-positive cursor.
- [x] Verify all errors use `ErrorResponse` and exact `400/404/409` mappings.
- [x] Run focused tests and verify red.

### 5.2 Implement explicit Minimal API endpoints

- [x] Keep route handlers in `ConversationEndpoints`; do not add controllers.
- [x] Normalize missing/blank title to `New conversation`; reject nonblank titles beyond the configured maximum.
- [x] Map `SendMessageStatus` explicitly to `200`, `404`, or `409` with typed response records.
- [x] Implement `GET /api/conversations/{id}?limit=20&beforeSequence=n`; treat `limit` as turns, cap at `100`, and make the cursor exclusive.
- [x] Return flattened user/assistant messages chronologically within each page, total stored message count, and the next cursor only when another page exists.
- [x] Return `HealthResponse("ok")` from `/api/health`.
- [x] Bind all option groups with `AddOptions().Bind(...).ValidateOnStart()` and register the custom validator.
- [x] Initialize indexes before `app.Run()` and expose `public partial class Program;` for WebApplicationFactory.
- [x] Set default application logging to `Warning`.

### 5.3 Prove endpoint composition

- [x] Run focused endpoint tests and all unit tests.
- [x] Run `dotnet build ChatMemory.sln --no-restore` and require zero warnings.
- [x] Search for `new {`, `Controller`, `MapControllers`, and Information/Debug/Trace project logs; expect none.
- [x] Inspect diff; do not commit.

## Task 6: Add real MongoDB/HTTP integration coverage

**Files:**

- Create: `tests/ChatMemory.IntegrationTests/ChatMemory.IntegrationTests.csproj`
- Create: `tests/ChatMemory.IntegrationTests/MongoApiFactory.cs`
- Create: `tests/ChatMemory.IntegrationTests/DeterministicChatClient.cs`
- Create: `tests/ChatMemory.IntegrationTests/ConversationApiTests.cs`
- Create: `tests/ChatMemory.IntegrationTests/RepositoryIntegrationTests.cs`
- Modify: `ChatMemory.sln`

### 6.1 Scaffold the isolated integration project

- [x] Reference the API project and pin `Microsoft.AspNetCore.Mvc.Testing` `9.0.19`, `Testcontainers.MongoDb` `4.14.0`, the existing xUnit packages, and a compatible Microsoft.NET.Test.Sdk.
- [x] Start one MongoDB container per test collection/fixture with an isolated database name.
- [x] Replace the registered `IChatClient` with a deterministic, call-recording fake; never require Ollama for automated integration tests.
- [x] Run the empty project and prove Docker/Testcontainers can start.

### 6.2 Write failing end-to-end tests

- [x] Verify startup creates the unique compound index and duplicate sequence insertion returns conflict.
- [x] Verify repository state/tail/page reads against Mongo and inspect generated behavior through results, not internal private methods.
- [x] Verify checkpoint persistence survives disposing/recreating the API host against the same test database.
- [x] Send concurrent HTTP requests and assert consecutive sequences; configure a deterministic conflict schedule so one request reloads/regenerates.
- [x] Verify typed `400`, `404`, and exhausted-retry `409` JSON bodies.
- [x] Verify exclusive pagination, chronological flattening, total count, and next cursor.
- [x] Verify cancellation/model failure leaves no turn.
- [x] Run focused integration tests and verify red before filling any missing production behavior.

### 6.3 Make integration tests green without test-only production branches

- [x] Fix only observable production gaps exposed by the tests.
- [x] Run:

```bash
dotnet test tests/ChatMemory.IntegrationTests/ChatMemory.IntegrationTests.csproj --no-restore --blame-hang-timeout 3m
dotnet test ChatMemory.sln --no-restore --blame-hang-timeout 3m
```

Expected: all tests pass, no skips, no warnings, and Testcontainers removes its containers.

- [x] Inspect `docker ps -a` for audit leftovers; remove only containers created by this plan if cleanup failed.
- [x] Inspect diff; do not commit.

## Task 7: Align runtime configuration and README

**Files:**

- Modify: `docker-compose.yml`
- Modify: `src/ChatMemory.Api/appsettings.json`
- Modify: `README.md`
- Modify: `requests.http`

### 7.1 Write documentation/runtime consistency checks

- [x] Search README/Compose/settings/requests for `27017`, shell prompt characters, obsolete response fields, embedded-array claims, and commands that omit integration prerequisites.
- [x] Record expected failures before editing.

### 7.2 Correct the runnable path

- [x] Publish MongoDB as host `27018:27017` and configure `mongodb://localhost:27018`.
- [x] Keep Ollama on `11434`; document model-init status and the first-pull delay without promising a fixed duration.
- [x] Document exact prerequisites, startup order, health check, create/send/transcript requests, counting/summarizing/none semantics, typed errors, concurrency retry trade-off, pagination, and cleanup.
- [x] Distinguish unit tests (no Docker) from integration/full-solution tests (Docker required).
- [x] Explain that separate turn documents avoid the single 16 MiB conversation-document limit but do not implement TTL, archive, or retention.
- [x] Use copyable commands without `$` prompt characters.
- [x] Update `requests.http` to the final response fields and cursor route.

### 7.3 Verify static consistency

- [x] Run targeted `rg` checks for stale fields/ports/claims.
- [x] Run `docker compose config` and expect success.
- [x] Inspect diff; do not commit.

## Task 8: Run the fresh-clone and full audit gates

**Files:**

- Verify only; fix the owning task if a gate fails.

### 8.1 Static and package gates

- [x] Capture final `git status --short`, `git diff --check`, and a diff hash before verification.
- [x] Run:

```bash
dotnet restore ChatMemory.sln
dotnet build ChatMemory.sln --no-restore --warnaserror
dotnet test tests/ChatMemory.Tests/ChatMemory.Tests.csproj --no-build
dotnet test tests/ChatMemory.IntegrationTests/ChatMemory.IntegrationTests.csproj --no-build --blame-hang-timeout 3m
dotnet test ChatMemory.sln --no-build --blame-hang-timeout 3m
dotnet format ChatMemory.sln --verify-no-changes --no-restore
dotnet list ChatMemory.sln package --vulnerable --include-transitive
docker compose config
```

Expected: zero warnings, no skipped/failed tests, format clean, no known vulnerable packages, valid Compose.

### 8.2 Independent clean-directory reproduction

- [x] Copy/export the current uncommitted tree to a temporary directory without `.git`, `bin`, or `obj`; do not mutate the working repository.
- [ ] Follow README commands exactly from that clean directory. _(2026-11-23: partial — README commands used, with `Mongo__ConnectionString` and `Ollama__Model` overrides; see next step.)_
- [ ] Start Compose and wait for Mongo/Ollama/model-init health/completion with bounded deadlines. _(2026-11-23: partial — only `mongodb`, on temporary host port 27028, because 27018 and 11434 were in use on the audit machine. Compose `ollama` and `ollama-init` were not started; the host Ollama with `llama3.2:latest` answered.)_
- [x] Start the API on a known free local port and verify health, create, negative validation, missing id, transcript pagination, and concurrent sends.
- [x] Drive enough deterministic/live turns to observe checkpoint creation, reuse after API restart, and the documented `18 stored -> 13 sent` metric.
- [x] Inspect MongoDB to confirm metadata and immutable turn/checkpoint documents, monotonic sequences, and the unique compound index.

### 8.3 Cleanup and final-state audit

- [x] Stop only API processes started by this audit.
- [x] Run `docker compose down -v` only in the temporary audit copy to remove only its containers/network/volumes.
- [x] Confirm no audit-created process, container, volume, or database remains.
- [x] Recompute status/diff hash; if the worktree changed during verification, rerun affected gates.
- [x] Run final base-rules review and resolve all confirmed P1/P2 findings.
- [x] Leave every change uncommitted and report the backend diff separately from the blog diff.
