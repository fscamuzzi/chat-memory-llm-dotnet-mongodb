using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Services;

// Prompt is the exact message list for the answer model.
// NewCheckpoint is set only when this request refreshed the summary; it must be stored with the turn.
public sealed record ReductionResult(
    IReadOnlyList<ChatMessage> Prompt,
    ReducerCheckpoint? NewCheckpoint,
    bool SummaryRefreshed);

// Builds the answer prompt for one request from the stored turns.
// The official Microsoft.Extensions.AI reducers do the reduction. This class owns what they
// do not have: which turns to load, when to refresh the summary, and the durable checkpoint.
public sealed class ConversationMemoryReducer(
    IChatClient chatClient,
    IConversationRepository repository,
    IOptions<ChatMemoryOptions> options)
{
    // Added to the official summarization prompt. A small model drops exact details and tends to
    // answer the user again; these two lines keep the facts and ask for the summary alone.
    public const string SummaryRules =
        "Keep every name, date, time, number and place that the user gave.\n" +
        "Write only the summary. Do not continue the conversation and do not ask questions.";

    private const string PreviousSummaryPrefix = "Previous summary: ";

    // The summarization model receives the instruction as a final user message.
    private readonly SummaryRequestChatClient summaryClient = new(chatClient);

    // A prompt message together with the sequence of the turn it belongs to.
    private sealed record SourcedMessage(ChatMessage Message, long Sequence);

    // Build the prompt for `userText` with the configured strategy.
    public Task<ReductionResult> ReduceAsync(ConversationState state, string userText, CancellationToken ct) =>
        options.Value.Strategy switch
        {
            ChatMemoryStrategy.None => SendEverythingAsync(state, userText, ct),
            ChatMemoryStrategy.Counting => ReduceByCountAsync(state, userText, ct),
            ChatMemoryStrategy.Summarizing => ReduceBySummaryAsync(state, userText, ct),
            _ => throw new InvalidOperationException($"Unknown strategy {options.Value.Strategy}."),
        };

    // None: system prompt + the complete transcript + the new user message.
    private async Task<ReductionResult> SendEverythingAsync(
        ConversationState state, string userText, CancellationToken ct)
    {
        var turns = await repository.GetTurnsAfterAsync(state.Conversation.Id, 0, ct);
        var tail = ToTail(turns, state, userText);

        return new ReductionResult(BuildPrompt(SystemMessage(), null, tail), null, false);
    }

    // Counting: system prompt + the most recent TargetMessageCount messages.
    private async Task<ReductionResult> ReduceByCountAsync(
        ConversationState state, string userText, CancellationToken ct)
    {
        // 1) The new user message takes one slot; the other slots need target / 2 stored turns.
        var target = options.Value.TargetMessageCount;
        var turnLimit = target / 2;
        var turns = turnLimit == 0
            ? []
            : await repository.GetLatestTurnsAsync(state.Conversation.Id, turnLimit, ct);

        // 2) The official reducer keeps the system message and the last `target` other messages.
        var candidates = BuildPrompt(SystemMessage(), null, ToTail(turns, state, userText));
        var reduced = await new MessageCountingChatReducer(target).ReduceAsync(candidates, ct);

        return new ReductionResult(reduced.ToList(), null, false);
    }

    // Summarizing: system prompt + durable summary + the turns that the summary does not cover.
    private async Task<ReductionResult> ReduceBySummaryAsync(
        ConversationState state, string userText, CancellationToken ct)
    {
        // 1) Load only the turns after the checkpoint. Without a checkpoint that is every turn.
        var checkpoint = state.Checkpoint;
        var turns = await repository.GetTurnsAfterAsync(
            state.Conversation.Id, checkpoint?.SummarizedThroughSequence ?? 0, ct);
        var tail = ToTail(turns, state, userText);
        var system = SystemMessage();

        // 2) Under the limit: reuse the stored summary. No call to the summarization model.
        var limit = options.Value.TargetMessageCount + options.Value.SummarizationThreshold;
        if (tail.Count <= limit)
            return new ReductionResult(BuildPrompt(system, checkpoint?.SummaryText, tail), null, false);

        // 3) Over the limit: the official reducer summarizes the previous summary plus the oldest tail messages.
        IEnumerable<ChatMessage> previousSummary = checkpoint is null
            ? []
            : [new ChatMessage(ChatRole.Assistant, PreviousSummaryPrefix + checkpoint.SummaryText)];
        List<ChatMessage> input = [system, .. previousSummary, .. tail.Select(m => m.Message)];
        var reducer = new SummarizingChatReducer(
            summaryClient, options.Value.TargetMessageCount, options.Value.SummarizationThreshold);
        reducer.SummarizationPrompt += "\n\n" + SummaryRules;
        var reduced = (await reducer.ReduceAsync(input, ct)).ToList();

        // 4) The summary is the only output message that was not in the input.
        var inputMessages = input.ToHashSet<ChatMessage>(ReferenceEqualityComparer.Instance);
        var summary = reduced.FirstOrDefault(m => !inputMessages.Contains(m));
        if (summary is null)
            return new ReductionResult(BuildPrompt(system, checkpoint?.SummaryText, tail), null, false);

        // 5) Map the first retained message back to its turn. The summary covers every turn before it.
        //    A turn counts as summarized only when both of its messages are summarized.
        var retained = reduced.ToHashSet<ChatMessage>(ReferenceEqualityComparer.Instance);
        var firstRetainedSequence = tail.First(m => retained.Contains(m.Message)).Sequence;
        var newCheckpoint = new ReducerCheckpoint
        {
            SummaryText = summary.Text,
            SummarizedThroughSequence = firstRetainedSequence - 1,
        };

        // 6) Send the new summary plus the complete turns after the new cutoff.
        var unsummarized = tail.Where(m => m.Sequence > newCheckpoint.SummarizedThroughSequence).ToList();
        return new ReductionResult(BuildPrompt(system, newCheckpoint.SummaryText, unsummarized), newCheckpoint, true);
    }

    // The system message that opens every prompt.
    private ChatMessage SystemMessage() => new(ChatRole.System, options.Value.SystemPrompt);

    // Flatten stored turns into user/assistant messages and append the new user message.
    // The new message belongs to the turn that this request will store: latest + 1.
    private static List<SourcedMessage> ToTail(
        IEnumerable<ConversationTurn> turns, ConversationState state, string userText) =>
        [
            .. turns.SelectMany(turn => new SourcedMessage[]
            {
                new(new ChatMessage(ChatRole.User, turn.UserText), turn.Sequence),
                new(new ChatMessage(ChatRole.Assistant, turn.AssistantText), turn.Sequence),
            }),
            new(new ChatMessage(ChatRole.User, userText), state.LatestSequence + 1),
        ];

    // Assemble system message + optional summary (as an assistant message) + tail messages.
    private static List<ChatMessage> BuildPrompt(
        ChatMessage system, string? summaryText, IEnumerable<SourcedMessage> tail)
    {
        IEnumerable<ChatMessage> summary = summaryText is null
            ? []
            : [new ChatMessage(ChatRole.Assistant, summaryText)];

        return [system, .. summary, .. tail.Select(m => m.Message)];
    }
}
