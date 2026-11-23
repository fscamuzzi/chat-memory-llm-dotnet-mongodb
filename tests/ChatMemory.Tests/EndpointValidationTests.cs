using ChatMemory.Api.Contracts;
using ChatMemory.Api.Endpoints;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatMemory.Tests;

// Calls the Minimal API handlers directly: no HTTP server, no MongoDB.
public class EndpointValidationTests
{
    private const string UnknownId = "68a0000000000000000000aa";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly InMemoryConversationRepository repository = new();
    private readonly FakeChatClient chatClient = new("Ciao!");
    private readonly IOptions<ChatMemoryOptions> options =
        Options.Create(new ChatMemoryOptions { Strategy = ChatMemoryStrategy.None });

    // Build the real service over the fakes of this test.
    private ChatService BuildService() =>
        new(
            chatClient,
            repository,
            new ConversationMemoryReducer(chatClient, repository, options),
            options,
            NullLogger<ChatService>.Instance);

    // Create a conversation that already has `turns` completed turns and return its id.
    private async Task<string> CreateConversationWithTurnsAsync(int turns)
    {
        var conversation = await repository.CreateAsync("Test", Ct);
        await repository.SeedTurnsAsync(conversation.Id, turns);
        return conversation.Id;
    }

    // ---- POST /api/conversations ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_MissingOrBlankTitle_UsesTheDefaultTitle(string? title)
    {
        var result = await ConversationEndpoints.CreateConversationAsync(
            new CreateConversationRequest(title), repository, options, Ct);

        var created = Assert.IsType<Created<ConversationCreatedResponse>>(result.Result);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("New conversation", created.Value!.Title);
        Assert.Equal($"/api/conversations/{created.Value.Id}", created.Location);
    }

    [Fact]
    public async Task Create_TitleAtTheLimit_IsTrimmedAndAccepted()
    {
        var title = new string('t', 120);

        var result = await ConversationEndpoints.CreateConversationAsync(
            new CreateConversationRequest($"  {title}  "), repository, options, Ct);

        var created = Assert.IsType<Created<ConversationCreatedResponse>>(result.Result);
        Assert.Equal(title, created.Value!.Title);
    }

    [Fact]
    public async Task Create_OversizedTitle_Returns400WithErrorResponse()
    {
        var result = await ConversationEndpoints.CreateConversationAsync(
            new CreateConversationRequest(new string('t', 121)), repository, options, Ct);

        var badRequest = Assert.IsType<BadRequest<ErrorResponse>>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal("Title must be at most 120 characters.", badRequest.Value!.Message);
    }

    // ---- POST /api/conversations/{id}/messages ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Send_MissingOrBlankText_Returns400_WithoutAModelCall(string? text)
    {
        var id = await CreateConversationWithTurnsAsync(0);

        var result = await ConversationEndpoints.SendMessageAsync(
            id, new SendMessageRequest(text!), BuildService(), options, Ct);

        var badRequest = Assert.IsType<BadRequest<ErrorResponse>>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal("Text is required.", badRequest.Value!.Message);
        Assert.Empty(chatClient.AnswerCalls);
    }

    [Fact]
    public async Task Send_OversizedText_Returns400_WithoutAModelCall()
    {
        var id = await CreateConversationWithTurnsAsync(0);

        var result = await ConversationEndpoints.SendMessageAsync(
            id, new SendMessageRequest(new string('x', 4001)), BuildService(), options, Ct);

        var badRequest = Assert.IsType<BadRequest<ErrorResponse>>(result.Result);
        Assert.Equal("Text must be at most 4000 characters.", badRequest.Value!.Message);
        Assert.Empty(chatClient.AnswerCalls);
    }

    [Fact]
    public async Task Send_TextAtTheLimit_Returns200WithTheTypedResponse()
    {
        var id = await CreateConversationWithTurnsAsync(0);

        var result = await ConversationEndpoints.SendMessageAsync(
            id, new SendMessageRequest(new string('x', 4000)), BuildService(), options, Ct);

        var ok = Assert.IsType<Ok<SendMessageResponse>>(result.Result);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("Ciao!", ok.Value!.Reply);
        Assert.Equal(2, ok.Value.StoredMessageCount);
    }

    [Theory]
    [InlineData("not-an-object-id")]
    [InlineData(UnknownId)]
    public async Task Send_InvalidOrUnknownConversation_Returns404WithErrorResponse(string id)
    {
        var result = await ConversationEndpoints.SendMessageAsync(
            id, new SendMessageRequest("Hello"), BuildService(), options, Ct);

        var notFound = Assert.IsType<NotFound<ErrorResponse>>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal("Conversation not found.", notFound.Value!.Message);
    }

    [Fact]
    public async Task Send_WhenRetriesAreExhausted_Returns409WithErrorResponse()
    {
        var id = await CreateConversationWithTurnsAsync(0);
        repository.ConflictsToInject = 2; // first attempt + the single default retry

        var result = await ConversationEndpoints.SendMessageAsync(
            id, new SendMessageRequest("Hello"), BuildService(), options, Ct);

        var conflict = Assert.IsType<Conflict<ErrorResponse>>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(conflict.Value!.Message));
    }

    // ---- GET /api/conversations/{id} ----

    [Theory]
    [InlineData("not-an-object-id")]
    [InlineData(UnknownId)]
    public async Task Get_InvalidOrUnknownConversation_Returns404WithErrorResponse(string id)
    {
        var result = await ConversationEndpoints.GetConversationAsync(id, null, null, repository, Ct);

        var notFound = Assert.IsType<NotFound<ErrorResponse>>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal("Conversation not found.", notFound.Value!.Message);
    }

    [Fact]
    public async Task Get_WithUppercaseHexId_ReturnsTheSameTranscript()
    {
        var id = await CreateConversationWithTurnsAsync(3);

        var result = await ConversationEndpoints.GetConversationAsync(id.ToUpperInvariant(), null, null, repository, Ct);

        var ok = Assert.IsType<Ok<ConversationResponse>>(result.Result);
        Assert.Equal(id, ok.Value!.Id);
        Assert.Equal(6, ok.Value.Messages.Count);
        Assert.Equal(6, ok.Value.StoredMessageCount);
    }

    [Fact]
    public async Task Get_WithoutLimit_ReturnsTheNewest20Turns()
    {
        var id = await CreateConversationWithTurnsAsync(25);

        var result = await ConversationEndpoints.GetConversationAsync(id, null, null, repository, Ct);

        var ok = Assert.IsType<Ok<ConversationResponse>>(result.Result);
        Assert.Equal(40, ok.Value!.Messages.Count); // 20 turns
        Assert.Equal(50, ok.Value.StoredMessageCount);
        Assert.Equal(6, ok.Value.NextBeforeSequence);
    }

    [Fact]
    public async Task Get_LimitAbove100_IsCappedAt100Turns()
    {
        var id = await CreateConversationWithTurnsAsync(120);

        var result = await ConversationEndpoints.GetConversationAsync(id, 500, null, repository, Ct);

        var ok = Assert.IsType<Ok<ConversationResponse>>(result.Result);
        Assert.Equal(200, ok.Value!.Messages.Count); // 100 turns
        Assert.Equal(21, ok.Value.NextBeforeSequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Get_NonPositiveLimit_Returns400WithErrorResponse(int limit)
    {
        var id = await CreateConversationWithTurnsAsync(1);

        var result = await ConversationEndpoints.GetConversationAsync(id, limit, null, repository, Ct);

        var badRequest = Assert.IsType<BadRequest<ErrorResponse>>(result.Result);
        Assert.Equal("limit must be at least 1.", badRequest.Value!.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Get_NonPositiveCursor_Returns400WithErrorResponse(long beforeSequence)
    {
        var id = await CreateConversationWithTurnsAsync(1);

        var result = await ConversationEndpoints.GetConversationAsync(id, null, beforeSequence, repository, Ct);

        var badRequest = Assert.IsType<BadRequest<ErrorResponse>>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal("beforeSequence must be at least 1.", badRequest.Value!.Message);
    }

    [Fact]
    public async Task Get_Page_FlattensTurnsChronologically_WithCountAndCursor()
    {
        var id = await CreateConversationWithTurnsAsync(5);

        var result = await ConversationEndpoints.GetConversationAsync(id, 2, 4, repository, Ct);

        var ok = Assert.IsType<Ok<ConversationResponse>>(result.Result);
        var response = ok.Value!;
        Assert.Equal(id, response.Id);
        Assert.Equal("Test", response.Title);
        Assert.Equal(
            ["user: Question 2", "assistant: Answer 2", "user: Question 3", "assistant: Answer 3"],
            response.Messages.Select(m => $"{m.Role}: {m.Text}"));
        Assert.Equal(TestData.Turn(id, 3).CreatedAt, response.Messages[^1].At);
        Assert.Equal(10, response.StoredMessageCount);
        Assert.Equal(TestData.Turn(id, 5).CreatedAt, response.UpdatedAt);
        Assert.Equal(2, response.NextBeforeSequence);
    }
}
