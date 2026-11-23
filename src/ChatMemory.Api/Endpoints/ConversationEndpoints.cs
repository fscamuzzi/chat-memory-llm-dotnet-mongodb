using ChatMemory.Api.Contracts;
using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Endpoints;

public static class ConversationEndpoints
{
    private const string DefaultTitle = "New conversation";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private static readonly ErrorResponse ConversationNotFound = new("Conversation not found.");

    // Register the health route and the conversation routes.
    public static void MapConversationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", Health);

        var group = app.MapGroup("/api/conversations");
        group.MapPost("/", CreateConversationAsync);
        group.MapGet("/{id}", GetConversationAsync);
        group.MapPost("/{id}/messages", SendMessageAsync);
    }

    // GET /api/health: the process is up.
    public static Ok<HealthResponse> Health() => TypedResults.Ok(new HealthResponse("ok"));

    // POST /api/conversations: create an empty conversation.
    // A missing or blank title becomes "New conversation"; an oversized title returns 400.
    public static async Task<Results<Created<ConversationCreatedResponse>, BadRequest<ErrorResponse>>>
        CreateConversationAsync(
            CreateConversationRequest request,
            IConversationRepository repository,
            IOptions<ChatMemoryOptions> options,
            CancellationToken ct)
    {
        // 1) Normalize the title.
        var title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle : request.Title.Trim();

        // 2) Guard: reject a title above the configured limit.
        var maxLength = options.Value.MaxTitleCharacters;
        if (title.Length > maxLength)
            return TypedResults.BadRequest(new ErrorResponse($"Title must be at most {maxLength} characters."));

        // 3) Store the conversation and point at its transcript route.
        var conversation = await repository.CreateAsync(title, ct);
        return TypedResults.Created(
            $"/api/conversations/{conversation.Id}",
            new ConversationCreatedResponse(conversation.Id, conversation.Title));
    }

    // GET /api/conversations/{id}?limit=20&beforeSequence=n: one transcript page.
    // `limit` counts turns (default 20, capped at 100). `beforeSequence` is an exclusive cursor.
    public static async Task<Results<Ok<ConversationResponse>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>>>
        GetConversationAsync(
            string id,
            int? limit,
            long? beforeSequence,
            IConversationRepository repository,
            CancellationToken ct)
    {
        // 1) Guards: both paging values must be positive when present.
        if (limit < 1)
            return TypedResults.BadRequest(new ErrorResponse("limit must be at least 1."));

        if (beforeSequence < 1)
            return TypedResults.BadRequest(new ErrorResponse("beforeSequence must be at least 1."));

        // 2) Load the page. Null means an invalid or unknown conversation id.
        var pageSize = Math.Min(limit ?? DefaultPageSize, MaxPageSize);
        var page = await repository.GetPageAsync(id, pageSize, beforeSequence, ct);
        if (page is null)
            return TypedResults.NotFound(ConversationNotFound);

        // 3) Flatten each turn into its user message and its assistant message, oldest first.
        var messages = page.Turns.SelectMany(ToMessages).ToList();
        return TypedResults.Ok(new ConversationResponse(
            page.Conversation.Id,
            page.Conversation.Title,
            page.Conversation.CreatedAt,
            page.UpdatedAt,
            messages,
            page.StoredMessageCount,
            page.NextBeforeSequence));
    }

    // POST /api/conversations/{id}/messages: answer one user message and store the completed turn.
    public static async Task<Results<Ok<SendMessageResponse>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>>>
        SendMessageAsync(
            string id,
            SendMessageRequest request,
            ChatService chatService,
            IOptions<ChatMemoryOptions> options,
            CancellationToken ct)
    {
        // 1) Guards: the text must be present and inside the configured limit.
        if (string.IsNullOrWhiteSpace(request.Text))
            return TypedResults.BadRequest(new ErrorResponse("Text is required."));

        var maxLength = options.Value.MaxInputCharacters;
        if (request.Text.Length > maxLength)
            return TypedResults.BadRequest(new ErrorResponse($"Text must be at most {maxLength} characters."));

        // 2) Run the turn and map each service outcome to its status code.
        var result = await chatService.SendAsync(id, request, ct);
        return result.Status switch
        {
            SendMessageStatus.Succeeded => TypedResults.Ok(result.Response),
            SendMessageStatus.ConversationNotFound => TypedResults.NotFound(ConversationNotFound),
            SendMessageStatus.Conflict => TypedResults.Conflict(new ErrorResponse(
                "Other requests changed this conversation while the reply was generated. Send the message again.")),
            _ => throw new InvalidOperationException($"Unknown send status {result.Status}."),
        };
    }

    // One stored turn as its two transcript messages.
    private static MessageResponse[] ToMessages(ConversationTurn turn) =>
    [
        new(ChatRole.User.Value, turn.UserText, turn.CreatedAt),
        new(ChatRole.Assistant.Value, turn.AssistantText, turn.CreatedAt),
    ];
}
