# chat-memory-llm-dotnet-mongodb

Conversational memory for LLM chats in ASP.NET Core. A Minimal API stores every
completed chat turn in **MongoDB** and keeps the model's context window under
control with the experimental **chat reduction** API of `Microsoft.Extensions.AI`
(`IChatReducer`, `MessageCountingChatReducer`, `SummarizingChatReducer`). The
model runs locally in **Ollama**.

The store keeps the *full* transcript. The model sees a *reduced* view: the last
N messages (counting), or a durable summary of the older turns plus the recent
tail (summarizing). Every reply reports how many messages are stored and how
many the model received.

📖 Companion article (IT): https://cool-solution.com/blog/chat-memory-llm-dotnet-mongodb
📖 Companion article (EN): https://cool-solution.com/en/blog/chat-memory-llm-dotnet-mongodb

## How it works

- **One document per turn.** A `Conversation` document holds only `Id`, `Title`
  and `CreatedAt`. Each completed user/assistant pair is one immutable
  `ConversationTurn` document in the `turns` collection. No array grows inside
  one document.
- **Unique sequence.** A unique index on `(ConversationId, Sequence)` makes two
  turns with the same sequence impossible, also across API processes.
- **Durable summary.** When a request refreshes the summary, the summary text
  and its cutoff (`SummarizedThroughSequence`) are stored inside that turn
  document as `CheckpointCreated`. Later requests, and a restarted process,
  reuse the stored summary and load only the turns after the cutoff.
- **Nothing partial.** The user message, the answer, and a new checkpoint are
  written in one insert, after the model answered. A failed or cancelled model
  call stores nothing.

Separate turn documents remove the 16 MiB limit of a single conversation
document. They do **not** add a retention policy: this sample has no TTL, no
archive and no deletion. Decide those for production.

## Technologies

- .NET 9 · ASP.NET Core Minimal API (`global.json` selects the .NET 9 SDK)
- Microsoft.Extensions.AI 10.9.0 (`IChatClient`, `IChatReducer` — experimental `MEAI001`)
- OllamaSharp 5.4.30 (Ollama as `IChatClient`)
- MongoDB.Driver 3.11.0 (LINQ reads, insert-only writes)
- Ollama + `llama3.2:3b` (local model)
- xunit 2.9.2 · Microsoft.AspNetCore.Mvc.Testing 9.0.19 · Testcontainers.MongoDb 4.14.0
- Docker Compose (MongoDB + Ollama)

## Prerequisites

- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Docker](https://docs.docker.com/get-docker/) with Docker Compose, running
- Free host ports `27018` (MongoDB), `11434` (Ollama) and `5210` (API)

## 1. Start MongoDB and Ollama

```bash
docker compose up -d
```

- `mongodb` — MongoDB 7 on `localhost:27018`. Data is in the `mongo-data` volume.
  The host port is `27018`, not `27017`, so the API never connects by mistake to
  a MongoDB that is installed on the host.
- `ollama` — Ollama API on `localhost:11434`. Models are in the `ollama-data` volume.
- `ollama-init` — runs once, pulls `llama3.2:3b` (about 2 GB), then exits.

The first pull takes time; the duration depends on your connection. Wait until
`ollama-init` has finished:

```bash
docker compose logs -f ollama-init
```

```bash
docker compose ps -a
```

`ollama-init` must show `Exited (0)`, and `mongodb` and `ollama` must show `healthy`.

If Docker reports `port is already allocated`, another process uses `27018` or
`11434`. Stop that process, or change the left side of the port mapping in
`docker-compose.yml` and the matching value in `src/ChatMemory.Api/appsettings.json`.

## 2. Start the API

```bash
dotnet run --project src/ChatMemory.Api
```

The API listens on `http://localhost:5210` (see `Properties/launchSettings.json`).
On startup it creates the unique index on the `turns` collection.

```bash
curl -s http://localhost:5210/api/health
```

Expected: `{"status":"ok"}`.

## 3. Try it

Create a conversation and keep its id:

```bash
ID=$(curl -s -X POST http://localhost:5210/api/conversations -H "Content-Type: application/json" -d '{"title":"Trip planning"}' | sed -E 's/.*"id":"([^"]+)".*/\1/')
```

```bash
echo "$ID"
```

Send one message:

```bash
curl -s -X POST "http://localhost:5210/api/conversations/$ID/messages" -H "Content-Type: application/json" -d '{"text":"I am planning a 3-day trip to Rome in October. Suggestions?"}'
```

The response:

```json
{
  "reply": "…",
  "storedMessageCount": 2,
  "messagesSentToModel": 2,
  "promptPreview": [
    "system: You are a helpful, concise assistant. Answer in the language the user writes in.",
    "user: I am planning a 3-day trip to Rome in October. Suggestions?"
  ],
  "summaryRefreshed": false
}
```

| Field                 | Meaning |
| --------------------- | ------- |
| `reply`               | The answer of the model. |
| `storedMessageCount`  | User and assistant messages stored in MongoDB after this turn. |
| `messagesSentToModel` | Exact number of messages in the prompt that the answer model received. |
| `promptPreview`       | One `role: text` line per prompt message. Each text is cut at 80 characters. It is a diagnostic, not the full prompt. |
| `summaryRefreshed`    | `true` when this request generated a new summary. |

Read the transcript. `limit` counts turns (default `20`, maximum `100`):

```bash
curl -s "http://localhost:5210/api/conversations/$ID?limit=2"
```

The response holds `messages` (oldest first inside the page), `storedMessageCount`,
`updatedAt` and `nextBeforeSequence`. To read the older page, pass that value as
`beforeSequence`. The cursor is exclusive. `nextBeforeSequence` is `null` on the
last page.

```bash
curl -s "http://localhost:5210/api/conversations/$ID?limit=2&beforeSequence=4"
```

`requests.http` has the same requests for the REST client of your IDE.

### Watch the summary start

With the default settings (`TargetMessageCount` 10, `SummarizationThreshold` 5),
send messages until the conversation has nine turns:

```bash
for i in 2 3 4 5 6 7 8 9; do curl -s -X POST "http://localhost:5210/api/conversations/$ID/messages" -H "Content-Type: application/json" -d "{\"text\":\"Give me travel tip number $i for Rome, in one sentence.\"}"; echo; done
```

| Turn | `storedMessageCount` | `messagesSentToModel` | `summaryRefreshed` |
| ---- | -------------------- | --------------------- | ------------------ |
| 1    | 2                    | 2                     | false              |
| 8    | 16                   | 16                    | false              |
| 9    | 18                   | 13                    | true               |
| 10   | 20                   | 15                    | false              |
| 11   | 22                   | 17                    | false              |
| 12   | 24                   | 13                    | true               |

On turn 9 the unsummarized history is 17 messages, more than 10 + 5. The model
summarizes turns 1–3. The answer model then receives 13 messages: the system
prompt, the summary, and 11 retained messages (turns 4–8 and the new question).
Turns 10 and 11 reuse the stored summary with no summarization call.

## Memory strategies

Set `ChatMemory:Strategy` in `src/ChatMemory.Api/appsettings.json`, or use an
environment variable:

```bash
ChatMemory__Strategy=Counting dotnet run --project src/ChatMemory.Api
```

| Strategy      | What the model receives | What the API loads from MongoDB | Extra cost |
| ------------- | ----------------------- | ------------------------------- | ---------- |
| `None`        | System prompt + the whole transcript. A measurement baseline: the prompt grows without limit. | Every turn | none |
| `Counting`    | System prompt + the last `TargetMessageCount` messages. Older messages are not sent. | Only the newest turns that fit the target | none |
| `Summarizing` | System prompt + stored summary + the turns after the summary. | The newest checkpoint and the turns after it | One model call each time the summary is refreshed |

The summary is refreshed only when the unsummarized messages (stored tail plus
the new user message) are more than `TargetMessageCount + SummarizationThreshold`.
The cut is always placed before a user message, so a user/assistant pair is
never split.

Other settings in the `ChatMemory` section:

| Setting                  | Default | Rule |
| ------------------------ | ------- | ---- |
| `TargetMessageCount`     | 10      | greater than 0 |
| `SummarizationThreshold` | 5       | at least 1 for `Summarizing`, at least 0 otherwise |
| `MaxInputCharacters`     | 4000    | greater than 0 |
| `MaxTitleCharacters`     | 120     | greater than 0 |
| `MaxConcurrencyRetries`  | 1       | 0 to 3 |
| `SystemPrompt`           | (text)  | not blank |

An invalid value stops the API at startup.

## Errors

The errors that the API handlers produce have the body `{ "message": "…" }`:

| Status | When |
| ------ | ---- |
| `400`  | Blank or too long `text`; too long `title`; `limit` or `beforeSequence` below 1. |
| `404`  | The conversation id is invalid or unknown. |
| `409`  | Other requests took the turn sequence on every attempt. Send the message again. |

Two cases do not have this body. ASP.NET Core rejects a missing or malformed JSON
body, or a non-numeric `limit`, with its own `400` before the handler runs. A
failed model call (for example Ollama is not running) returns `500`, and no
turn is stored.

## Concurrent turns

Two requests for the same conversation can run at the same time. Both read the
same state and both ask the model. Only one can store sequence `n + 1`; MongoDB
rejects the other insert through the unique index. The API then discards the
stale answer, reloads the conversation, and asks the model again
(`MaxConcurrencyRetries`, default `1`). If the retry also loses, the API returns `409`.

Trade-off: real contention costs one extra model call. In return there is no
lock, no lease and no MongoDB transaction, and it works across API processes.

## Inspect MongoDB

```bash
docker compose exec mongodb mongosh chatmemory --quiet --eval 'db.turns.find({}, {UserText: 0, AssistantText: 0}).sort({Sequence: 1}).toArray()'
```

```bash
docker compose exec mongodb mongosh chatmemory --quiet --eval 'db.turns.getIndexes()'
```

## Tests

Unit tests need neither Docker nor Ollama. They use an in-memory repository and
a scripted `IChatClient`:

```bash
dotnet test tests/ChatMemory.Tests
```

Integration tests need a running Docker. Testcontainers starts a temporary
`mongo:7` container; `WebApplicationFactory` starts the real API over it. Ollama
is replaced by a deterministic `IChatClient`:

```bash
dotnet test tests/ChatMemory.IntegrationTests
```

The whole solution (Docker required):

```bash
dotnet test ChatMemory.sln
```

One deterministic test at each level locks the example above: after eight
completed turns, the ninth reports 18 stored messages and 13 sent to the model.

## Stop and clean up

Stop the containers and keep the volumes (model and data survive):

```bash
docker compose down
```

Stop the containers and delete the volumes:

```bash
docker compose down -v
```

## Project layout

```
src/ChatMemory.Api/
  Program.cs                             # DI wiring, options validation, index initializer
  Endpoints/ConversationEndpoints.cs     # Minimal API handlers with typed results
  Services/ChatService.cs                # load state → reduce → ask → insert turn, with retry
  Services/ConversationMemoryReducer.cs  # strategies and the durable summary checkpoint
  Data/ConversationRepository.cs         # LINQ reads, insert-only writes
  Data/ConversationIndexInitializer.cs   # unique (ConversationId, Sequence) index
  Models/ · Contracts/ · Options/
tests/ChatMemory.Tests/                  # unit tests, no Docker
tests/ChatMemory.IntegrationTests/       # MongoDB (Testcontainers) + HTTP tests
docker-compose.yml
```

## License

MIT
