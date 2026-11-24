using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace ChatMemory.Tests;

public class ChatReducerTests
{
    private const string SystemPrompt = "You are a test assistant.";

    private static readonly CancellationToken Ct = CancellationToken.None;

    // Build the reducer under test with the test system prompt.
    private static ConversationMemoryReducer BuildReducer(
        FakeChatClient chatClient, InMemoryConversationRepository repository, ChatMemoryOptions options)
    {
        options.SystemPrompt = SystemPrompt;
        return new ConversationMemoryReducer(chatClient, repository, Options.Create(options));
    }

    // Create a conversation with `turns` completed turns; the last one may carry a checkpoint.
    private static async Task<(InMemoryConversationRepository Repository, string ConversationId)> SeedAsync(
        int turns, ReducerCheckpoint? checkpointOnLastTurn = null)
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Test", Ct);
        await repository.SeedTurnsAsync(conversation.Id, checkpointOnLastTurn is null ? turns : turns - 1);
        if (checkpointOnLastTurn is not null)
            await repository.InsertTurnAsync(TestData.Turn(conversation.Id, turns, checkpointOnLastTurn), Ct);

        repository.TurnReads.Clear();
        return (repository, conversation.Id);
    }

    // Load the conversation state and reduce it for `userText`, as ChatService does.
    private static async Task<ReductionResult> ReduceAsync(
        ConversationMemoryReducer reducer, InMemoryConversationRepository repository, string conversationId, string userText)
    {
        var state = await repository.GetStateAsync(conversationId, Ct);
        return await reducer.ReduceAsync(state!, userText, Ct);
    }

    // The text of each message, for compact assertions.
    private static string[] Texts(IEnumerable<ChatMessage> messages) => messages.Select(m => m.Text).ToArray();

    [Fact]
    public async Task None_SendsSystemPrompt_EveryStoredTurn_AndTheNewUserMessage()
    {
        var (repository, id) = await SeedAsync(turns: 12);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new() { Strategy = ChatMemoryStrategy.None });

        var result = await ReduceAsync(reducer, repository, id, "Latest question");

        Assert.Equal(26, result.Prompt.Count); // system + 24 stored + new user
        Assert.Equal(ChatRole.System, result.Prompt[0].Role);
        Assert.Equal(SystemPrompt, result.Prompt[0].Text);
        Assert.Equal("Question 1", result.Prompt[1].Text);
        Assert.Equal("Answer 12", result.Prompt[^2].Text);
        Assert.Equal(ChatRole.User, result.Prompt[^1].Role);
        Assert.Equal("Latest question", result.Prompt[^1].Text);
        Assert.Null(result.NewCheckpoint);
        Assert.False(result.SummaryRefreshed);
        Assert.Empty(chatClient.SummaryCalls);
    }

    [Fact]
    public async Task Counting_LoadsOnlyTheNeededTurns_AndKeepsSystemPlusTargetRecentMessages()
    {
        var (repository, id) = await SeedAsync(turns: 10);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(
            chatClient, repository, new() { Strategy = ChatMemoryStrategy.Counting, TargetMessageCount = 4 });

        var result = await ReduceAsync(reducer, repository, id, "Latest question");

        // Target 4 = the new user message + 3 stored messages, which live in the 2 newest turns.
        Assert.Equal(["latest:2"], repository.TurnReads);
        Assert.Equal(
            [SystemPrompt, "Answer 9", "Question 10", "Answer 10", "Latest question"],
            Texts(result.Prompt));
        Assert.Null(result.NewCheckpoint);
        Assert.False(result.SummaryRefreshed);
        Assert.Empty(chatClient.SummaryCalls);
    }

    [Fact]
    public async Task Counting_BelowTarget_SendsTheWholeShortHistory()
    {
        var (repository, id) = await SeedAsync(turns: 2);
        var reducer = BuildReducer(
            new FakeChatClient(), repository, new() { Strategy = ChatMemoryStrategy.Counting, TargetMessageCount = 10 });

        var result = await ReduceAsync(reducer, repository, id, "Latest question");

        Assert.Equal(6, result.Prompt.Count); // system + 4 stored + new user
    }

    [Fact]
    public async Task Summarizing_UnderThreshold_SendsTheRawTail_WithoutASummaryCall()
    {
        // 7 turns = 14 stored messages; with the new user message the tail is 15 = target 10 + threshold 5.
        var (repository, id) = await SeedAsync(turns: 7);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        var result = await ReduceAsync(reducer, repository, id, "Question 8");

        Assert.Equal(16, result.Prompt.Count); // system + 14 stored + new user
        Assert.Empty(chatClient.SummaryCalls);
        Assert.Null(result.NewCheckpoint);
        Assert.False(result.SummaryRefreshed);
    }

    [Fact]
    public async Task Summarizing_DefaultSettings_NinthRequestAfterEightTurns_Sends13Messages()
    {
        // The documented example: target 10, threshold 5, eight completed turns, ninth request.
        var (repository, id) = await SeedAsync(turns: 8);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        var result = await ReduceAsync(reducer, repository, id, "Question 9");

        // 1) One summary call, over the three oldest turns.
        var summaryCall = Assert.Single(chatClient.SummaryCalls);
        Assert.Equal(
            ["Question 1", "Answer 1", "Question 2", "Answer 2", "Question 3", "Answer 3"],
            Texts(summaryCall.SkipLast(1)));

        // 2) The durable checkpoint holds the summary text and the last summarized turn.
        Assert.True(result.SummaryRefreshed);
        Assert.NotNull(result.NewCheckpoint);
        Assert.Equal("SUMMARY 1", result.NewCheckpoint.SummaryText);
        Assert.Equal(3, result.NewCheckpoint.SummarizedThroughSequence);

        // 3) The answer prompt is system + summary + 11 retained messages = 13.
        Assert.Equal(13, result.Prompt.Count);
        Assert.Equal(ChatRole.System, result.Prompt[0].Role);
        Assert.Equal(ChatRole.Assistant, result.Prompt[1].Role);
        Assert.Equal("SUMMARY 1", result.Prompt[1].Text);
        Assert.Equal(
            [
                "Question 4", "Answer 4", "Question 5", "Answer 5", "Question 6", "Answer 6",
                "Question 7", "Answer 7", "Question 8", "Answer 8", "Question 9",
            ],
            Texts(result.Prompt.Skip(2)));
    }

    [Theory]
    [InlineData(3, 1, 3, 2)] // ideal cut already at a user message
    [InlineData(4, 1, 3, 1)] // ideal cut at an assistant message: moved back to its user message
    [InlineData(10, 5, 8, 3)]
    [InlineData(2, 3, 6, 5)]
    public async Task Summarizing_Cutoff_NeverSplitsAUserAssistantPair(
        int target, int threshold, int turns, long expectedCutoff)
    {
        var (repository, id) = await SeedAsync(turns);
        var reducer = BuildReducer(
            new FakeChatClient(),
            repository,
            new() { TargetMessageCount = target, SummarizationThreshold = threshold });

        var result = await ReduceAsync(reducer, repository, id, "Latest question");

        Assert.Equal(expectedCutoff, result.NewCheckpoint!.SummarizedThroughSequence);
        // The first raw message after the summary opens the first turn that is not summarized.
        Assert.Equal(ChatRole.User, result.Prompt[2].Role);
        Assert.Equal($"Question {expectedCutoff + 1}", result.Prompt[2].Text);
        Assert.Equal($"Answer {expectedCutoff + 1}", result.Prompt[3].Text);
    }

    [Fact]
    public async Task Summarizing_WithCheckpointUnderThreshold_ReusesIt_WithoutASummaryCall()
    {
        var checkpoint = new ReducerCheckpoint { SummaryText = "Stored summary", SummarizedThroughSequence = 3 };
        var (repository, id) = await SeedAsync(turns: 9, checkpoint);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        var result = await ReduceAsync(reducer, repository, id, "Question 10");

        Assert.Empty(chatClient.SummaryCalls);
        Assert.Equal(["after:3"], repository.TurnReads); // only the unsummarized tail is loaded
        Assert.Null(result.NewCheckpoint);
        Assert.False(result.SummaryRefreshed);
        Assert.Equal(15, result.Prompt.Count); // system + summary + 12 stored + new user
        Assert.Equal(ChatRole.Assistant, result.Prompt[1].Role);
        Assert.Equal("Stored summary", result.Prompt[1].Text);
        Assert.Equal("Question 4", result.Prompt[2].Text);
        Assert.Equal("Question 10", result.Prompt[^1].Text);
    }

    [Fact]
    public async Task Summarizing_SummaryRequest_EndsWithTheInstructionAsAUserMessage()
    {
        var (repository, id) = await SeedAsync(turns: 8);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        await ReduceAsync(reducer, repository, id, "Question 9");

        // 1) No system message: a model whose template ignores a trailing system message
        //    would continue the last answer instead of writing a summary.
        var summaryCall = Assert.Single(chatClient.SummaryCalls);
        Assert.DoesNotContain(summaryCall, message => message.Role == ChatRole.System);

        // 2) The instruction is the final user message: the official prompt, then the extra rules.
        var instruction = summaryCall[^1];
        Assert.Equal(ChatRole.User, instruction.Role);
        Assert.StartsWith("**Generate a clear and complete summary", instruction.Text);
        Assert.EndsWith(ConversationMemoryReducer.SummaryRules, instruction.Text);
        Assert.Contains("Keep every name, date, time, number and place", ConversationMemoryReducer.SummaryRules);
    }

    [Fact]
    public void OfficialSummarizationPrompt_StillContainsTheSentenceLimitThatTheReducerReplaces()
    {
        // The reducer replaces this phrase. If a library update changes the official prompt,
        // this test fails first and names the cause.
        var official = new SummarizingChatReducer(new FakeChatClient(), 10, 5).SummarizationPrompt;

        Assert.Contains(ConversationMemoryReducer.OfficialSentenceLimit, official);
    }

    [Fact]
    public async Task Summarizing_FirstSummary_KeepsTheOfficialFiveSentenceLimit()
    {
        var (repository, id) = await SeedAsync(turns: 8);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        await ReduceAsync(reducer, repository, id, "Question 9");

        // A longer first summary fills with details and leaves less room for the next one.
        var instruction = Assert.Single(chatClient.SummaryCalls)[^1].Text;
        Assert.Equal("no more than five sentences", ConversationMemoryReducer.OfficialSentenceLimit);
        Assert.Contains(ConversationMemoryReducer.OfficialSentenceLimit, instruction);
        Assert.DoesNotContain(ConversationMemoryReducer.SummarySentenceLimit, instruction);
    }

    [Fact]
    public async Task Summarizing_LaterSummary_AsksForTenSentencesInsteadOfFive()
    {
        // A stored checkpoint means this request folds a previous summary into the new one.
        var checkpoint = new ReducerCheckpoint { SummaryText = "Stored summary", SummarizedThroughSequence = 3 };
        var (repository, id) = await SeedAsync(turns: 11, checkpoint);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        await ReduceAsync(reducer, repository, id, "Question 12");

        // Five sentences cannot hold the facts of the previous summary plus the new turns.
        var instruction = Assert.Single(chatClient.SummaryCalls)[^1].Text;
        Assert.Equal("no more than ten sentences", ConversationMemoryReducer.SummarySentenceLimit);
        Assert.Contains(ConversationMemoryReducer.SummarySentenceLimit, instruction);
        Assert.DoesNotContain("five sentences", instruction);
    }

    [Fact]
    public async Task Summarizing_SummaryRequest_UsesTemperatureZero_AndTheAnswerDoesNot()
    {
        var (repository, id) = await SeedAsync(turns: 8);
        var chatClient = new FakeChatClient();
        var reducer = BuildReducer(chatClient, repository, new());

        await ReduceAsync(reducer, repository, id, "Question 9");

        // The reducer made one model call here: the summary call.
        Assert.Single(chatClient.SummaryCalls);
        Assert.Equal(0f, Assert.Single(chatClient.ReceivedOptions)?.Temperature);
    }

    [Fact]
    public async Task Summarizing_WithCheckpoint_RefreshesOnlyWhenTheTailExceedsTargetPlusThreshold()
    {
        var checkpoint = new ReducerCheckpoint { SummaryText = "Stored summary", SummarizedThroughSequence = 3 };

        // Turns 4..10 = 14 stored messages + new user = 15: not above 15, no refresh.
        var (atLimit, atLimitId) = await SeedAsync(turns: 10, checkpoint);
        var atLimitClient = new FakeChatClient();
        var atLimitResult = await ReduceAsync(
            BuildReducer(atLimitClient, atLimit, new()), atLimit, atLimitId, "Question 11");

        // Turns 4..11 = 16 stored messages + new user = 17: above 15, refresh.
        var (overLimit, overLimitId) = await SeedAsync(turns: 11, checkpoint);
        var overLimitClient = new FakeChatClient();
        var overLimitResult = await ReduceAsync(
            BuildReducer(overLimitClient, overLimit, new()), overLimit, overLimitId, "Question 12");

        Assert.Empty(atLimitClient.SummaryCalls);
        Assert.False(atLimitResult.SummaryRefreshed);

        // The refresh folds the previous summary and turns 4..6 into the new summary.
        var summaryCall = Assert.Single(overLimitClient.SummaryCalls);
        Assert.Equal(
            [
                "Previous summary: Stored summary",
                "Question 4", "Answer 4", "Question 5", "Answer 5", "Question 6", "Answer 6",
            ],
            Texts(summaryCall.SkipLast(1)));
        Assert.True(overLimitResult.SummaryRefreshed);
        Assert.Equal("SUMMARY 1", overLimitResult.NewCheckpoint!.SummaryText);
        Assert.Equal(6, overLimitResult.NewCheckpoint.SummarizedThroughSequence);
        Assert.Equal(13, overLimitResult.Prompt.Count);
        Assert.Equal("SUMMARY 1", overLimitResult.Prompt[1].Text);
        Assert.Equal("Question 7", overLimitResult.Prompt[2].Text);
    }

    [Fact]
    public async Task Summarizing_NewReducerInstance_ReusesThePersistedCheckpoint()
    {
        // 1) The first reducer refreshes the summary on the ninth request.
        var (repository, id) = await SeedAsync(turns: 8);
        var first = await ReduceAsync(
            BuildReducer(new FakeChatClient(), repository, new()), repository, id, "Question 9");

        // 2) The turn is stored with its checkpoint, as ChatService does.
        await repository.InsertTurnAsync(TestData.Turn(id, 9, first.NewCheckpoint), Ct);

        // 3) A new reducer and a new chat client stand for a restarted process.
        var restartedClient = new FakeChatClient();
        var second = await ReduceAsync(
            BuildReducer(restartedClient, repository, new()), repository, id, "Question 10");

        Assert.Empty(restartedClient.SummaryCalls);
        Assert.False(second.SummaryRefreshed);
        Assert.Null(second.NewCheckpoint);
        Assert.Equal("SUMMARY 1", second.Prompt[1].Text);
        Assert.Equal(15, second.Prompt.Count); // system + summary + turns 4..9 + new user
    }

    [Fact]
    public async Task Summarizing_NeverExposesTheFrameworkPrivateSummaryMarker()
    {
        const string privateMarker = "__summary__";
        var (repository, id) = await SeedAsync(turns: 8);
        var reducer = BuildReducer(new FakeChatClient(), repository, new());

        var result = await ReduceAsync(reducer, repository, id, "Question 9");
        var turn = TestData.Turn(id, 9, result.NewCheckpoint);
        await repository.InsertTurnAsync(turn, Ct);

        // Neither the answer prompt nor the document that MongoDB stores carries the marker.
        Assert.All(
            result.Prompt,
            m => Assert.True(m.AdditionalProperties is null || !m.AdditionalProperties.ContainsKey(privateMarker)));
        Assert.DoesNotContain(privateMarker, turn.ToBsonDocument().ToJson());
    }
}
