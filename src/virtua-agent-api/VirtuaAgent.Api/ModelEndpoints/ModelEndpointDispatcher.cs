using VirtuaAgent.Codex;
using VirtuaAgent.OpenAi;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.ModelEndpoints;

public sealed class ModelEndpointDispatcher(
    IOpenAiCompatibleUpstreamClient upstream,
    CodexSubscriptionClient codex)
{
    public Task<ModelListResponse> ListModelsAsync(
        ModelEndpointDefinition endpoint,
        CancellationToken cancellationToken = default) =>
        endpoint.Type switch
        {
            ModelEndpointTypes.OpenAiCompatible => upstream.ListModelsAsync(endpoint, cancellationToken),
            ModelEndpointTypes.CodexSubscription => codex.ListModelsAsync(cancellationToken),
            _ => throw Unsupported(endpoint)
        };

    public Task<ChatCompletionResponse> ChatAsync(
        ChatCompletionRequest request,
        ModelEndpointDefinition endpoint,
        CancellationToken cancellationToken = default) =>
        endpoint.Type switch
        {
            ModelEndpointTypes.OpenAiCompatible => upstream.ChatAsync(request, endpoint, cancellationToken),
            ModelEndpointTypes.CodexSubscription => codex.ChatAsync(request, cancellationToken),
            _ => throw Unsupported(endpoint)
        };

    public Task StreamChatAsync(
        ChatCompletionRequest request,
        ModelEndpointDefinition endpoint,
        Func<string, CancellationToken, Task> onDataAsync,
        CancellationToken cancellationToken = default) =>
        endpoint.Type switch
        {
            ModelEndpointTypes.OpenAiCompatible => upstream.StreamChatAsync(request, endpoint, onDataAsync, cancellationToken),
            ModelEndpointTypes.CodexSubscription => codex.StreamChatAsync(request, onDataAsync, cancellationToken),
            _ => throw Unsupported(endpoint)
        };

    private static InvalidOperationException Unsupported(ModelEndpointDefinition endpoint) =>
        new($"Unsupported endpoint type '{endpoint.Type}'.");
}
