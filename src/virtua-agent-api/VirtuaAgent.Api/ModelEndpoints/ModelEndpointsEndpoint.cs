using VirtuaAgent.OpenAi;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.ModelEndpoints;

public static class ModelEndpointsEndpoint
{
    public static async Task<IResult> ListAsync(IModelEndpointStore store, CancellationToken cancellationToken)
    {
        var endpoints = await store.ListAsync(cancellationToken);
        return Results.Json(endpoints.Select(endpoint => endpoint.ToDto()), JsonOptions.Default);
    }

    public static async Task<IResult> SaveAsync(
        SaveModelEndpointRequest request,
        IModelEndpointStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Endpoint name is required.", "name", "endpoint_name_required");
        }

        var kind = string.IsNullOrWhiteSpace(request.Kind)
            ? ModelEndpointKinds.OpenAiCompatible
            : request.Kind.Trim().ToLowerInvariant();
        if (kind is not ModelEndpointKinds.OpenAiCompatible and not ModelEndpointKinds.CodexSubscription)
        {
            return BadRequest("Endpoint kind is not supported.", "kind", "invalid_endpoint_kind");
        }

        if (kind == ModelEndpointKinds.OpenAiCompatible &&
            (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var baseUri) ||
             (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)))
        {
            return BadRequest("Endpoint base_url must be an absolute HTTP URL.", "base_url", "invalid_endpoint_url");
        }

        var saved = await store.SaveAsync(request with
        {
            Kind = kind,
            BaseUrl = kind == ModelEndpointKinds.CodexSubscription ? null : request.BaseUrl,
            ApiKey = kind == ModelEndpointKinds.CodexSubscription ? null : request.ApiKey
        }, cancellationToken);
        return Results.Json(saved.ToDto(), JsonOptions.Default);
    }

    public static async Task<IResult> DeleteAsync(string id, IModelEndpointStore store, CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteAsync(id, cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    public static async Task<IResult> ListModelsAsync(
        string id,
        IModelEndpointStore store,
        IOpenAiCompatibleUpstreamClient upstreamClient,
        CancellationToken cancellationToken)
    {
        var endpoint = await store.GetAsync(id, cancellationToken);
        if (endpoint is null)
        {
            return Results.NotFound();
        }

        try
        {
            var models = await upstreamClient.ListModelsAsync(endpoint, cancellationToken);
            return Results.Json(models, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            var error = new OpenAiErrorResponse(new OpenAiError
            {
                Message = ex is TaskCanceledException or TimeoutException
                    ? "Endpoint model list timed out. The endpoint may be offline or warming."
                    : ex.Message,
                Type = "server_error",
                Code = "upstream_error"
            });

            return Results.Json(error, JsonOptions.Default, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult BadRequest(string message, string param, string code) =>
        Results.BadRequest(new OpenAiErrorResponse(new OpenAiError
        {
            Message = message,
            Type = "invalid_request_error",
            Param = param,
            Code = code
        }));
}
