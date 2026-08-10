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
        endpoint.Kind switch
        {
            ModelEndpointKinds.OpenAiCompatible => upstream.ListModelsAsync(endpoint, cancellationToken),
            ModelEndpointKinds.CodexSubscription => codex.ListModelsAsync(cancellationToken),
            _ => throw Unsupported(endpoint)
        };

    public Task<ChatCompletionResponse> ChatAsync(
        ChatCompletionRequest request,
        ModelEndpointDefinition endpoint,
        CancellationToken cancellationToken = default) =>
        endpoint.Kind switch
        {
            ModelEndpointKinds.OpenAiCompatible => upstream.ChatAsync(request, endpoint, cancellationToken),
            ModelEndpointKinds.CodexSubscription => codex.ChatAsync(request, cancellationToken),
            _ => throw Unsupported(endpoint)
        };

    public Task StreamChatAsync(
        ChatCompletionRequest request,
        ModelEndpointDefinition endpoint,
        Func<string, CancellationToken, Task> onDataAsync,
        CancellationToken cancellationToken = default) =>
        endpoint.Kind switch
        {
            ModelEndpointKinds.OpenAiCompatible => upstream.StreamChatAsync(request, endpoint, onDataAsync, cancellationToken),
            ModelEndpointKinds.CodexSubscription => codex.StreamChatAsync(request, onDataAsync, cancellationToken),
            _ => throw Unsupported(endpoint)
        };

    private static InvalidOperationException Unsupported(ModelEndpointDefinition endpoint) =>
        new($"Unsupported endpoint kind '{endpoint.Kind}'.");
}
