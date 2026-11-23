using ChatMemory.Api.Models;
using ChatMemory.Api.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatMemory.Tests;

public class ChatMemoryOptionsValidatorTests
{
    // Bind the given ChatMemory settings exactly as Program does and return the options accessor.
    private static IOptions<ChatMemoryOptions> BindOptions(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddOptions<ChatMemoryOptions>().Bind(configuration.GetSection(ChatMemoryOptions.Section));
        services.AddSingleton<IValidateOptions<ChatMemoryOptions>, ChatMemoryOptionsValidator>();

        return services.BuildServiceProvider().GetRequiredService<IOptions<ChatMemoryOptions>>();
    }

    [Theory]
    [InlineData("Strategy", "bogus")]
    [InlineData("Strategy", "7")]
    [InlineData("TargetMessageCount", "0")]
    [InlineData("TargetMessageCount", "-1")]
    [InlineData("MaxInputCharacters", "0")]
    [InlineData("MaxTitleCharacters", "0")]
    [InlineData("SummarizationThreshold", "0")]
    [InlineData("SummarizationThreshold", "-1")]
    [InlineData("SystemPrompt", "   ")]
    [InlineData("MaxConcurrencyRetries", "-1")]
    [InlineData("MaxConcurrencyRetries", "4")]
    public void InvalidSetting_FailsWhenOptionsAreRead(string key, string value)
    {
        var options = BindOptions(new() { [$"{ChatMemoryOptions.Section}:{key}"] = value });

        var exception = Assert.ThrowsAny<Exception>(() => options.Value);

        // The failure must name the offending setting: binder errors and validator errors both do.
        Assert.True(
            exception is OptionsValidationException or InvalidOperationException,
            $"Unexpected exception type {exception.GetType().Name}");
        Assert.Contains(key, exception.Message);
    }

    [Fact]
    public void NegativeThreshold_FailsAlsoForCountingStrategy()
    {
        var options = BindOptions(new()
        {
            [$"{ChatMemoryOptions.Section}:Strategy"] = "Counting",
            [$"{ChatMemoryOptions.Section}:SummarizationThreshold"] = "-1",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => options.Value);

        Assert.Contains("SummarizationThreshold", exception.Message);
    }

    [Fact]
    public void ZeroThreshold_IsValidForCountingStrategy()
    {
        var options = BindOptions(new()
        {
            [$"{ChatMemoryOptions.Section}:Strategy"] = "Counting",
            [$"{ChatMemoryOptions.Section}:SummarizationThreshold"] = "0",
        });

        Assert.Equal(0, options.Value.SummarizationThreshold);
    }

    [Fact]
    public void Defaults_AreValid()
    {
        var options = BindOptions([]).Value;

        Assert.Equal(ChatMemoryStrategy.Summarizing, options.Strategy);
        Assert.Equal(10, options.TargetMessageCount);
        Assert.Equal(5, options.SummarizationThreshold);
        Assert.Equal(4000, options.MaxInputCharacters);
        Assert.Equal(120, options.MaxTitleCharacters);
        Assert.Equal(1, options.MaxConcurrencyRetries);
        Assert.False(string.IsNullOrWhiteSpace(options.SystemPrompt));
    }
}
