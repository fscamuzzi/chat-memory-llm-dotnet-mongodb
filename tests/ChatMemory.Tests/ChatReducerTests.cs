using Microsoft.Extensions.AI;

namespace ChatMemory.Tests;

public class ChatReducerTests
{
    private static List<ChatMessage> BuildHistory(int turns)
    {
        List<ChatMessage> messages = [new(ChatRole.System, "You are a test assistant.")];
        var pairs = Enumerable.Range(1, turns).SelectMany(i => new ChatMessage[]
        {
            new(ChatRole.User, $"Question {i}"),
            new(ChatRole.Assistant, $"Answer {i}"),
        });
        messages.AddRange(pairs);
        return messages;
    }

    [Fact]
    public async Task CountingReducer_KeepsSystemMessage_AndLastN()
    {
        var reducer = new MessageCountingChatReducer(targetCount: 4);
        var history = BuildHistory(turns: 10); // 1 system + 20 messages

        var reduced = (await reducer.ReduceAsync(history, CancellationToken.None)).ToList();

        Assert.Contains(reduced, m => m.Role == ChatRole.System);
        Assert.Equal(4, reduced.Count(m => m.Role != ChatRole.System));
        Assert.Equal("Answer 10", reduced[^1].Text);
    }

    [Fact]
    public async Task SummarizingReducer_FoldsOlderMessages_IntoSummary()
    {
        var summarizer = new FakeChatClient("SUMMARY: the user asked ten numbered questions.");
        var reducer = new SummarizingChatReducer(summarizer, targetCount: 4, threshold: 0);
        var history = BuildHistory(turns: 10);

        var reduced = (await reducer.ReduceAsync(history, CancellationToken.None)).ToList();

        Assert.True(reduced.Count < history.Count);
        Assert.Contains(reduced, m => m.Text.Contains("SUMMARY"));
        Assert.Equal("Answer 10", reduced[^1].Text);
    }

    [Fact]
    public async Task CountingReducer_BelowTarget_LeavesHistoryUntouched()
    {
        var reducer = new MessageCountingChatReducer(targetCount: 10);
        var history = BuildHistory(turns: 2);

        var reduced = (await reducer.ReduceAsync(history, CancellationToken.None)).ToList();

        Assert.Equal(history.Count, reduced.Count);
    }
}
