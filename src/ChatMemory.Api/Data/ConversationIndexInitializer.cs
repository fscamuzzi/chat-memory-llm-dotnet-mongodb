using ChatMemory.Api.Models;
using MongoDB.Driver;

namespace ChatMemory.Api.Data;

// Creates the unique (ConversationId, Sequence) index before the host accepts requests.
// The index makes two concurrent turns with the same sequence impossible, also across processes.
public sealed class ConversationIndexInitializer(IMongoDatabase database) : IHostedService
{
    public const string TurnSequenceIndexName = "ux_turns_conversation_sequence";

    // Create the index. MongoDB does nothing when the same index already exists.
    // One ascending index is enough: MongoDB scans it in reverse for latest-turn reads.
    public Task StartAsync(CancellationToken cancellationToken) =>
        database.GetCollection<ConversationTurn>(MongoConversationRepository.TurnsCollection)
            .Indexes.CreateOneAsync(
                new CreateIndexModel<ConversationTurn>(
                    Builders<ConversationTurn>.IndexKeys
                        .Ascending(t => t.ConversationId)
                        .Ascending(t => t.Sequence),
                    new CreateIndexOptions { Unique = true, Name = TurnSequenceIndexName }),
                cancellationToken: cancellationToken);

    // Nothing to release on shutdown.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
