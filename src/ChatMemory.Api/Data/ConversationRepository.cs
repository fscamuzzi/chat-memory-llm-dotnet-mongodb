using ChatMemory.Api.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatMemory.Api.Data;

public interface IConversationRepository
{
    Task<Conversation> CreateAsync(string title, CancellationToken ct);

    Task<Conversation?> GetAsync(string id, CancellationToken ct);

    Task AppendMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct);
}

public sealed class MongoConversationRepository(IMongoDatabase database) : IConversationRepository
{
    private readonly IMongoCollection<Conversation> collection =
        database.GetCollection<Conversation>("conversations");

    public async Task<Conversation> CreateAsync(string title, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await collection.InsertOneAsync(conversation, cancellationToken: ct);
        return conversation;
    }

    public async Task<Conversation?> GetAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _))
            return null;

        return await collection.Find(c => c.Id == id).FirstOrDefaultAsync(ct);
    }

    // Partial update on purpose: $push the new turn instead of replacing the
    // whole document, so concurrent turns never overwrite each other.
    public Task AppendMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct) =>
        collection.UpdateOneAsync(
            c => c.Id == id,
            Builders<Conversation>.Update
                .PushEach(c => c.Messages, messages)
                .Set(c => c.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);
}
