using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using Microsoft.Extensions.AI;
using MongoDB.Bson;

namespace ChatMemory.Tests;

// Scripted IChatClient: fixed reply, records the messages it actually received.
public sealed class FakeChatClient(string reply) : IChatClient
{
    public List<ChatMessage>? LastMessages { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class InMemoryConversationRepository : IConversationRepository
{
    private readonly Dictionary<string, Conversation> store = [];

    public Task<Conversation> CreateAsync(string title, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };
        store[conversation.Id] = conversation;
        return Task.FromResult(conversation);
    }

    public Task<Conversation?> GetAsync(string id, CancellationToken ct) =>
        Task.FromResult(store.GetValueOrDefault(id));

    public Task AppendMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct)
    {
        store[id].Messages.AddRange(messages);
        return Task.CompletedTask;
    }
}
