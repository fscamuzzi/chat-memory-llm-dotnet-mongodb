using Microsoft.Extensions.Options;

namespace ChatMemory.Api.Options;

// Checks OllamaOptions when the host starts. A timeout outside the range stops startup
// instead of failing on the first model call.
public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;

    // Accept only a timeout the HTTP client can use and that still ends a stuck call.
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        if (options.TimeoutSeconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds)
            return ValidateOptionsResult.Success;

        return ValidateOptionsResult.Fail(
            $"{OllamaOptions.Section}:TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.");
    }
}
