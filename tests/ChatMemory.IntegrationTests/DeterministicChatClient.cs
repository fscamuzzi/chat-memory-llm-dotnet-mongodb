using Microsoft.Extensions.AI;

namespace ChatMemory.IntegrationTests;

// Stands in for Ollama. Replies are a pure function of the request, and every call is recorded.
// A summary request ends with a system message (the official reducer's shape); an answer prompt never does.
public sealed class DeterministicChatClient : IChatClient
{
    private readonly object gate = new();
    private readonly List<IReadOnlyList<ChatMessage>> answerCalls = [];
    private int summaryCalls;

    // Runs before every answer is produced. Use it to block, fail, or wait for cancellation.
    public Func<CancellationToken, Task>? BeforeAnswer { get; set; }

    public int SummaryCallCount => Volatile.Read(ref summaryCalls);

    // A snapshot of the answer prompts, in arrival order.
    public IReadOnlyList<IReadOnlyList<ChatMessage>> AnswerCalls
    {
        get
        {
            lock (gate)
                return answerCalls.ToList();
        }
    }

    // Reply "SUMMARY n" to a summary request and "Answer to: <last user text>" to an answer prompt.
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> received = messages.ToList();

        if (received[^1].Role == ChatRole.System)
            return Reply($"SUMMARY {Interlocked.Increment(ref summaryCalls)}");

        lock (gate)
            answerCalls.Add(received);

        if (BeforeAnswer is not null)
            await BeforeAnswer(cancellationToken);

        return Reply($"Answer to: {received[^1].Text}");
    }

    // Streaming is not used by the API.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // No inner services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    // Nothing to release.
    public void Dispose()
    {
    }

    // Wrap a text as an assistant response.
    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));
}
