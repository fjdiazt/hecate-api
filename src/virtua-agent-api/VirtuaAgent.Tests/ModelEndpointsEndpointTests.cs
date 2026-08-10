using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VirtuaAgent.ModelEndpoints;
using VirtuaAgent.OpenAi;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.Tests;

public sealed class ModelEndpointsEndpointTests
{
    [Fact]
    public async Task SaveCodexSubscriptionEndpointNeedsNoUrlOrKey()
    {
        await using var factory = FactoryWith(new InMemoryModelEndpointStore());
        var response = await factory.CreateClient().PostAsJsonAsync("/v1/model-endpoints", new SaveModelEndpointRequest
        {
            Id = "codex-subscription",
            Name = "ChatGPT Codex",
            Type = ModelEndpointTypes.CodexSubscription
        }, JsonOptions.Default);
        var body = await response.Content.ReadFromJsonAsync<ModelEndpointDto>(JsonOptions.Default);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ModelEndpointTypes.CodexSubscription, body!.Type);
        Assert.Contains("\"type\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\"", json, StringComparison.Ordinal);
        Assert.Null(body.BaseUrl);
        Assert.False(body.HasApiKey);
    }

    [Fact]
    public async Task MissingTypeDefaultsToOpenAiCompatible()
    {
        await using var factory = FactoryWith(new InMemoryModelEndpointStore());
        var response = await factory.CreateClient().PostAsJsonAsync("/v1/model-endpoints", new
        {
            name = "llama.cpp",
            base_url = "http://localhost:8080"
        });
        var body = await response.Content.ReadFromJsonAsync<ModelEndpointDto>(JsonOptions.Default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ModelEndpointTypes.OpenAiCompatible, body!.Type);
    }

    [Fact]
    public async Task UnknownTypeIsRejected()
    {
        await using var factory = FactoryWith(new InMemoryModelEndpointStore());
        var response = await factory.CreateClient().PostAsJsonAsync("/v1/model-endpoints", new
        {
            name = "Unknown",
            type = "unknown"
        });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_endpoint_type", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndListEndpointDoesNotReturnApiKey()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IModelEndpointStore>();
                    services.AddSingleton<IModelEndpointStore>(new InMemoryModelEndpointStore());
                });
            });

        var client = factory.CreateClient();
        var saveResponse = await client.PostAsJsonAsync("/v1/model-endpoints", new SaveModelEndpointRequest
        {
            Name = "llama.cpp",
            BaseUrl = "http://localhost:8080",
            ApiKey = "secret"
        }, JsonOptions.Default);
        var savedText = await saveResponse.Content.ReadAsStringAsync();

        var listResponse = await client.GetAsync("/v1/model-endpoints");
        var listText = await listResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains("llama.cpp", listText, StringComparison.Ordinal);
        Assert.Contains("has_api_key", listText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", savedText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", listText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointModelsUseSelectedEndpoint()
    {
        var store = new InMemoryModelEndpointStore();
        await store.SaveAsync(new SaveModelEndpointRequest
        {
            Id = "llamacpp",
            Name = "llama.cpp",
            BaseUrl = "http://llama.test",
            ApiKey = "secret"
        });
        var upstream = new RecordingEndpointUpstreamClient();

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IModelEndpointStore>();
                    services.RemoveAll<IOpenAiCompatibleUpstreamClient>();
                    services.AddSingleton<IModelEndpointStore>(store);
                    services.AddSingleton<IOpenAiCompatibleUpstreamClient>(upstream);
                });
            });

        var response = await factory.CreateClient().GetAsync("/v1/model-endpoints/llamacpp/models");
        var body = await response.Content.ReadFromJsonAsync<ModelListResponse>(JsonOptions.Default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("llama-model", body!.Data[0].Id);
        Assert.Equal("llamacpp", upstream.EndpointIds.Single());
    }

    private sealed class RecordingEndpointUpstreamClient : IOpenAiCompatibleUpstreamClient
    {
        public List<string> EndpointIds { get; } = [];

        public Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModelListResponse> ListModelsAsync(ModelEndpointDefinition endpoint, CancellationToken cancellationToken = default)
        {
            EndpointIds.Add(endpoint.Id);
            return Task.FromResult(new ModelListResponse
            {
                Data = [new ModelDto { Id = "llama-model", OwnedBy = endpoint.Name }]
            });
        }

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StreamChatAsync(ChatCompletionRequest request, Stream output, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> FactoryWith(IModelEndpointStore store) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IModelEndpointStore>();
                    services.AddSingleton(store);
                });
            });

    private sealed class InMemoryModelEndpointStore : IModelEndpointStore
    {
        private readonly Dictionary<string, ModelEndpointDefinition> _endpoints = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ModelEndpointDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelEndpointDefinition>>(_endpoints.Values.OrderBy(endpoint => endpoint.Name).ToList());

        public Task<ModelEndpointDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_endpoints.GetValueOrDefault(id));

        public Task<ModelEndpointDefinition> SaveAsync(SaveModelEndpointRequest request, CancellationToken cancellationToken = default)
        {
            var endpoint = new ModelEndpointDefinition
            {
                Id = request.Id ?? "endpoint_test",
                Name = request.Name,
                Type = string.IsNullOrWhiteSpace(request.Type) ? ModelEndpointTypes.OpenAiCompatible : request.Type,
                BaseUrl = request.Type == ModelEndpointTypes.CodexSubscription ? "" : request.BaseUrl ?? "",
                ApiKey = request.Type == ModelEndpointTypes.CodexSubscription ? null : request.ApiKey,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _endpoints[endpoint.Id] = endpoint;
            return Task.FromResult(endpoint);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_endpoints.Remove(id));
    }
}
