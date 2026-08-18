using ChatMemory.Api.Contracts;
using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatMemory.Tests;

public class ChatServiceTests
{
    private static ChatService BuildService(
        FakeChatClient chatClient, InMemoryConversationRepository repository, ChatMemoryOptions options) =>
        new(chatClient, repository, new ChatReducerFactory(Options.Create(options)), Options.Create(options));

    [Fact]
    public async Task Send_PersistsTurn_AndReportsCounts()
    {
        var chatClient = new FakeChatClient("Ciao!");
        var repository = new InMemoryConversationRepository();
        var service = BuildService(chatClient, repository, new ChatMemoryOptions { Strategy = "none" });
        var conversation = await repository.CreateAsync("Test", CancellationToken.None);

        var response = await service.SendAsync(
            conversation.Id, new SendMessageRequest("Hello"), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Ciao!", response.Reply);
        Assert.Equal(2, response.MessagesInHistory);      // system + user
        Assert.Equal(2, response.MessagesSentToModel);    // no reduction
        var stored = await repository.GetAsync(conversation.Id, CancellationToken.None);
        Assert.Equal(2, stored!.Messages.Count);          // user + assistant persisted
        Assert.Equal(ChatRole.Assistant.Value, stored.Messages[^1].Role);
    }

    [Fact]
    public async Task Send_WithCountingStrategy_TrimsWhatTheModelSees()
    {
        var chatClient = new FakeChatClient("ok");
        var repository = new InMemoryConversationRepository();
        var service = BuildService(
            chatClient, repository, new ChatMemoryOptions { Strategy = "counting", TargetMessageCount = 4 });
        var conversation = await repository.CreateAsync("Test", CancellationToken.None);
        var now = DateTime.UtcNow;
        var history = Enumerable.Range(1, 10).SelectMany(i => new StoredMessage[]
        {
            new() { Role = "user", Text = $"Question {i}", At = now },
            new() { Role = "assistant", Text = $"Answer {i}", At = now },
        }).ToList();
        await repository.AppendMessagesAsync(conversation.Id, history, CancellationToken.None);

        var response = await service.SendAsync(
            conversation.Id, new SendMessageRequest("Latest question"), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(22, response.MessagesInHistory);     // system + 20 + new user
        Assert.Equal(5, response.MessagesSentToModel);    // system + last 4
        Assert.NotNull(chatClient.LastMessages);
        Assert.Equal("Latest question", chatClient.LastMessages![^1].Text);
        var stored = await repository.GetAsync(conversation.Id, CancellationToken.None);
        Assert.Equal(22, stored!.Messages.Count);         // store keeps EVERYTHING
    }

    [Fact]
    public async Task Send_UnknownConversation_ReturnsNull()
    {
        var service = BuildService(
            new FakeChatClient("x"), new InMemoryConversationRepository(), new ChatMemoryOptions());

        var response = await service.SendAsync(
            "68a0000000000000000000aa", new SendMessageRequest("Hi"), CancellationToken.None);

        Assert.Null(response);
    }
}
