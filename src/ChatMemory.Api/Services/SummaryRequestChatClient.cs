using Microsoft.Extensions.AI;

namespace ChatMemory.Api.Services;

// Reshapes the request of the official SummarizingChatReducer before it reaches the model.
// The reducer ends its request with a system message that carries the instruction. Some chat
// templates (llama3.2 in Ollama) render system text at the top and leave the last assistant
// message open: the model then continues that old answer instead of writing a summary.
// A final user message is the shape every model answers.
// The summary is also requested at temperature 0: the same history gives the same summary,
// and a small model drops fewer facts. Answer requests keep their own options.
public sealed class SummaryRequestChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    // The temperature of a summary request.
    public const float SummaryTemperature = 0f;

    // Send a trailing system instruction as the final user message, at temperature 0;
    // forward every other request as it is.
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 1) Only a request that ends with a system message needs the new shape.
        IReadOnlyList<ChatMessage> received = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (received.Count == 0 || received[^1].Role != ChatRole.System)
            return base.GetResponseAsync(received, options, cancellationToken);

        // 2) Same texts in the same order; the caller's list is left untouched.
        List<ChatMessage> reshaped = [.. received.SkipLast(1), new ChatMessage(ChatRole.User, received[^1].Text)];

        // 3) Temperature 0 on a copy of the options, so the caller's object keeps its values.
        var summaryOptions = options?.Clone() ?? new ChatOptions();
        summaryOptions.Temperature = SummaryTemperature;
        return base.GetResponseAsync(reshaped, summaryOptions, cancellationToken);
    }
}
