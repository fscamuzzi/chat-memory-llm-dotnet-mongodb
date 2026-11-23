using ChatMemory.Api.Data;
using ChatMemory.Api.Models;

namespace ChatMemory.Tests;

// Repository semantics, proven on the in-memory fake.
// RepositoryIntegrationTests proves the same semantics on MongoDB.
public class InMemoryConversationRepositoryTests
{
    private const string UnknownId = "68a0000000000000000000aa";

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Create_ThenGetConversation_ReturnsMetadata()
    {
        var repository = new InMemoryConversationRepository();

        var created = await repository.CreateAsync("Trip planning", Ct);
        var loaded = await repository.GetConversationAsync(created.Id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("Trip planning", loaded.Title);
        Assert.Equal(created.CreatedAt, loaded.CreatedAt);
    }

    [Theory]
    [InlineData("not-an-object-id")]
    [InlineData(UnknownId)]
    public async Task UnknownOrInvalidId_ReturnsNullEverywhere(string id)
    {
        var repository = new InMemoryConversationRepository();

        Assert.Null(await repository.GetConversationAsync(id, Ct));
        Assert.Null(await repository.GetStateAsync(id, Ct));
        Assert.Null(await repository.GetPageAsync(id, 20, null, Ct));
    }

    [Fact]
    public async Task GetState_WithoutTurns_ReportsSequenceZeroAndNoCheckpoint()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Empty", Ct);

        var state = await repository.GetStateAsync(conversation.Id, Ct);

        Assert.NotNull(state);
        Assert.Equal(conversation.Id, state.Conversation.Id);
        Assert.Equal(0, state.LatestSequence);
        Assert.Null(state.Checkpoint);
    }

    [Fact]
    public async Task GetState_ReturnsLatestSequenceAndNewestCheckpoint()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Long", Ct);
        await repository.SeedTurnsAsync(conversation.Id, 2);
        await repository.InsertTurnAsync(
            TestData.Turn(conversation.Id, 3, new ReducerCheckpoint { SummaryText = "old", SummarizedThroughSequence = 1 }), Ct);
        await repository.InsertTurnAsync(TestData.Turn(conversation.Id, 4), Ct);
        await repository.InsertTurnAsync(
            TestData.Turn(conversation.Id, 5, new ReducerCheckpoint { SummaryText = "new", SummarizedThroughSequence = 3 }), Ct);
        await repository.InsertTurnAsync(TestData.Turn(conversation.Id, 6), Ct);

        var state = await repository.GetStateAsync(conversation.Id, Ct);

        Assert.NotNull(state);
        Assert.Equal(6, state.LatestSequence);
        Assert.NotNull(state.Checkpoint);
        Assert.Equal("new", state.Checkpoint.SummaryText);
        Assert.Equal(3, state.Checkpoint.SummarizedThroughSequence);
    }

    [Fact]
    public async Task GetTurnsAfter_ReturnsOnlyLaterTurns_OldestFirst()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Tail", Ct);
        await repository.SeedTurnsAsync(conversation.Id, 5);

        var tail = await repository.GetTurnsAfterAsync(conversation.Id, 3, Ct);

        Assert.Equal([4L, 5L], tail.Select(t => t.Sequence));
    }

    [Fact]
    public async Task GetLatestTurns_ReturnsNewestTurns_OldestFirst()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Latest", Ct);
        await repository.SeedTurnsAsync(conversation.Id, 5);

        var latest = await repository.GetLatestTurnsAsync(conversation.Id, 2, Ct);

        Assert.Equal([4L, 5L], latest.Select(t => t.Sequence));
    }

    [Fact]
    public async Task Reads_NeverReturnTurnsOfAnotherConversation()
    {
        var repository = new InMemoryConversationRepository();
        var first = await repository.CreateAsync("First", Ct);
        var second = await repository.CreateAsync("Second", Ct);
        await repository.SeedTurnsAsync(first.Id, 3);
        await repository.SeedTurnsAsync(second.Id, 1);

        var state = await repository.GetStateAsync(second.Id, Ct);
        var tail = await repository.GetTurnsAfterAsync(second.Id, 0, Ct);
        var latest = await repository.GetLatestTurnsAsync(second.Id, 10, Ct);
        var page = await repository.GetPageAsync(second.Id, 20, null, Ct);

        Assert.Equal(1, state!.LatestSequence);
        Assert.Single(tail);
        Assert.Single(latest);
        Assert.Single(page!.Turns);
        Assert.Equal(2, page.StoredMessageCount);
    }

    [Fact]
    public async Task GetPage_WithoutCursor_ReturnsNewestTurns_AndCursorToOlderOnes()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Paged", Ct);
        await repository.SeedTurnsAsync(conversation.Id, 5);

        var page = await repository.GetPageAsync(conversation.Id, 2, null, Ct);

        Assert.NotNull(page);
        Assert.Equal([4L, 5L], page.Turns.Select(t => t.Sequence));
        Assert.Equal(4, page.NextBeforeSequence);
        Assert.Equal(10, page.StoredMessageCount);
        Assert.Equal(page.Turns[^1].CreatedAt, page.UpdatedAt);
    }

    [Fact]
    public async Task GetPage_Cursor_IsExclusive_AndEndsWithNullCursor()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Paged", Ct);
        await repository.SeedTurnsAsync(conversation.Id, 5);

        var middle = await repository.GetPageAsync(conversation.Id, 2, 4, Ct);
        var last = await repository.GetPageAsync(conversation.Id, 2, middle!.NextBeforeSequence, Ct);

        Assert.Equal([2L, 3L], middle.Turns.Select(t => t.Sequence));
        Assert.Equal(2, middle.NextBeforeSequence);
        Assert.Equal([1L], last!.Turns.Select(t => t.Sequence));
        Assert.Null(last.NextBeforeSequence);
        Assert.Equal(10, last.StoredMessageCount);
    }

    [Fact]
    public async Task GetPage_ThatEndsExactlyAtTheFirstTurn_HasNoCursor()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Paged", Ct);
        await repository.SeedTurnsAsync(conversation.Id, 4);

        var page = await repository.GetPageAsync(conversation.Id, 2, 3, Ct);

        Assert.Equal([1L, 2L], page!.Turns.Select(t => t.Sequence));
        Assert.Null(page.NextBeforeSequence);
    }

    [Fact]
    public async Task GetPage_WithoutTurns_UsesCreatedAtAsUpdatedAt()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Empty", Ct);

        var page = await repository.GetPageAsync(conversation.Id, 20, null, Ct);

        Assert.NotNull(page);
        Assert.Empty(page.Turns);
        Assert.Equal(0, page.StoredMessageCount);
        Assert.Equal(conversation.CreatedAt, page.UpdatedAt);
        Assert.Null(page.NextBeforeSequence);
    }

    [Fact]
    public async Task InsertTurn_StoresTheTurn_AndAssignsAnId()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Insert", Ct);
        var checkpoint = new ReducerCheckpoint { SummaryText = "summary", SummarizedThroughSequence = 0 };

        var result = await repository.InsertTurnAsync(TestData.Turn(conversation.Id, 1, checkpoint), Ct);

        Assert.Equal(InsertTurnResult.Succeeded, result);
        var stored = Assert.Single(await repository.GetTurnsAfterAsync(conversation.Id, 0, Ct));
        Assert.False(string.IsNullOrEmpty(stored.Id));
        Assert.Equal("Question 1", stored.UserText);
        Assert.Equal("Answer 1", stored.AssistantText);
        Assert.Equal("summary", stored.CheckpointCreated!.SummaryText);
    }

    [Fact]
    public async Task InsertTurn_WithUsedSequence_ReturnsConflict_AndKeepsTheFirstTurn()
    {
        var repository = new InMemoryConversationRepository();
        var conversation = await repository.CreateAsync("Conflict", Ct);
        await repository.InsertTurnAsync(TestData.Turn(conversation.Id, 1), Ct);
        var competing = TestData.Turn(conversation.Id, 1);
        competing.UserText = "Competing question";

        var result = await repository.InsertTurnAsync(competing, Ct);

        Assert.Equal(InsertTurnResult.SequenceConflict, result);
        var stored = Assert.Single(await repository.GetTurnsAfterAsync(conversation.Id, 0, Ct));
        Assert.Equal("Question 1", stored.UserText);
    }
}
