using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace ChatMemory.IntegrationTests;

// One MongoDB container for the whole test collection. Each test uses its own database name.
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7").Build();

    public string ConnectionString => container.GetConnectionString();

    // Start the container before the first test of the collection.
    public Task InitializeAsync() => container.StartAsync();

    // Remove the container after the last test of the collection.
    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    // A database name that no other test uses.
    public static string NewDatabaseName() => $"chatmemory_it_{Guid.NewGuid():N}";

    // A database handle on the test container.
    public IMongoDatabase GetDatabase(string databaseName) =>
        new MongoClient(ConnectionString).GetDatabase(databaseName);
}

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "MongoDB";
}

// Starts the real API host against the test MongoDB database.
// Only the IChatClient is replaced: Ollama is never needed.
public sealed class MongoApiFactory(
    MongoFixture mongo,
    string databaseName,
    DeterministicChatClient chatClient,
    Dictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    // Point the host at the test database and replace the chat client.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Added last, so these values win over appsettings.json.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(settings ?? [])
            {
                ["Mongo:ConnectionString"] = mongo.ConnectionString,
                ["Mongo:Database"] = databaseName,
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(chatClient);
        });
    }
}
