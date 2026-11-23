namespace ChatMemory.Api.Contracts;

public sealed record HealthResponse(string Status);

public sealed record ErrorResponse(string Message);

public sealed record CreateConversationRequest(string? Title);

public sealed record ConversationCreatedResponse(string Id, string Title);

public sealed record SendMessageRequest(string Text);

/// <summary>
/// Reply plus the memory diagnostics of the request.
/// <c>StoredMessageCount</c>: user and assistant messages persisted after this turn.
/// <c>MessagesSentToModel</c>: exact number of prompt messages the answer model received.
/// <c>PromptPreview</c>: one "role: text" line per prompt message, text cut at 80 characters.
/// <c>SummaryRefreshed</c>: true when this request generated a new durable summary.
/// </summary>
public sealed record SendMessageResponse(
    string Reply,
    int StoredMessageCount,
    int MessagesSentToModel,
    IReadOnlyList<string> PromptPreview,
    bool SummaryRefreshed);

public sealed record MessageResponse(string Role, string Text, DateTime At);

/// <summary>
/// One transcript page. <c>Messages</c> are chronological inside the page.
/// <c>NextBeforeSequence</c> is the cursor of the next older page, or null on the last page.
/// </summary>
public sealed record ConversationResponse(
    string Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MessageResponse> Messages,
    int StoredMessageCount,
    long? NextBeforeSequence);
