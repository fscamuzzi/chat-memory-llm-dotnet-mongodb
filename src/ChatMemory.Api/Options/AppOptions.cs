using ChatMemory.Api.Models;

namespace ChatMemory.Api.Options;

public sealed class MongoOptions
{
    public const string Section = "Mongo";

    public string ConnectionString { get; set; } = "mongodb://localhost:27018";

    public string Database { get; set; } = "chatmemory";
}

public sealed class OllamaOptions
{
    public const string Section = "Ollama";

    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "llama3.2:3b";

    /// <summary>
    /// Longest wait for one model call, in seconds. Range 1..3600.
    /// Raise it when Ollama runs on CPU only, for example in Docker on macOS.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 100;
}

public sealed class ChatMemoryOptions
{
    public const string Section = "ChatMemory";

    /// <summary>None | Counting | Summarizing.</summary>
    public ChatMemoryStrategy Strategy { get; set; } = ChatMemoryStrategy.Summarizing;

    /// <summary>How many recent (non-system) messages survive the reduction.</summary>
    public int TargetMessageCount { get; set; } = 10;

    /// <summary>
    /// Extra messages tolerated past the target before the summary is refreshed.
    /// Avoids a summarization call on every turn. Must be at least 1 for Summarizing.
    /// </summary>
    public int SummarizationThreshold { get; set; } = 5;

    /// <summary>Longest accepted user message, in characters.</summary>
    public int MaxInputCharacters { get; set; } = 4000;

    /// <summary>Longest accepted conversation title, in characters.</summary>
    public int MaxTitleCharacters { get; set; } = 120;

    /// <summary>
    /// How many times a turn is regenerated after another request took its sequence. Range 0..3.
    /// </summary>
    public int MaxConcurrencyRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } =
        "You are a helpful, concise assistant. Answer in the language the user writes in.";
}
