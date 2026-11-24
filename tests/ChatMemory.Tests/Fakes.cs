using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using ChatMemory.Api.Services;
using Microsoft.Extensions.AI;
using MongoDB.Bson;

namespace ChatMemory.Tests;

// Scripted IChatClient. It records every call and tells answer calls from summary calls.
// A summary request ends with a user message that closes with the summary rules; an answer prompt never does.
public sealed class FakeChatClient(string answer = "ok") : IChatClient
{
    private readonly List<IReadOnlyList<ChatMessage>> calls = [];

    // Optional script for answer calls. Throw from it to simulate a model failure.
    public Func<IReadOnlyList<ChatMessage>, string>? OnAnswer { get; set; }

    // The prompts of the answer calls, in call order.
    public IReadOnlyList<IReadOnlyList<ChatMessage>> AnswerCalls => calls.Where(c => !IsSummaryCall(c)).ToList();

    // The requests of the summary calls, in call order.
    public IReadOnlyList<IReadOnlyList<ChatMessage>> SummaryCalls => calls.Where(IsSummaryCall).ToList();

    // Record the call and reply: "SUMMARY n" for the n-th summary call, the scripted answer otherwise.
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ChatMessage> received = messages.ToList();
        calls.Add(received);

        var text = IsSummaryCall(received)
            ? $"SUMMARY {SummaryCalls.Count}"
            : OnAnswer?.Invoke(received) ?? answer;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    // Streaming is not used by the API.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // No inner services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    // Nothing to release.
    public void Dispose()
    {
    }

    // A summary request ends with the user message that carries the summarization prompt and rules.
    private static bool IsSummaryCall(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0
        && messages[^1].Role == ChatRole.User
        && messages[^1].Text.EndsWith(ConversationMemoryReducer.SummaryRules, StringComparison.Ordinal);
}

// In-memory repository with the same observable behavior as MongoConversationRepository.
public sealed class InMemoryConversationRepository : IConversationRepository
{
    // MongoDB compares conversation ids as ObjectId values, so the hex case does not matter.
    // Turn documents store ConversationId as a plain string: that comparison is case-sensitive.
    private readonly Dictionary<string, Conversation> conversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConversationTurn> turns = [];

    // Every turn read, for example "latest:5" or "after:3". Proves how much history a strategy loads.
    public List<string> TurnReads { get; } = [];

    // How many of the next inserts lose their sequence to a competing request.
    // The competing turn is stored first, so the caller's insert conflicts by the normal unique rule.
    public int ConflictsToInject { get; set; }

    // Store a new conversation with a generated ObjectId.
    public Task<Conversation> CreateAsync(string title, CancellationToken ct)
    {
        var conversation = new Conversation
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Title = title,
            CreatedAt = DateTime.UtcNow,
        };
        conversations[conversation.Id] = conversation;
        return Task.FromResult(conversation);
    }

    // Return the conversation, or null for an invalid or unknown id.
    public Task<Conversation?> GetConversationAsync(string id, CancellationToken ct) =>
        Task.FromResult(conversations.GetValueOrDefault(id));

    // Return metadata, latest sequence and newest checkpoint; turns are read with the stored id.
    public Task<ConversationState?> GetStateAsync(string id, CancellationToken ct)
    {
        if (!conversations.TryGetValue(id, out var conversation))
            return Task.FromResult<ConversationState?>(null);

        var newestFirst = NewestFirst(conversation.Id).ToList();
        var checkpoint = newestFirst.Select(t => t.CheckpointCreated).FirstOrDefault(c => c is not null);
        return Task.FromResult<ConversationState?>(
            new ConversationState(conversation, newestFirst.FirstOrDefault()?.Sequence ?? 0, checkpoint));
    }

    // Return the newest `limit` turns, oldest first, and log the read.
    public Task<IReadOnlyList<ConversationTurn>> GetLatestTurnsAsync(
        string conversationId, int limit, CancellationToken ct)
    {
        TurnReads.Add($"latest:{limit}");
        return Task.FromResult<IReadOnlyList<ConversationTurn>>(
            NewestFirst(conversationId).Take(limit).Reverse().ToList());
    }

    // Return the turns after `sequence`, oldest first, and log the read.
    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAfterAsync(
        string conversationId, long sequence, CancellationToken ct)
    {
        TurnReads.Add($"after:{sequence}");
        return Task.FromResult<IReadOnlyList<ConversationTurn>>(
            NewestFirst(conversationId).Where(t => t.Sequence > sequence).Reverse().ToList());
    }

    // Insert a turn, or report SequenceConflict when the sequence is taken.
    public Task<InsertTurnResult> InsertTurnAsync(ConversationTurn turn, CancellationToken ct)
    {
        if (ConflictsToInject > 0)
        {
            ConflictsToInject--;
            turns.Add(TestData.CompetingTurn(turn.ConversationId, turn.Sequence));
        }

        // Same rule as the unique (ConversationId, Sequence) index.
        if (turns.Any(t => t.ConversationId == turn.ConversationId && t.Sequence == turn.Sequence))
            return Task.FromResult(InsertTurnResult.SequenceConflict);

        turn.Id = ObjectId.GenerateNewId().ToString();
        turns.Add(turn);
        return Task.FromResult(InsertTurnResult.Succeeded);
    }

    // Return one transcript page with the same shape rules as the MongoDB repository.
    public Task<ConversationPage?> GetPageAsync(
        string id, int limit, long? beforeSequence, CancellationToken ct)
    {
        if (!conversations.TryGetValue(id, out var conversation))
            return Task.FromResult<ConversationPage?>(null);

        var cursor = beforeSequence ?? long.MaxValue;
        var newestFirst = NewestFirst(conversation.Id).Where(t => t.Sequence < cursor).Take(limit + 1).ToList();
        return Task.FromResult<ConversationPage?>(
            ConversationPages.Build(conversation, NewestFirst(conversation.Id).FirstOrDefault(), newestFirst, limit));
    }

    // The turns of one conversation, newest first.
    private IEnumerable<ConversationTurn> NewestFirst(string conversationId) =>
        turns.Where(t => t.ConversationId == conversationId).OrderByDescending(t => t.Sequence);
}

public static class TestData
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    // A completed turn with predictable text: "Question n" / "Answer n".
    public static ConversationTurn Turn(string conversationId, long sequence, ReducerCheckpoint? checkpoint = null) =>
        new()
        {
            ConversationId = conversationId,
            Sequence = sequence,
            UserText = $"Question {sequence}",
            AssistantText = $"Answer {sequence}",
            CreatedAt = Epoch.AddMinutes(sequence),
            CheckpointCreated = checkpoint,
        };

    // The turn of another request that took the same sequence first.
    public static ConversationTurn CompetingTurn(string conversationId, long sequence) =>
        new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ConversationId = conversationId,
            Sequence = sequence,
            UserText = $"Competing question {sequence}",
            AssistantText = $"Competing answer {sequence}",
            CreatedAt = Epoch.AddMinutes(sequence),
        };

    // Insert turns 1..count without checkpoints.
    public static async Task SeedTurnsAsync(
        this IConversationRepository repository, string conversationId, int count)
    {
        foreach (var sequence in Enumerable.Range(1, count))
            await repository.InsertTurnAsync(Turn(conversationId, sequence), CancellationToken.None);
    }
}
