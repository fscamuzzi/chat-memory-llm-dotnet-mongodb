using ChatMemory.Api.Data;
using ChatMemory.Api.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatMemory.IntegrationTests;

// The repository semantics of InMemoryConversationRepositoryTests, proven on a real MongoDB.
[Collection(MongoCollection.Name)]
public class RepositoryIntegrationTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Epoch = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IMongoDatabase database = mongo.GetDatabase(MongoFixture.NewDatabaseName());
    private MongoConversationRepository repository = null!;

    // Create the index and the repository on a database that only this test uses.
    public async Task InitializeAsync()
    {
        await new ConversationIndexInitializer(database).StartAsync(Ct);
        repository = new MongoConversationRepository(database);
    }

    // Drop the database of this test.
    public Task DisposeAsync() => database.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);

    // A completed turn with predictable text: "Question n" / "Answer n".
    private static ConversationTurn Turn(string conversationId, long sequence, ReducerCheckpoint? checkpoint = null) =>
        new()
        {
            ConversationId = conversationId,
            Sequence = sequence,
            UserText = $"Question {sequence}",
            AssistantText = $"Answer {sequence}",
            CreatedAt = Epoch.AddMinutes(sequence),
            CheckpointCreated = checkpoint,
        };

    // Create a conversation with `turns` completed turns and return its id.
    private async Task<string> SeedAsync(int turns)
    {
        var conversation = await repository.CreateAsync("Seeded", Ct);
        foreach (var sequence in Enumerable.Range(1, turns))
            Assert.Equal(InsertTurnResult.Succeeded, await repository.InsertTurnAsync(Turn(conversation.Id, sequence), Ct));

        return conversation.Id;
    }

    [Fact]
    public async Task Initializer_CreatesExactlyOneUniqueAscendingCompoundIndex()
    {
        var indexes = await (await database.GetCollection<BsonDocument>(MongoConversationRepository.TurnsCollection)
            .Indexes.ListAsync(Ct)).ToListAsync(Ct);

        var index = Assert.Single(indexes, i => i["name"] == ConversationIndexInitializer.TurnSequenceIndexName);
        Assert.True(index["unique"].AsBoolean);
        Assert.Equal(new BsonDocument { { "ConversationId", 1 }, { "Sequence", 1 } }, index["key"].AsBsonDocument);
        Assert.Equal(2, indexes.Count); // _id + the compound index, no redundant descending index
    }

    [Fact]
    public async Task Initializer_CanRunAgain_WithoutError()
    {
        await new ConversationIndexInitializer(database).StartAsync(Ct);
    }

    [Fact]
    public async Task InsertTurn_WithUsedSequence_ReturnsConflict_AndKeepsTheFirstTurn()
    {
        var id = await SeedAsync(1);
        var competing = Turn(id, 1);
        competing.UserText = "Competing question";

        var result = await repository.InsertTurnAsync(competing, Ct);

        Assert.Equal(InsertTurnResult.SequenceConflict, result);
        var stored = Assert.Single(await repository.GetTurnsAfterAsync(id, 0, Ct));
        Assert.Equal("Question 1", stored.UserText);
    }

    [Fact]
    public async Task SameSequence_InAnotherConversation_IsNotAConflict()
    {
        var first = await SeedAsync(1);
        var second = await repository.CreateAsync("Other", Ct);

        var result = await repository.InsertTurnAsync(Turn(second.Id, 1), Ct);

        Assert.Equal(InsertTurnResult.Succeeded, result);
        Assert.Single(await repository.GetTurnsAfterAsync(first, 0, Ct));
    }

    [Theory]
    [InlineData("not-an-object-id")]
    [InlineData("68a0000000000000000000aa")]
    public async Task UnknownOrInvalidId_ReturnsNullEverywhere(string id)
    {
        Assert.Null(await repository.GetConversationAsync(id, Ct));
        Assert.Null(await repository.GetStateAsync(id, Ct));
        Assert.Null(await repository.GetPageAsync(id, 20, null, Ct));
    }

    [Fact]
    public async Task GetState_WithoutTurns_ReportsSequenceZeroAndNoCheckpoint()
    {
        var conversation = await repository.CreateAsync("Empty", Ct);

        var state = await repository.GetStateAsync(conversation.Id, Ct);

        Assert.Equal("Empty", state!.Conversation.Title);
        Assert.Equal(0, state.LatestSequence);
        Assert.Null(state.Checkpoint);
    }

    [Fact]
    public async Task GetState_ReturnsLatestSequenceAndNewestCheckpoint()
    {
        var id = await SeedAsync(2);
        await repository.InsertTurnAsync(
            Turn(id, 3, new ReducerCheckpoint { SummaryText = "old", SummarizedThroughSequence = 1 }), Ct);
        await repository.InsertTurnAsync(Turn(id, 4), Ct);
        await repository.InsertTurnAsync(
            Turn(id, 5, new ReducerCheckpoint { SummaryText = "new", SummarizedThroughSequence = 3 }), Ct);
        await repository.InsertTurnAsync(Turn(id, 6), Ct);

        var state = await repository.GetStateAsync(id, Ct);

        Assert.Equal(6, state!.LatestSequence);
        Assert.Equal("new", state.Checkpoint!.SummaryText);
        Assert.Equal(3, state.Checkpoint.SummarizedThroughSequence);
    }

    [Fact]
    public async Task TurnReads_ReturnTheRequestedTurns_OldestFirst()
    {
        var id = await SeedAsync(5);
        await SeedAsync(3); // another conversation must never leak into the reads

        var tail = await repository.GetTurnsAfterAsync(id, 3, Ct);
        var latest = await repository.GetLatestTurnsAsync(id, 2, Ct);

        Assert.Equal([4L, 5L], tail.Select(t => t.Sequence));
        Assert.Equal([4L, 5L], latest.Select(t => t.Sequence));
        Assert.Equal("Answer 5", latest[^1].AssistantText);
    }

    [Fact]
    public async Task GetPage_WalksBackwards_WithAnExclusiveCursor()
    {
        var id = await SeedAsync(5);

        var newest = await repository.GetPageAsync(id, 2, null, Ct);
        var middle = await repository.GetPageAsync(id, 2, newest!.NextBeforeSequence, Ct);
        var oldest = await repository.GetPageAsync(id, 2, middle!.NextBeforeSequence, Ct);

        Assert.Equal([4L, 5L], newest.Turns.Select(t => t.Sequence));
        Assert.Equal(4, newest.NextBeforeSequence);
        Assert.Equal([2L, 3L], middle.Turns.Select(t => t.Sequence));
        Assert.Equal(2, middle.NextBeforeSequence);
        Assert.Equal([1L], oldest!.Turns.Select(t => t.Sequence));
        Assert.Null(oldest.NextBeforeSequence);
        Assert.All([newest, middle, oldest], page => Assert.Equal(10, page.StoredMessageCount));
        Assert.Equal(Epoch.AddMinutes(5), newest.UpdatedAt);
    }

    [Fact]
    public async Task GetPage_WithoutTurns_UsesCreatedAtAsUpdatedAt()
    {
        var conversation = await repository.CreateAsync("Empty", Ct);

        var page = await repository.GetPageAsync(conversation.Id, 20, null, Ct);

        Assert.Empty(page!.Turns);
        Assert.Equal(0, page.StoredMessageCount);
        Assert.Equal(page.Conversation.CreatedAt, page.UpdatedAt);
        Assert.Null(page.NextBeforeSequence);
    }
}
