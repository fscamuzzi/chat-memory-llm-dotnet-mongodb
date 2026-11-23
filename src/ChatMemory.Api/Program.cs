using ChatMemory.Api.Data;
using ChatMemory.Api.Endpoints;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

// Options: bound once and checked at startup, so a wrong value stops the host.
builder.Services.AddOptions<MongoOptions>()
    .Bind(builder.Configuration.GetSection(MongoOptions.Section))
    .ValidateOnStart();
builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection(OllamaOptions.Section))
    .ValidateOnStart();
builder.Services.AddOptions<ChatMemoryOptions>()
    .Bind(builder.Configuration.GetSection(ChatMemoryOptions.Section))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ChatMemoryOptions>, ChatMemoryOptionsValidator>();

// MongoDB: conversations, immutable turns, and the unique turn-sequence index.
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var mongo = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
    return new MongoClient(mongo.ConnectionString).GetDatabase(mongo.Database);
});
builder.Services.AddSingleton<IConversationRepository, MongoConversationRepository>();

// Hosted services start before the server accepts requests: the index exists before the first turn.
builder.Services.AddHostedService<ConversationIndexInitializer>();

// Ollama as the IChatClient for answers and for summaries.
builder.Services.AddChatClient(sp =>
{
    var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    return new OllamaApiClient(new Uri(ollama.Endpoint), ollama.Model);
});

builder.Services.AddSingleton<ConversationMemoryReducer>();
builder.Services.AddSingleton<ChatService>();

var app = builder.Build();

app.MapConversationEndpoints();

app.Run();

// Lets WebApplicationFactory<Program> in the integration tests start this host.
public partial class Program;
