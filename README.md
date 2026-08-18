# chat-memory-llm-dotnet-mongodb

Conversational memory for LLM chats in ASP.NET Core: a Minimal API that persists
every conversation turn in **MongoDB** and keeps the model's context window under
control with the experimental **chat reduction** API of `Microsoft.Extensions.AI`
(`IChatReducer`, `MessageCountingChatReducer`, `SummarizingChatReducer`), running
fully local against **Ollama**.

The store keeps the *full* transcript; the model only sees the *reduced* view —
either the last N messages (counting) or an LLM-generated summary of the older
ones plus the recent tail (summarizing). Every reply reports how many messages
the history holds vs how many actually reached the model.

📖 Companion article (IT): https://cool-solution.com/blog/chat-memory-llm-dotnet-mongodb
📖 Companion article (EN): https://cool-solution.com/en/blog/chat-memory-llm-dotnet-mongodb

## Technologies

- .NET 9 · ASP.NET Core Minimal API
- Microsoft.Extensions.AI 10.9.0 (`IChatClient`, `IChatReducer` — experimental `MEAI001`)
- OllamaSharp 5.4.30 (Ollama as `IChatClient`)
- MongoDB.Driver 3.11.0 (partial `$push` updates, no document replacement)
- Ollama + `llama3.2:3b` (local model)
- xunit (unit tests with a scripted fake `IChatClient`)
- Docker Compose (MongoDB + Ollama, zero local installs)

## Prerequisites

- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Docker](https://docs.docker.com/get-docker/) (only external dependency)

## Run dependencies with Docker

Everything external (MongoDB, Ollama, model pull) runs in Docker:

```bash
docker compose up -d
```

Per-service notes:

- `mongodb` — MongoDB 7 on `localhost:27017`, data persisted in the `mongo-data` volume.
- `ollama` — Ollama API on `localhost:11434`, models persisted in `ollama-data`.
- `ollama-init` — one-shot container that pulls `llama3.2:3b` (~2 GB) and exits.
  First run takes a few minutes; check progress with
  `docker logs -f chatmemory-ollama-init`. To pull manually instead:
  `docker compose exec ollama ollama pull llama3.2:3b`.

Stop and clean up:

```bash
docker compose down        # stop, keep volumes (models + data survive)
docker compose down -v     # stop and delete volumes too
```

## Run the API

```bash
dotnet run --project src/ChatMemory.Api
```

The API listens on `http://localhost:5210` (see `Properties/launchSettings.json`).
Configuration lives in `src/ChatMemory.Api/appsettings.json` and already points
at the compose services (`localhost:27017`, `localhost:11434`).

### Try it

```bash
# 1. Create a conversation
curl -s -X POST http://localhost:5210/api/conversations \
  -H "Content-Type: application/json" -d '{"title":"Trip planning"}'

# 2. Chat (use the returned id)
curl -s -X POST http://localhost:5210/api/conversations/<id>/messages \
  -H "Content-Type: application/json" \
  -d '{"text":"I am planning a 3-day trip to Rome in October. Suggestions?"}'

# 3. Inspect the persisted transcript
curl -s http://localhost:5210/api/conversations/<id>
```

Each reply includes `messagesInHistory`, `messagesSentToModel` and a
`promptPreview` so you can watch the reducer kick in as the conversation grows.

### Memory strategies

Switch strategy in `appsettings.json` → `ChatMemory:Strategy`:

| Strategy      | What the model sees                                        | Extra cost |
| ------------- | ---------------------------------------------------------- | ---------- |
| `none`        | The whole transcript, always                               | none       |
| `counting`    | System prompt + last `TargetMessageCount` messages         | none       |
| `summarizing` | Summary of older turns + recent tail (`SummarizationThreshold` controls how often it re-summarizes) | 1 LLM call when it triggers |

## Tests

```bash
dotnet test
```

Unit tests cover both reducers and the chat service (persistence + reduction
stats) using a scripted fake `IChatClient` — no MongoDB or Ollama needed.

## Project layout

```
src/ChatMemory.Api/
  Program.cs                    # DI wiring: Mongo, Ollama IChatClient, reducer
  Endpoints/                    # Minimal API endpoints
  Services/ChatService.cs       # load → reduce → ask → persist
  Services/ChatReducerFactory.cs
  Data/ConversationRepository.cs
  Models/ · Contracts/ · Options/
tests/ChatMemory.Tests/
docker-compose.yml
```

## License

MIT
