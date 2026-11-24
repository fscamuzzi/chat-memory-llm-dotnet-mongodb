using ChatMemory.Api.Services;
using Microsoft.Extensions.AI;

namespace ChatMemory.Tests;

public class SummaryRequestChatClientTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // Role and text of each message, in order.
    private static List<(ChatRole Role, string Text)> Shape(IEnumerable<ChatMessage> messages) =>
        messages.Select(message => (message.Role, message.Text)).ToList();

    [Fact]
    public async Task TrailingSystemInstruction_IsSentAsTheFinalUserMessage()
    {
        var inner = new FakeChatClient();
        var client = new SummaryRequestChatClient(inner);
        List<ChatMessage> request =
        [
            new(ChatRole.User, "Question 1"),
            new(ChatRole.Assistant, "Answer 1"),
            new(ChatRole.System, "Summarize the conversation."),
        ];

        await client.GetResponseAsync(request, cancellationToken: Ct);

        // The model receives the same texts; only the role of the instruction changes.
        var received = Assert.Single(inner.AnswerCalls);
        Assert.Equal(
            [
                (ChatRole.User, "Question 1"),
                (ChatRole.Assistant, "Answer 1"),
                (ChatRole.User, "Summarize the conversation."),
            ],
            Shape(received));

        // The caller's list is not changed.
        Assert.Equal(ChatRole.System, request[^1].Role);
    }

    [Fact]
    public async Task RequestThatEndsWithAUserMessage_IsForwardedUnchanged()
    {
        var inner = new FakeChatClient();
        var client = new SummaryRequestChatClient(inner);
        List<ChatMessage> request = [new(ChatRole.System, "You are a test assistant."), new(ChatRole.User, "Hello")];

        await client.GetResponseAsync(request, cancellationToken: Ct);

        var received = Assert.Single(inner.AnswerCalls);
        Assert.Equal([(ChatRole.System, "You are a test assistant."), (ChatRole.User, "Hello")], Shape(received));
    }
}
