using ChatMemory.Api.Contracts;
using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Services;

public sealed class ChatService(
    IChatClient chatClient,
    IConversationRepository repository,
    ChatReducerFactory reducerFactory,
    IOptions<ChatMemoryOptions> options)
{
    private readonly Lazy<IChatReducer?> reducer = new(() => reducerFactory.Create(chatClient));

    public async Task<SendMessageResponse?> SendAsync(
        string conversationId, SendMessageRequest request, CancellationToken ct)
    {
        var conversation = await repository.GetAsync(conversationId, ct);
        if (conversation is null)
            return null;

        // 1. Rebuild the full prompt: system + persisted history + new user turn.
        List<ChatMessage> fullPrompt =
        [
            new(ChatRole.System, options.Value.SystemPrompt),
            .. conversation.Messages.Select(m => new ChatMessage(new ChatRole(m.Role), m.Text)),
            new(ChatRole.User, request.Text),
        ];

        // 2. Reduce it. The store keeps everything; the model gets the reduced view.
        var prompt = reducer.Value is null
            ? fullPrompt
            : (await reducer.Value.ReduceAsync(fullPrompt, ct)).ToList();

        // 3. Ask the model with the reduced prompt only.
        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: ct);

        // 4. Persist the new turn (full fidelity, no reduction on write).
        var now = DateTime.UtcNow;
        List<StoredMessage> turn =
        [
            new() { Role = ChatRole.User.Value, Text = request.Text, At = now },
            new() { Role = ChatRole.Assistant.Value, Text = response.Text, At = now },
        ];
        await repository.AppendMessagesAsync(conversationId, turn, ct);

        var preview = prompt.Select(m => $"{m.Role.Value}: {Truncate(m.Text)}").ToList();
        return new SendMessageResponse(response.Text, fullPrompt.Count, prompt.Count, preview);
    }

    private static string Truncate(string text) =>
        text.Length <= 80 ? text : $"{text[..80]}…";
}
