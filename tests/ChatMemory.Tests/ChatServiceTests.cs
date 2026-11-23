using ChatMemory.Api.Contracts;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatMemory.Tests;

public class ChatServiceTests
{
    private const string UnknownId = "68a0000000000000000000aa";

    private static readonly CancellationToken Ct = CancellationToken.None;

    // Build the service under test with a real reducer over the fakes.
    private static ChatService BuildService(
        FakeChatClient chatClient, InMemoryConversationRepository repository, ChatMemoryOptions options)
    {
        var wrapped = Options.Create(options);
        return new ChatService(
            chatClient,
            repository,
            new ConversationMemoryReducer(chatClient, repository, wrapped),
            wrapped,
            NullLogger<ChatService>.Instance);
    }

    // Create a conversation that already has `turns` completed turns.
    private static async Task<(InMemoryConversationRepository Repository, string ConversationId)> SeedAsync(int turns)
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Test", Ct);
        await repository.SeedTurnsAsync(conversation.Id, turns);
        return (repository, conversation.Id);
    }

    // Script the answer model: the n-th answer call returns "answer n".
    private static FakeChatClient NumberedAnswers()
    {
        var chatClient = new FakeChatClient();
        chatClient.OnAnswer = _ => $"answer {chatClient.AnswerCalls.Count}";
        return chatClient;
    }

    [Fact]
    public async Task Send_StoresOneCompletedTurn_AndReportsTheMetrics()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        var service = BuildService(
            new FakeChatClient("Ciao!"),
            repository,
            new() { Strategy = ChatMemoryStrategy.None, SystemPrompt = "Be brief." });

        var result = await service.SendAsync(id, new SendMessageRequest("Hello"), Ct);

        Assert.Equal(SendMessageStatus.Succeeded, result.Status);
        Assert.NotNull(result.Response);
        Assert.Equal("Ciao!", result.Response.Reply);
        Assert.Equal(2, result.Response.StoredMessageCount);  // user + assistant
        Assert.Equal(2, result.Response.MessagesSentToModel); // system + user
        Assert.Equal(["system: Be brief.", "user: Hello"], result.Response.PromptPreview);
        Assert.False(result.Response.SummaryRefreshed);
        var turn = Assert.Single(await repository.GetTurnsAfterAsync(id, 0, Ct));
        Assert.Equal(1, turn.Sequence);
        Assert.Equal("Hello", turn.UserText);
        Assert.Equal("Ciao!", turn.AssistantText);
        Assert.Null(turn.CheckpointCreated);
    }

    [Fact]
    public async Task Send_PromptPreview_CutsEachTextAt80Characters()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        var service = BuildService(new FakeChatClient(), repository, new() { Strategy = ChatMemoryStrategy.None });
        var longText = new string('a', 80) + new string('b', 20);

        var result = await service.SendAsync(id, new SendMessageRequest(longText), Ct);

        Assert.Equal($"user: {new string('a', 80)}…", result.Response!.PromptPreview[^1]);
    }

    [Fact]
    public async Task Send_DefaultSettings_NinthTurnReports18Stored_And13SentToTheModel()
    {
        // The documented example: target 10, threshold 5, eight completed turns.
        var (repository, id) = await SeedAsync(turns: 8);
        var chatClient = new FakeChatClient();
        var service = BuildService(chatClient, repository, new());

        var ninth = await service.SendAsync(id, new SendMessageRequest("Question 9"), Ct);
        var tenth = await service.SendAsync(id, new SendMessageRequest("Question 10"), Ct);

        // 1) Ninth turn: 18 messages stored, 13 sent (system + summary + 11 retained), summary refreshed.
        Assert.Equal(18, ninth.Response!.StoredMessageCount);
        Assert.Equal(13, ninth.Response.MessagesSentToModel);
        Assert.True(ninth.Response.SummaryRefreshed);
        Assert.Equal(13, ninth.Response.PromptPreview.Count);
        Assert.Equal("assistant: SUMMARY 1", ninth.Response.PromptPreview[1]);
        Assert.Equal(13, chatClient.AnswerCalls[0].Count);

        // 2) The checkpoint is stored inside the ninth turn document.
        var state = await repository.GetStateAsync(id, Ct);
        Assert.Equal("SUMMARY 1", state!.Checkpoint!.SummaryText);
        Assert.Equal(3, state.Checkpoint.SummarizedThroughSequence);

        // 3) Tenth turn: the stored summary is reused; no second summary call.
        Assert.Equal(20, tenth.Response!.StoredMessageCount);
        Assert.Equal(15, tenth.Response.MessagesSentToModel);
        Assert.False(tenth.Response.SummaryRefreshed);
        Assert.Single(chatClient.SummaryCalls);
    }

    [Theory]
    [InlineData(UnknownId)]
    [InlineData("not-an-object-id")]
    public async Task Send_UnknownConversation_ReturnsNotFound_WithoutAModelCall(string id)
    {
        var chatClient = new FakeChatClient();
        var service = BuildService(chatClient, new InMemoryConversationRepository(), new());

        var result = await service.SendAsync(id, new SendMessageRequest("Hi"), Ct);

        Assert.Equal(SendMessageStatus.ConversationNotFound, result.Status);
        Assert.Null(result.Response);
        Assert.Empty(chatClient.AnswerCalls);
        Assert.Empty(chatClient.SummaryCalls);
    }

    [Fact]
    public async Task Send_WithUppercaseHexId_ContinuesTheSameConversation()
    {
        // MongoDB accepts the id in any hex case; the turns must still land in one conversation.
        var (repository, id) = await SeedAsync(turns: 2);
        var service = BuildService(new FakeChatClient(), repository, new() { Strategy = ChatMemoryStrategy.None });

        var result = await service.SendAsync(id.ToUpperInvariant(), new SendMessageRequest("Third question"), Ct);

        Assert.Equal(6, result.Response!.StoredMessageCount);
        Assert.Equal(6, result.Response.MessagesSentToModel); // system + 2 stored pairs + new user
        var turns = await repository.GetTurnsAfterAsync(id, 0, Ct);
        Assert.Equal([1L, 2L, 3L], turns.Select(t => t.Sequence));
        Assert.Equal(id, turns[^1].ConversationId);
    }

    [Fact]
    public async Task Send_WhenTheModelFails_StoresNoTurnAndNoCheckpoint()
    {
        // Eight turns: the request refreshes the summary first, then the answer call fails.
        var (repository, id) = await SeedAsync(turns: 8);
        var chatClient = new FakeChatClient { OnAnswer = _ => throw new InvalidOperationException("model down") };
        var service = BuildService(chatClient, repository, new());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendAsync(id, new SendMessageRequest("Question 9"), Ct));

        var state = await repository.GetStateAsync(id, Ct);
        Assert.Single(chatClient.SummaryCalls);
        Assert.Equal(8, state!.LatestSequence);
        Assert.Null(state.Checkpoint);
    }

    [Fact]
    public async Task Send_WhenCancelledDuringTheModelCall_StoresNoTurn()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        using var cancellation = new CancellationTokenSource();
        var chatClient = new FakeChatClient
        {
            OnAnswer = _ =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return "never returned";
            },
        };
        var service = BuildService(chatClient, repository, new());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SendAsync(id, new SendMessageRequest("Hello"), cancellation.Token));

        Assert.Equal(0, (await repository.GetStateAsync(id, Ct))!.LatestSequence);
    }

    [Fact]
    public async Task Send_AfterOneSequenceConflict_RegeneratesFromReloadedState_AndReturnsOnlyTheStoredReply()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        repository.ConflictsToInject = 1;
        var chatClient = NumberedAnswers();
        var service = BuildService(chatClient, repository, new() { Strategy = ChatMemoryStrategy.None });

        var result = await service.SendAsync(id, new SendMessageRequest("My question"), Ct);

        // 1) The reply is the regenerated one; the stale first answer is discarded.
        Assert.Equal(SendMessageStatus.Succeeded, result.Status);
        Assert.Equal("answer 2", result.Response!.Reply);
        Assert.Equal(2, chatClient.AnswerCalls.Count);

        // 2) The second prompt was built from reloaded state: it contains the winning turn.
        Assert.Contains(chatClient.AnswerCalls[1], m => m.Text == "Competing answer 1");
        Assert.Equal(4, result.Response.MessagesSentToModel); // system + winning pair + user
        Assert.Equal(4, result.Response.StoredMessageCount);

        // 3) Stored sequences are consecutive: the winner at 1, this request at 2.
        var turns = await repository.GetTurnsAfterAsync(id, 0, Ct);
        Assert.Equal([1L, 2L], turns.Select(t => t.Sequence));
        Assert.Equal("Competing answer 1", turns[0].AssistantText);
        Assert.Equal("My question", turns[1].UserText);
        Assert.Equal("answer 2", turns[1].AssistantText);
    }

    [Fact]
    public async Task Send_AfterASecondConflict_ReturnsConflict_AndNeverAStaleReply()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        repository.ConflictsToInject = 2;
        var chatClient = NumberedAnswers();
        var service = BuildService(chatClient, repository, new() { Strategy = ChatMemoryStrategy.None });

        var result = await service.SendAsync(id, new SendMessageRequest("My question"), Ct);

        Assert.Equal(SendMessageStatus.Conflict, result.Status);
        Assert.Null(result.Response);
        Assert.Equal(2, chatClient.AnswerCalls.Count); // first attempt + one retry
        var turns = await repository.GetTurnsAfterAsync(id, 0, Ct);
        Assert.Equal(2, turns.Count);
        Assert.DoesNotContain(turns, t => t.UserText == "My question");
    }

    [Fact]
    public async Task Send_WithZeroRetries_ReturnsConflictAfterTheFirstConflict()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        repository.ConflictsToInject = 1;
        var chatClient = NumberedAnswers();
        var service = BuildService(
            chatClient, repository, new() { Strategy = ChatMemoryStrategy.None, MaxConcurrencyRetries = 0 });

        var result = await service.SendAsync(id, new SendMessageRequest("My question"), Ct);

        Assert.Equal(SendMessageStatus.Conflict, result.Status);
        Assert.Null(result.Response);
        Assert.Single(chatClient.AnswerCalls);
    }

    [Fact]
    public async Task Send_WithTwoRetries_SucceedsOnTheThirdAttempt()
    {
        var (repository, id) = await SeedAsync(turns: 0);
        repository.ConflictsToInject = 2;
        var chatClient = NumberedAnswers();
        var service = BuildService(
            chatClient, repository, new() { Strategy = ChatMemoryStrategy.None, MaxConcurrencyRetries = 2 });

        var result = await service.SendAsync(id, new SendMessageRequest("My question"), Ct);

        Assert.Equal(SendMessageStatus.Succeeded, result.Status);
        Assert.Equal("answer 3", result.Response!.Reply);
        Assert.Equal(6, result.Response.StoredMessageCount);
    }
}
