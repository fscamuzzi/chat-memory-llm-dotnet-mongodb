using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatMemory.Api.Models;

// One completed user/assistant exchange. Written once, never updated.
public sealed class ConversationTurn
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;

    // One-based position inside the conversation. Unique together with ConversationId.
    public long Sequence { get; set; }

    public string UserText { get; set; } = string.Empty;

    public string AssistantText { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    // Set only on the turn whose request refreshed the summary.
    public ReducerCheckpoint? CheckpointCreated { get; set; }
}

// Application-owned summary state. It survives requests and process restarts.
public sealed class ReducerCheckpoint
{
    public string SummaryText { get; set; } = string.Empty;

    // Highest turn sequence that SummaryText covers.
    public long SummarizedThroughSequence { get; set; }
}
