using ChatMemory.Api.Models;
using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Options;

// Checks ChatMemoryOptions when the host starts. An invalid value stops startup
// instead of silently changing how much history the model receives.
public sealed class ChatMemoryOptionsValidator : IValidateOptions<ChatMemoryOptions>
{
    private const string Prefix = ChatMemoryOptions.Section;

    // Return every broken rule in one result, so a single startup failure lists all of them.
    public ValidateOptionsResult Validate(string? name, ChatMemoryOptions options)
    {
        // 1) The summarizing strategy needs a threshold window to keep user/assistant pairs together.
        var minimumThreshold = options.Strategy == ChatMemoryStrategy.Summarizing ? 1 : 0;

        // 2) Pair each rule with its message; keep the messages of the rules that fail.
        (bool IsBroken, string Message)[] rules =
        [
            (!Enum.IsDefined(options.Strategy),
                $"{Prefix}:Strategy must be None, Counting, or Summarizing."),
            (options.TargetMessageCount <= 0,
                $"{Prefix}:TargetMessageCount must be greater than 0."),
            (options.SummarizationThreshold < minimumThreshold,
                $"{Prefix}:SummarizationThreshold must be at least {minimumThreshold} for strategy {options.Strategy}."),
            (options.MaxInputCharacters <= 0,
                $"{Prefix}:MaxInputCharacters must be greater than 0."),
            (options.MaxTitleCharacters <= 0,
                $"{Prefix}:MaxTitleCharacters must be greater than 0."),
            (options.MaxConcurrencyRetries is < 0 or > 3,
                $"{Prefix}:MaxConcurrencyRetries must be between 0 and 3."),
            (string.IsNullOrWhiteSpace(options.SystemPrompt),
                $"{Prefix}:SystemPrompt must not be blank."),
        ];
        var failures = rules.Where(rule => rule.IsBroken).Select(rule => rule.Message).ToList();

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
