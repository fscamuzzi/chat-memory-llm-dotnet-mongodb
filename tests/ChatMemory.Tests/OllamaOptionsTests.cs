using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatMemory.Tests;

public class OllamaOptionsTests
{
    // Bind the given Ollama settings exactly as Program does and return the options accessor.
    private static IOptions<OllamaOptions> BindOptions(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddOptions<OllamaOptions>().Bind(configuration.GetSection(OllamaOptions.Section));
        services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();

        return services.BuildServiceProvider().GetRequiredService<IOptions<OllamaOptions>>();
    }

    [Fact]
    public void Defaults_KeepTheHundredSecondTimeout()
    {
        var options = BindOptions([]);

        Assert.Equal(100, options.Value.TimeoutSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3601")]
    public void TimeoutOutOfRange_FailsWhenOptionsAreRead(string value)
    {
        var options = BindOptions(new() { [$"{OllamaOptions.Section}:TimeoutSeconds"] = value });

        var exception = Assert.Throws<OptionsValidationException>(() => options.Value);

        Assert.Contains("TimeoutSeconds", exception.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("300")]
    [InlineData("3600")]
    public void TimeoutInRange_IsAccepted(string value)
    {
        var options = BindOptions(new() { [$"{OllamaOptions.Section}:TimeoutSeconds"] = value });

        Assert.Equal(int.Parse(value), options.Value.TimeoutSeconds);
    }

    [Fact]
    public void HttpClient_UsesTheConfiguredEndpointAndTimeout()
    {
        var options = new OllamaOptions { Endpoint = "http://ollama.test:11434", TimeoutSeconds = 300 };

        using var client = OllamaHttpClientFactory.Create(options);

        Assert.Equal(new Uri("http://ollama.test:11434"), client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(300), client.Timeout);
    }
}
