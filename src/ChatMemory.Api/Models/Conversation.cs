using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatMemory.Api.Models;

// Small metadata document. The messages live in separate ConversationTurn documents.
public sealed class Conversation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
