using ChatMemory.Api.Options;

namespace ChatMemory.Api.Services;

// Builds the HttpClient that OllamaSharp uses for answers and summaries.
public static class OllamaHttpClientFactory
{
    // Point the client at the configured endpoint and apply the configured timeout.
    // A CPU-only Ollama (for example in Docker on macOS) can need more than the default 100 seconds.
    public static HttpClient Create(OllamaOptions options) =>
        new()
        {
            BaseAddress = new Uri(options.Endpoint),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
}
