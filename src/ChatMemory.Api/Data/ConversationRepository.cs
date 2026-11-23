using ChatMemory.Api.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace ChatMemory.Api.Data;

public enum InsertTurnResult
{
    Succeeded,

    // Another request already stored a turn with the same sequence.
    SequenceConflict,
}

// What a send attempt needs before it builds a prompt.
// LatestSequence is 0 when the conversation has no completed turn.
public sealed record ConversationState(
    Conversation Conversation,
    long LatestSequence,
    ReducerCheckpoint? Checkpoint);

// One transcript page. Turns are oldest first. NextBeforeSequence is null on the last page.
public sealed record ConversationPage(
    Conversation Conversation,
    IReadOnlyList<ConversationTurn> Turns,
    int StoredMessageCount,
    DateTime UpdatedAt,
    long? NextBeforeSequence);

public interface IConversationRepository
{
    Task<Conversation> CreateAsync(string title, CancellationToken ct);

    Task<Conversation?> GetConversationAsync(string id, CancellationToken ct);

    Task<ConversationState?> GetStateAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<ConversationTurn>> GetLatestTurnsAsync(
        string conversationId, int limit, CancellationToken ct);

    Task<IReadOnlyList<ConversationTurn>> GetTurnsAfterAsync(
        string conversationId, long sequence, CancellationToken ct);

    Task<InsertTurnResult> InsertTurnAsync(ConversationTurn turn, CancellationToken ct);

    Task<ConversationPage?> GetPageAsync(
        string id, int limit, long? beforeSequence, CancellationToken ct);
}

// MongoDB store: one small document per conversation, one immutable document per completed turn.
// Reads use LINQ. The native driver API is used only for inserts.
public sealed class MongoConversationRepository(IMongoDatabase database) : IConversationRepository
{
    public const string ConversationsCollection = "conversations";
    public const string TurnsCollection = "turns";

    private readonly IMongoCollection<Conversation> conversations =
        database.GetCollection<Conversation>(ConversationsCollection);

    private readonly IMongoCollection<ConversationTurn> turns =
        database.GetCollection<ConversationTurn>(TurnsCollection);

    // Store a new conversation metadata document and return it.
    public async Task<Conversation> CreateAsync(string title, CancellationToken ct)
    {
        var conversation = new Conversation
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Title = title,
            CreatedAt = DateTime.UtcNow,
        };
        await conversations.InsertOneAsync(conversation, cancellationToken: ct);

        return conversation;
    }

    // Load the metadata of one conversation. Returns null for an invalid or unknown id.
    public async Task<Conversation?> GetConversationAsync(string id, CancellationToken ct)
    {
        // Guard: an id that is not an ObjectId cannot match any document.
        if (!ObjectId.TryParse(id, out _))
            return null;

        return await conversations.AsQueryable().Where(c => c.Id == id).FirstOrDefaultAsync(ct);
    }

    // Load what a send attempt needs: metadata, the latest turn sequence, and the newest checkpoint.
    public async Task<ConversationState?> GetStateAsync(string id, CancellationToken ct)
    {
        // 1) Guard: without the conversation there is no state.
        var conversation = await GetConversationAsync(id, ct);
        if (conversation is null)
            return null;

        // 2) Latest sequence: a reverse scan of the unique index. No turn gives 0.
        //    Turns are read with the stored id, not the caller's spelling: MongoDB matches an
        //    ObjectId in any hex case, but ConversationId on a turn is a case-sensitive string.
        var latestSequence = await ConversationTurns(conversation.Id)
            .OrderByDescending(t => t.Sequence)
            .Select(t => t.Sequence)
            .FirstOrDefaultAsync(ct);

        // 3) Newest durable checkpoint: the last turn that refreshed the summary.
        var checkpoint = await ConversationTurns(conversation.Id)
            .Where(t => t.CheckpointCreated != null)
            .OrderByDescending(t => t.Sequence)
            .Select(t => t.CheckpointCreated)
            .FirstOrDefaultAsync(ct);

        return new ConversationState(conversation, latestSequence, checkpoint);
    }

    // Load the newest `limit` turns of a conversation, oldest first.
    public async Task<IReadOnlyList<ConversationTurn>> GetLatestTurnsAsync(
        string conversationId, int limit, CancellationToken ct)
    {
        var newestFirst = await ConversationTurns(conversationId)
            .OrderByDescending(t => t.Sequence)
            .Take(limit)
            .ToListAsync(ct);

        return newestFirst.AsEnumerable().Reverse().ToList();
    }

    // Load every turn with a sequence greater than `sequence`, oldest first.
    public async Task<IReadOnlyList<ConversationTurn>> GetTurnsAfterAsync(
        string conversationId, long sequence, CancellationToken ct) =>
        await ConversationTurns(conversationId)
            .Where(t => t.Sequence > sequence)
            .OrderBy(t => t.Sequence)
            .ToListAsync(ct);

    // Insert one completed turn. The unique (ConversationId, Sequence) index rejects a
    // second turn with the same sequence; that case is reported as SequenceConflict.
    public async Task<InsertTurnResult> InsertTurnAsync(ConversationTurn turn, CancellationToken ct)
    {
        turn.Id = ObjectId.GenerateNewId().ToString();

        try
        {
            await turns.InsertOneAsync(turn, cancellationToken: ct);
            return InsertTurnResult.Succeeded;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another request won this sequence. Every other database error propagates.
            return InsertTurnResult.SequenceConflict;
        }
    }

    // Load one transcript page of at most `limit` turns older than `beforeSequence` (exclusive).
    // Without a cursor the page holds the newest turns. Returns null for an unknown conversation.
    public async Task<ConversationPage?> GetPageAsync(
        string id, int limit, long? beforeSequence, CancellationToken ct)
    {
        // 1) Guard: without the conversation there is no page.
        var conversation = await GetConversationAsync(id, ct);
        if (conversation is null)
            return null;

        // 2) The latest turn gives the total count and the last-activity time.
        //    Turns are read with the stored id (see GetStateAsync).
        var latest = await ConversationTurns(conversation.Id)
            .OrderByDescending(t => t.Sequence)
            .FirstOrDefaultAsync(ct);

        // 3) Read one turn more than requested: the extra turn proves that an older page exists.
        var cursor = beforeSequence ?? long.MaxValue;
        var newestFirst = await ConversationTurns(conversation.Id)
            .Where(t => t.Sequence < cursor)
            .OrderByDescending(t => t.Sequence)
            .Take(limit + 1)
            .ToListAsync(ct);

        return ConversationPages.Build(conversation, latest, newestFirst, limit);
    }

    // LINQ source for the turns of one conversation.
    private IQueryable<ConversationTurn> ConversationTurns(string conversationId) =>
        turns.AsQueryable().Where(t => t.ConversationId == conversationId);
}

public static class ConversationPages
{
    // Shape a page from the latest turn and from `limit + 1` turns read newest first.
    public static ConversationPage Build(
        Conversation conversation,
        ConversationTurn? latest,
        IReadOnlyList<ConversationTurn> newestFirst,
        int limit)
    {
        // 1) Keep at most `limit` turns and put them in chronological order.
        var pageTurns = newestFirst.Take(limit).Reverse().ToList();

        // 2) Sequences are 1..n without gaps, so the latest sequence is the turn count.
        var storedMessageCount = (int)((latest?.Sequence ?? 0) * 2);

        // 3) The cursor points at the oldest returned turn only when an older turn exists.
        long? nextBeforeSequence = newestFirst.Count > limit ? pageTurns[0].Sequence : null;

        return new ConversationPage(
            conversation,
            pageTurns,
            storedMessageCount,
            latest?.CreatedAt ?? conversation.CreatedAt,
            nextBeforeSequence);
    }
}
