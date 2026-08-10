using System.Text.Json.Serialization;

namespace VirtuaAgent.ModelEndpoints;

public static class ModelEndpointKinds
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string CodexSubscription = "codex_subscription";
}

public sealed record ModelEndpointDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = ModelEndpointKinds.OpenAiCompatible;
    public string BaseUrl { get; init; } = "";
    public string? ApiKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ModelEndpointDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = ModelEndpointKinds.OpenAiCompatible;

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("has_api_key")]
    public bool HasApiKey { get; init; }
}

public sealed record SaveModelEndpointRequest
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Kind { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }
}

public static class ModelEndpointMapping
{
    public static ModelEndpointDto ToDto(this ModelEndpointDefinition endpoint) => new()
    {
        Id = endpoint.Id,
        Name = endpoint.Name,
        Kind = endpoint.Kind,
        BaseUrl = endpoint.Kind == ModelEndpointKinds.CodexSubscription ? null : endpoint.BaseUrl,
        HasApiKey = !string.IsNullOrWhiteSpace(endpoint.ApiKey)
    };
}
