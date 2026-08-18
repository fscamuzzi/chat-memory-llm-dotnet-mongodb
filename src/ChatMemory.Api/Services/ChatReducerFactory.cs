using ChatMemory.Api.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Services;

public sealed class ChatReducerFactory(IOptions<ChatMemoryOptions> options)
{
    // "counting" keeps the last N messages, "summarizing" folds the older ones
    // into an LLM-generated summary. Anything else = no reduction at all.
    public IChatReducer? Create(IChatClient chatClient) =>
        options.Value.Strategy.ToLowerInvariant() switch
        {
            "counting" => new MessageCountingChatReducer(options.Value.TargetMessageCount),
            "summarizing" => new SummarizingChatReducer(
                chatClient,
                options.Value.TargetMessageCount,
                options.Value.SummarizationThreshold),
            _ => null,
        };
}
