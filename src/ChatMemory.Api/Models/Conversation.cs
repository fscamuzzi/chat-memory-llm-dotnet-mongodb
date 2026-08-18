using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatMemory.Api.Models;

public sealed class Conversation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<StoredMessage> Messages { get; set; } = [];
}

public sealed class StoredMessage
{
    public string Role { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTime At { get; set; }
}
