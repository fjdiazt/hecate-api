using VirtuaAgent.Endpoints;
using VirtuaAgent.Codex;
using VirtuaAgent.ModelEndpoints;
using VirtuaAgent.PipelineModels;
using VirtuaAgent.OpenAi;
using VirtuaAgent.Orchestration;
using VirtuaAgent.Settings;
using VirtuaAgent.Storage;
using VirtuaAgent.Tracing;
using VirtuaAgent.Upstream;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SchemaFilter<ChatMessageContentSchemaFilter>();
});
builder.Services.Configure<UpstreamOptions>(builder.Configuration.GetSection("Upstream"));
builder.Services.Configure<CodexOptions>(builder.Configuration.GetSection("Codex"));
builder.Services.AddSingleton<ActiveTraceHub>();
builder.Services.AddSingleton<PipelineExecutor>();
builder.Services.AddSingleton<IPipelineModelStore>(_ =>
{
    var connectionString = builder.Configuration.GetValue<string>("TraceStore:ConnectionString")
        ?? "Data Source=virtua-agent.db";
    var store = new SqlitePipelineModelStore(connectionString);
    store.InitializeAsync().GetAwaiter().GetResult();
    return store;
});
builder.Services.AddSingleton(services =>
    new PipelinePresetCatalog(
        builder.Configuration.GetSection("PipelinePresets").Get<List<PipelineModelDefinition>>() ?? [],
        services.GetRequiredService<IPipelineModelStore>()));
builder.Services.AddSingleton<ITraceStore>(_ =>
{
    var connectionString = builder.Configuration.GetValue<string>("TraceStore:ConnectionString")
        ?? "Data Source=virtua-agent.db";
    var store = new SqliteTraceStore(connectionString);
    store.InitializeAsync().GetAwaiter().GetResult();
    return store;
});
builder.Services.AddSingleton<IModelEndpointStore>(_ =>
{
    var connectionString = builder.Configuration.GetValue<string>("TraceStore:ConnectionString")
        ?? "Data Source=virtua-agent.db";
    var store = new SqliteModelEndpointStore(connectionString);
    store.InitializeAsync().GetAwaiter().GetResult();
    return store;
});
builder.Services.AddSingleton<IPipelineSettingsStore>(_ =>
{
    var connectionString = builder.Configuration.GetValue<string>("TraceStore:ConnectionString")
        ?? "Data Source=virtua-agent.db";
    var store = new SqlitePipelineSettingsStore(connectionString);
    store.InitializeAsync().GetAwaiter().GetResult();
    return store;
});
builder.Services.AddHttpClient<IOpenAiCompatibleUpstreamClient, OpenAiCompatibleUpstreamClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<UpstreamOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
});
builder.Services.AddSingleton<ICodexAppServerClient, CodexAppServerClient>();
builder.Services.AddSingleton<CodexSubscriptionClient>();
builder.Services.AddSingleton<ModelEndpointDispatcher>();

var storageConnectionString = builder.Configuration.GetValue<string>("TraceStore:ConnectionString")
    ?? "Data Source=virtua-agent.db";
ObsoleteStorageCleanup.DropTemporaryBenchTablesAsync(storageConnectionString).GetAwaiter().GetResult();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.HandleAsync)
    .WithName("CreateChatCompletion")
    .WithSummary("Create an OpenAI-compatible chat completion")
    .WithDescription("Proxies OpenAI-compatible chat completions and optionally runs Virtua Agent orchestration.")
    .Accepts<ChatCompletionRequest>("application/json")
    .Produces<ChatCompletionResponse>()
    .Produces<OpenAiErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<OpenAiErrorResponse>(StatusCodes.Status502BadGateway);
app.MapGet("/v1/models", ModelsEndpoint.ListAsync)
    .WithName("ListModels")
    .WithSummary("List OpenAI-compatible upstream models")
    .Produces<ModelListResponse>()
    .Produces<OpenAiErrorResponse>(StatusCodes.Status502BadGateway);
app.MapGet("/v1/pipeline-models", PipelineModelsEndpoint.ListAsync)
    .WithName("ListPipelineModels")
    .WithSummary("List saved Virtua Agent Pipeline-backed models");
app.MapPost("/v1/pipeline-models", PipelineModelsEndpoint.SaveAsync)
    .WithName("SavePipelineModel")
    .WithSummary("Save a Virtua Agent Pipeline-backed model");
app.MapDelete("/v1/pipeline-models/{**id}", PipelineModelsEndpoint.DeleteAsync)
    .WithName("DeletePipelineModel")
    .WithSummary("Delete a saved Virtua Agent Pipeline-backed model");
app.MapGet("/v1/model-endpoints", ModelEndpointsEndpoint.ListAsync)
    .WithName("ListModelEndpoints")
    .WithSummary("List configured OpenAI-compatible model endpoints");
app.MapPost("/v1/model-endpoints", ModelEndpointsEndpoint.SaveAsync)
    .WithName("SaveModelEndpoint")
    .WithSummary("Save an OpenAI-compatible model endpoint");
app.MapDelete("/v1/model-endpoints/{id}", ModelEndpointsEndpoint.DeleteAsync)
    .WithName("DeleteModelEndpoint")
    .WithSummary("Delete an OpenAI-compatible model endpoint");
app.MapGet("/v1/model-endpoints/{id}/models", ModelEndpointsEndpoint.ListModelsAsync)
    .WithName("ListModelEndpointModels")
    .WithSummary("List models from a configured OpenAI-compatible model endpoint");
app.MapGet("/v1/settings", PipelineSettingsEndpoint.GetAsync)
    .WithName("GetPipelineSettings")
    .WithSummary("Get Virtua Agent settings");
app.MapPut("/v1/settings", PipelineSettingsEndpoint.SaveAsync)
    .WithName("SavePipelineSettings")
    .WithSummary("Save Virtua Agent settings");
app.MapGet("/v1/orchestrations/{runId}", OrchestrationRunsEndpoint.GetAsync)
    .WithName("GetOrchestrationRun")
    .WithSummary("Get a Virtua Agent orchestration run");
app.MapGet("/v1/orchestrations", OrchestrationRunsEndpoint.ListAsync)
    .WithName("ListOrchestrationRuns")
    .WithSummary("List Virtua Agent orchestration runs");
app.MapDelete("/v1/orchestrations", OrchestrationRunsEndpoint.ClearAsync)
    .WithName("ClearOrchestrationRuns")
    .WithSummary("Clear stored Virtua Agent orchestration runs");
app.MapGet("/v1/orchestrations/{runId}/events", OrchestrationEventsEndpoint.HandleAsync)
    .WithName("StreamOrchestrationEvents")
    .WithSummary("Stream live Virtua Agent trace events")
    .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

app.MapGet("/", () => Results.Redirect("/app/models"));
app.MapFallbackToFile("/app/{*path:nonfile}", "app/index.html");

app.Run();

public partial class Program;
