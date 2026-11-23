using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatMemory.Api.Contracts;
using ChatMemory.Api.Data;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatMemory.IntegrationTests;

// The real API host over HTTP against a real MongoDB. Only the chat model is a deterministic fake.
[Collection(MongoCollection.Name)]
public class ConversationApiTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string UnknownId = "68a0000000000000000000aa";

    private readonly string databaseName = MongoFixture.NewDatabaseName();

    // Nothing to prepare: each test starts its own host.
    public Task InitializeAsync() => Task.CompletedTask;

    // Drop the database of this test.
    public Task DisposeAsync() => mongo.GetDatabase(databaseName).Client.DropDatabaseAsync(databaseName);

    // Start the API host over the database of this test.
    private MongoApiFactory StartApi(DeterministicChatClient chatClient, Dictionary<string, string?>? settings = null) =>
        new(mongo, databaseName, chatClient, settings);

    // Create a conversation over HTTP and return its id.
    private static async Task<string> CreateConversationAsync(HttpClient client, string title = "Trip planning")
    {
        var response = await client.PostAsJsonAsync("/api/conversations", new CreateConversationRequest(title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ConversationCreatedResponse>();
        Assert.Equal($"/api/conversations/{created!.Id}", response.Headers.Location!.OriginalString);
        return created.Id;
    }

    // POST one user message and return the raw response.
    private static Task<HttpResponseMessage> PostMessageAsync(
        HttpClient client, string conversationId, string text, CancellationToken ct = default) =>
        client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", new SendMessageRequest(text), ct);

    // POST one user message, require 200, and return the typed body.
    private static async Task<SendMessageResponse> SendAsync(HttpClient client, string conversationId, string text)
    {
        var response = await PostMessageAsync(client, conversationId, text);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SendMessageResponse>())!;
    }

    // GET one transcript page, require 200, and return the typed body.
    private static async Task<ConversationResponse> GetTranscriptAsync(HttpClient client, string conversationId, string query = "")
    {
        var response = await client.GetAsync($"/api/conversations/{conversationId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }

    // Require the status code and a non-blank ErrorResponse message.
    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.False(string.IsNullOrWhiteSpace(error!.Message));
    }

    // Hold the first two answer calls until both are in flight, so both requests read the same state.
    private static void HoldFirstTwoAnswersUntilBothArrive(DeterministicChatClient chatClient)
    {
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        chatClient.BeforeAnswer = async ct =>
        {
            var position = Interlocked.Increment(ref arrived);
            if (position == 2)
                bothArrived.SetResult();

            if (position <= 2)
                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        };
    }

    [Fact]
    public async Task Health_ReturnsTypedOk()
    {
        await using var api = StartApi(new DeterministicChatClient());

        var health = await api.CreateClient().GetFromJsonAsync<HealthResponse>("/api/health");

        Assert.Equal("ok", health!.Status);
    }

    [Fact]
    public async Task Startup_CreatesTheUniqueTurnIndex_BeforeTheFirstRequest()
    {
        await using var api = StartApi(new DeterministicChatClient());
        using var client = api.CreateClient(); // starts the host; no request sent yet

        var indexes = await (await mongo.GetDatabase(databaseName)
            .GetCollection<BsonDocument>(MongoConversationRepository.TurnsCollection)
            .Indexes.ListAsync()).ToListAsync();

        var index = Assert.Single(indexes, i => i["name"] == ConversationIndexInitializer.TurnSequenceIndexName);
        Assert.True(index["unique"].AsBoolean);
    }

    [Fact]
    public async Task Send_ReturnsTheDocumentedJsonFieldNames()
    {
        await using var api = StartApi(new DeterministicChatClient());
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);

        var response = await PostMessageAsync(client, id, "Hello");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            ["reply", "storedMessageCount", "messagesSentToModel", "promptPreview", "summaryRefreshed"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Answer to: Hello", json.RootElement.GetProperty("reply").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("storedMessageCount").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("messagesSentToModel").GetInt32());
        Assert.False(json.RootElement.GetProperty("summaryRefreshed").GetBoolean());
    }

    [Fact]
    public async Task DefaultExample_NinthTurnIs18StoredAnd13Sent_AndTheCheckpointSurvivesAHostRestart()
    {
        string id;
        var firstClient = new DeterministicChatClient();

        // 1) First host: eight turns, then the ninth turn refreshes the summary.
        await using (var firstApi = StartApi(firstClient))
        {
            using var client = firstApi.CreateClient();
            id = await CreateConversationAsync(client);
            foreach (var turn in Enumerable.Range(1, 8))
            {
                var early = await SendAsync(client, id, $"Question {turn}");
                Assert.False(early.SummaryRefreshed);
                Assert.Equal(turn * 2, early.StoredMessageCount);
                Assert.Equal(turn * 2, early.MessagesSentToModel); // system + earlier pairs + new user
            }

            var ninth = await SendAsync(client, id, "Question 9");

            Assert.Equal(18, ninth.StoredMessageCount);
            Assert.Equal(13, ninth.MessagesSentToModel);
            Assert.True(ninth.SummaryRefreshed);
            Assert.Equal(13, ninth.PromptPreview.Count);
            Assert.StartsWith("system: ", ninth.PromptPreview[0]);
            Assert.Equal("assistant: SUMMARY 1", ninth.PromptPreview[1]);
            Assert.Equal("user: Question 4", ninth.PromptPreview[2]);
            Assert.Equal("user: Question 9", ninth.PromptPreview[^1]);
            Assert.Equal(1, firstClient.SummaryCallCount);
        }

        // 2) Second host, same database, new chat client: a process restart.
        var secondClient = new DeterministicChatClient();
        await using var secondApi = StartApi(secondClient);
        using var restarted = secondApi.CreateClient();

        var tenth = await SendAsync(restarted, id, "Question 10");

        // 3) The stored summary is reused. The restarted process makes no summary call.
        Assert.Equal(20, tenth.StoredMessageCount);
        Assert.Equal(15, tenth.MessagesSentToModel);
        Assert.False(tenth.SummaryRefreshed);
        Assert.Equal("assistant: SUMMARY 1", tenth.PromptPreview[1]);
        Assert.Equal("user: Question 4", tenth.PromptPreview[2]);
        Assert.Equal(0, secondClient.SummaryCallCount);
    }

    [Fact]
    public async Task ConcurrentSends_AreStoredWithConsecutiveSequences_AfterOneRegeneration()
    {
        var chatClient = new DeterministicChatClient();
        HoldFirstTwoAnswersUntilBothArrive(chatClient);
        await using var api = StartApi(chatClient);
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);

        var responses = await Task.WhenAll(
            PostMessageAsync(client, id, "First concurrent question"),
            PostMessageAsync(client, id, "Second concurrent question"));

        // 1) Both requests succeed: the loser of sequence 1 regenerated once and took sequence 2.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(3, chatClient.AnswerCalls.Count);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<SendMessageResponse>()));
        Assert.Equal([2, 4], bodies.Select(b => b!.StoredMessageCount).Order());

        // 2) MongoDB holds sequences 1 and 2, and each reply belongs to its own stored turn.
        var repository = api.Services.GetRequiredService<IConversationRepository>();
        var turns = await repository.GetTurnsAfterAsync(id, 0, CancellationToken.None);
        Assert.Equal([1L, 2L], turns.Select(t => t.Sequence));
        Assert.Equal(
            ["First concurrent question", "Second concurrent question"],
            turns.Select(t => t.UserText).Order());
        Assert.All(turns, t => Assert.Equal($"Answer to: {t.UserText}", t.AssistantText));

        // 3) The regenerated prompt was built from reloaded state: it contains the winning turn.
        Assert.Contains(chatClient.AnswerCalls[2], m => m.Text == turns[0].AssistantText);
    }

    [Fact]
    public async Task ConcurrentSends_WithZeroRetries_ReturnOne200AndOneTyped409()
    {
        var chatClient = new DeterministicChatClient();
        HoldFirstTwoAnswersUntilBothArrive(chatClient);
        await using var api = StartApi(chatClient, new() { ["ChatMemory:MaxConcurrencyRetries"] = "0" });
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);

        var responses = await Task.WhenAll(
            PostMessageAsync(client, id, "First concurrent question"),
            PostMessageAsync(client, id, "Second concurrent question"));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(r => r.StatusCode).Order());
        await AssertErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
        Assert.Equal(2, (await GetTranscriptAsync(client, id)).StoredMessageCount);
    }

    [Fact]
    public async Task InvalidRequests_ReturnTyped400()
    {
        await using var api = StartApi(new DeterministicChatClient());
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);

        await AssertErrorAsync(await PostMessageAsync(client, id, "   "), HttpStatusCode.BadRequest);
        await AssertErrorAsync(await PostMessageAsync(client, id, new string('x', 4001)), HttpStatusCode.BadRequest);
        await AssertErrorAsync(
            await client.PostAsJsonAsync("/api/conversations", new CreateConversationRequest(new string('t', 121))),
            HttpStatusCode.BadRequest);
        await AssertErrorAsync(await client.GetAsync($"/api/conversations/{id}?limit=0"), HttpStatusCode.BadRequest);
        await AssertErrorAsync(await client.GetAsync($"/api/conversations/{id}?beforeSequence=0"), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(UnknownId)]
    [InlineData("not-an-object-id")]
    public async Task UnknownOrInvalidConversation_ReturnsTyped404(string id)
    {
        var chatClient = new DeterministicChatClient();
        await using var api = StartApi(chatClient);
        using var client = api.CreateClient();

        await AssertErrorAsync(await PostMessageAsync(client, id, "Hello"), HttpStatusCode.NotFound);
        await AssertErrorAsync(await client.GetAsync($"/api/conversations/{id}"), HttpStatusCode.NotFound);
        Assert.Empty(chatClient.AnswerCalls);
    }

    [Fact]
    public async Task UppercaseHexId_AddressesTheSameConversation()
    {
        await using var api = StartApi(new DeterministicChatClient());
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);
        await SendAsync(client, id, "Question 1");

        var second = await SendAsync(client, id.ToUpperInvariant(), "Question 2");
        var transcript = await GetTranscriptAsync(client, id.ToUpperInvariant());

        // One conversation, sequences 1 and 2: the id case never splits the transcript.
        Assert.Equal(4, second.StoredMessageCount);
        Assert.Equal(4, second.MessagesSentToModel); // system + first pair + new user
        Assert.Equal(id, transcript.Id);
        Assert.Equal(["Question 1", "Answer to: Question 1", "Question 2", "Answer to: Question 2"], transcript.Messages.Select(m => m.Text));
        Assert.Equal(4, (await GetTranscriptAsync(client, id)).StoredMessageCount);
    }

    [Fact]
    public async Task Create_WithoutTitle_UsesTheDefaultTitle()
    {
        await using var api = StartApi(new DeterministicChatClient());
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/api/conversations", new CreateConversationRequest(null));
        var created = await response.Content.ReadFromJsonAsync<ConversationCreatedResponse>();

        Assert.Equal("New conversation", created!.Title);
    }

    [Fact]
    public async Task Transcript_PagesBackwards_WithChronologicalMessages_TotalCount_AndCursor()
    {
        await using var api = StartApi(new DeterministicChatClient());
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);
        foreach (var turn in Enumerable.Range(1, 5))
            await SendAsync(client, id, $"Question {turn}");

        var newest = await GetTranscriptAsync(client, id, "?limit=2");
        var middle = await GetTranscriptAsync(client, id, $"?limit=2&beforeSequence={newest.NextBeforeSequence}");
        var oldest = await GetTranscriptAsync(client, id, $"?limit=2&beforeSequence={middle.NextBeforeSequence}");
        var everything = await GetTranscriptAsync(client, id);

        Assert.Equal(
            ["user: Question 4", "assistant: Answer to: Question 4", "user: Question 5", "assistant: Answer to: Question 5"],
            newest.Messages.Select(m => $"{m.Role}: {m.Text}"));
        Assert.Equal(4, newest.NextBeforeSequence);
        Assert.Equal(["Question 2", "Answer to: Question 2", "Question 3", "Answer to: Question 3"], middle.Messages.Select(m => m.Text));
        Assert.Equal(2, middle.NextBeforeSequence);
        Assert.Equal(["Question 1", "Answer to: Question 1"], oldest.Messages.Select(m => m.Text));
        Assert.Null(oldest.NextBeforeSequence);
        Assert.All([newest, middle, oldest, everything], page => Assert.Equal(10, page.StoredMessageCount));
        Assert.Equal(10, everything.Messages.Count); // default limit 20 turns covers all five
        Assert.Null(everything.NextBeforeSequence);
        Assert.Equal("Trip planning", everything.Title);
        Assert.True(everything.UpdatedAt >= everything.CreatedAt);
        Assert.Equal(everything.Messages[^1].At, everything.UpdatedAt);
    }

    [Fact]
    public async Task ModelFailure_StoresNoTurn()
    {
        var chatClient = new DeterministicChatClient
        {
            BeforeAnswer = _ => throw new InvalidOperationException("model down"),
        };
        await using var api = StartApi(chatClient);
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);

        var response = await PostMessageAsync(client, id, "Hello");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var transcript = await GetTranscriptAsync(client, id);
        Assert.Equal(0, transcript.StoredMessageCount);
        Assert.Empty(transcript.Messages);
    }

    [Fact]
    public async Task CancelledRequest_StoresNoTurn()
    {
        var modelCallStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelCallCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatClient = new DeterministicChatClient
        {
            BeforeAnswer = async ct =>
            {
                modelCallStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    modelCallCancelled.SetResult();
                }
            },
        };
        await using var api = StartApi(chatClient);
        using var client = api.CreateClient();
        var id = await CreateConversationAsync(client);
        using var cancellation = new CancellationTokenSource();

        // 1) Cancel the HTTP request while the model call is running.
        var pending = PostMessageAsync(client, id, "Hello", cancellation.Token);
        await modelCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cancellation.Cancel();

        // 2) The client call is cancelled and the server-side model call observes the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await modelCallCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // 3) Nothing was stored: no user-only message, no turn.
        chatClient.BeforeAnswer = null;
        Assert.Equal(0, (await GetTranscriptAsync(client, id)).StoredMessageCount);
    }
}
