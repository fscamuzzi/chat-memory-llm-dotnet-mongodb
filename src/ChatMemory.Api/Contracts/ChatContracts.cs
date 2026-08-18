namespace ChatMemory.Api.Contracts;

public sealed record CreateConversationRequest(string? Title);

public sealed record ConversationCreatedResponse(string Id, string Title);

public sealed record SendMessageRequest(string Text);

/// <summary>
/// Reply plus the memory stats that make the reduction observable:
/// how many messages the stored history holds vs how many actually
/// reached the model after the IChatReducer pass.
/// </summary>
public sealed record SendMessageResponse(
    string Reply,
    int MessagesInHistory,
    int MessagesSentToModel,
    IReadOnlyList<string> PromptPreview);

public sealed record MessageResponse(string Role, string Text, DateTime At);

public sealed record ConversationResponse(
    string Id,
    string Title,
    DateTime CreatedAt,
    IReadOnlyList<MessageResponse> Messages);
