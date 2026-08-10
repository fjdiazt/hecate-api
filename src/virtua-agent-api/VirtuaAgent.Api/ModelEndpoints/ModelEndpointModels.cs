using System.Text.Json.Serialization;

namespace VirtuaAgent.ModelEndpoints;

public static class ModelEndpointTypes
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string CodexSubscription = "codex_subscription";
}

public sealed record ModelEndpointDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = ModelEndpointTypes.OpenAiCompatible;
    public string BaseUrl { get; init; } = "";
    public string? ApiKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ModelEndpointDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = ModelEndpointTypes.OpenAiCompatible;

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("has_api_key")]
    public bool HasApiKey { get; init; }
}

public sealed record SaveModelEndpointRequest
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Type { get; init; }

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
        Type = endpoint.Type,
        BaseUrl = endpoint.Type == ModelEndpointTypes.CodexSubscription ? null : endpoint.BaseUrl,
        HasApiKey = !string.IsNullOrWhiteSpace(endpoint.ApiKey)
    };
}
