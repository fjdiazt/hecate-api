using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VirtuaAgent.Tests;

public sealed class SwaggerRouteTests
{
    [Fact]
    public async Task SwaggerJsonIncludesOpenAiCompatibleEndpoint()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"/v1/chat/completions\"", json);
        Assert.Contains("\"/v1/models\"", json);
        Assert.Contains("CreateChatCompletion", json);
    }

    [Fact]
    public async Task SwaggerJsonShowsOpenAiMultimodalContentShape()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var contentSchema = schemas.GetProperty("ChatMessageContent");
        var variants = contentSchema.GetProperty("oneOf").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(variants, schema => schema.GetProperty("type").GetString() == "string");
        Assert.Contains(variants, schema => schema.GetProperty("type").GetString() == "array");
        var rawContentSchema = contentSchema.GetRawText();
        Assert.Contains("\"image_url\"", rawContentSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("additionalProp1", json, StringComparison.Ordinal);

        var inputSchema = schemas.GetProperty("PipelineStageInputRequestDto");
        Assert.True(inputSchema.GetProperty("properties").TryGetProperty("original_messages", out _));
        Assert.True(inputSchema.GetProperty("properties").TryGetProperty("prior_stage_output", out _));
        var pipelineSchema = schemas.GetProperty("PipelineRequestDto");
        Assert.True(pipelineSchema.GetProperty("properties").TryGetProperty("protocol", out _));
    }

    [Fact]
    public async Task SwaggerJsonIncludesCodexAccountRoutes()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"/v1/codex/account\"", json, StringComparison.Ordinal);
        Assert.Contains("\"/v1/codex/account/login\"", json, StringComparison.Ordinal);
        Assert.Contains("GetCodexAccount", json, StringComparison.Ordinal);
        Assert.Contains("StartCodexLogin", json, StringComparison.Ordinal);
        Assert.Contains("CancelCodexLogin", json, StringComparison.Ordinal);
        Assert.Contains("LogoutCodexAccount", json, StringComparison.Ordinal);
    }
}
