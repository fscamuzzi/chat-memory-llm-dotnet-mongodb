namespace ChatMemory.Api.Options;

public sealed class MongoOptions
{
    public const string Section = "Mongo";

    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    public string Database { get; set; } = "chatmemory";
}

public sealed class OllamaOptions
{
    public const string Section = "Ollama";

    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "llama3.2:3b";
}

public sealed class ChatMemoryOptions
{
    public const string Section = "ChatMemory";

    /// <summary>none | counting | summarizing</summary>
    public string Strategy { get; set; } = "summarizing";

    /// <summary>How many recent (non-system) messages survive the reduction.</summary>
    public int TargetMessageCount { get; set; } = 10;

    /// <summary>
    /// Extra messages tolerated past the target before the summarizing reducer
    /// actually summarizes. Avoids re-summarizing on every single turn.
    /// </summary>
    public int? SummarizationThreshold { get; set; } = 5;

    public string SystemPrompt { get; set; } =
        "You are a helpful, concise assistant. Answer in the language the user writes in.";
}
