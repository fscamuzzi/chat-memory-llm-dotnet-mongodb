using ChatMemory.Api.Contracts;
using ChatMemory.Api.Data;
using ChatMemory.Api.Services;

namespace ChatMemory.Api.Endpoints;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapPost("/", async (
            CreateConversationRequest request, IConversationRepository repository, CancellationToken ct) =>
        {
            var conversation = await repository.CreateAsync(request.Title ?? "New conversation", ct);
            return Results.Created(
                $"/api/conversations/{conversation.Id}",
                new ConversationCreatedResponse(conversation.Id, conversation.Title));
        });

        group.MapGet("/{id}", async (
            string id, IConversationRepository repository, CancellationToken ct) =>
        {
            var conversation = await repository.GetAsync(id, ct);
            return conversation is null
                ? Results.NotFound()
                : Results.Ok(new ConversationResponse(
                    conversation.Id,
                    conversation.Title,
                    conversation.CreatedAt,
                    conversation.Messages
                        .Select(m => new MessageResponse(m.Role, m.Text, m.At))
                        .ToList()));
        });

        group.MapPost("/{id}/messages", async (
            string id, SendMessageRequest request, ChatService chatService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest(new { message = "Text is required." });

            var response = await chatService.SendAsync(id, request, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });
    }
}
