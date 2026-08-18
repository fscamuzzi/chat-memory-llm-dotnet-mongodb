using ChatMemory.Api.Data;
using ChatMemory.Api.Endpoints;
using ChatMemory.Api.Options;
using ChatMemory.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection(MongoOptions.Section));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.Section));
builder.Services.Configure<ChatMemoryOptions>(builder.Configuration.GetSection(ChatMemoryOptions.Section));

builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var mongo = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
    return new MongoClient(mongo.ConnectionString).GetDatabase(mongo.Database);
});
builder.Services.AddSingleton<IConversationRepository, MongoConversationRepository>();

builder.Services.AddChatClient(sp =>
{
    var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    return new OllamaApiClient(new Uri(ollama.Endpoint), ollama.Model);
});

builder.Services.AddSingleton<ChatReducerFactory>();
builder.Services.AddSingleton<ChatService>();

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapConversationEndpoints();

app.Run();
