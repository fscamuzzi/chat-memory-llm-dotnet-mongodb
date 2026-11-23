using ChatMemory.Api.Contracts;
using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Services;

public enum SendMessageStatus
{
    Succeeded,
    ConversationNotFound,

    // Other requests took the turn sequence on every attempt.
    Conflict,
}

// Response is set only when Status is Succeeded.
public sealed record SendMessageResult(
    SendMessageStatus Status,
    SendMessageResponse? Response = null);

// Handles one chat turn: load state, reduce, ask the model, store the completed turn.
// Concurrent turns are serialized optimistically by the unique turn sequence.
public sealed class ChatService(
    IChatClient chatClient,
    IConversationRepository repository,
    ConversationMemoryReducer reducer,
    IOptions<ChatMemoryOptions> options,
    ILogger<ChatService> logger)
{
    private const int PreviewLength = 80;

    // Answer `request` inside the conversation and store the completed turn.
    // Only a reply whose turn was stored is returned as Succeeded.
    public Task<SendMessageResult> SendAsync(
        string conversationId, SendMessageRequest request, CancellationToken ct) =>
        AttemptAsync(conversationId, request.Text, options.Value.MaxConcurrencyRetries, ct);

    // Run one attempt. On a sequence conflict, start again from reloaded state while retries remain.
    private async Task<SendMessageResult> AttemptAsync(
        string conversationId, string userText, int retriesLeft, CancellationToken ct)
    {
        // 1) Load the conversation state. Guard: unknown conversation, no model call.
        var state = await repository.GetStateAsync(conversationId, ct);
        if (state is null)
            return new SendMessageResult(SendMessageStatus.ConversationNotFound);

        // 2) Build the reduced prompt and ask the answer model with exactly that prompt.
        var reduction = await reducer.ReduceAsync(state, userText, ct);
        var answer = await chatClient.GetResponseAsync(reduction.Prompt, cancellationToken: ct);

        // 3) Store user text, answer, and any new checkpoint as one document.
        //    A failed or cancelled model call never reaches this point, so nothing partial is stored.
        //    The turn carries the stored conversation id, not the caller's spelling of it.
        var turn = new ConversationTurn
        {
            ConversationId = state.Conversation.Id,
            Sequence = state.LatestSequence + 1,
            UserText = userText,
            AssistantText = answer.Text,
            CreatedAt = DateTime.UtcNow,
            CheckpointCreated = reduction.NewCheckpoint,
        };
        var inserted = await repository.InsertTurnAsync(turn, ct);

        // 4) Stored: report the reply and the memory diagnostics of this attempt.
        if (inserted == InsertTurnResult.Succeeded)
            return new SendMessageResult(SendMessageStatus.Succeeded, ToResponse(answer.Text, turn, reduction));

        // 5) Another request took this sequence. The answer is stale: discard it.
        if (retriesLeft == 0)
        {
            logger.LogWarning(
                "Turn {Sequence} of conversation {ConversationId} conflicted and no retry is left.",
                turn.Sequence,
                turn.ConversationId);
            return new SendMessageResult(SendMessageStatus.Conflict);
        }

        logger.LogWarning(
            "Turn {Sequence} of conversation {ConversationId} conflicted. Regenerating from reloaded state.",
            turn.Sequence,
            turn.ConversationId);
        return await AttemptAsync(conversationId, userText, retriesLeft - 1, ct);
    }

    // Build the HTTP response for a stored turn.
    private static SendMessageResponse ToResponse(string reply, ConversationTurn turn, ReductionResult reduction) =>
        new(
            reply,
            (int)(turn.Sequence * 2),
            reduction.Prompt.Count,
            reduction.Prompt.Select(m => $"{m.Role.Value}: {Truncate(m.Text)}").ToList(),
            reduction.SummaryRefreshed);

    // Cut a preview text at 80 characters and mark the cut with an ellipsis.
    private static string Truncate(string text) =>
        text.Length <= PreviewLength ? text : $"{text[..PreviewLength]}…";
}
