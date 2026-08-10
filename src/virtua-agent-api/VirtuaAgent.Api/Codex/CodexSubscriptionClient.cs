using System.Text.Json;
using VirtuaAgent.OpenAi;
using VirtuaAgent.Orchestration;

namespace VirtuaAgent.Codex;

public sealed class CodexSubscriptionClient(ICodexAppServerClient appServer)
{
    private const int MaxDataImageBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> SupportedImageTypes =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp"
    ];

    public async Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await appServer.ListModelsAsync(cancellationToken);
        return new ModelListResponse
        {
            Data = models.Select(model => new ModelDto
            {
                Id = model.Id,
                OwnedBy = "openai-codex"
            }).ToList()
        };
    }

    public async Task<ChatCompletionResponse> ChatAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await appServer.RunTurnAsync(
            request.Model,
            MapInput(request.Messages),
            cancellationToken: cancellationToken);
        return ToResponse(result, request.Model);
    }

    public async Task StreamChatAsync(
        ChatCompletionRequest request,
        Func<string, CancellationToken, Task> onDataAsync,
        CancellationToken cancellationToken = default)
    {
        var id = "chatcmpl_" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = request.Model ?? "codex";
        var result = await appServer.RunTurnAsync(
            request.Model,
            MapInput(request.Messages),
            async (delta, token) =>
            {
                if (!string.IsNullOrEmpty(delta.Reasoning))
                    await onDataAsync(CreateChunk(id, created, model, reasoning: delta.Reasoning), token);
                if (!string.IsNullOrEmpty(delta.Content))
                    await onDataAsync(CreateChunk(id, created, model, content: delta.Content), token);
            },
            cancellationToken);

        await onDataAsync(CreateChunk(
            id,
            created,
            string.IsNullOrWhiteSpace(result.Model) ? model : result.Model,
            finishReason: "stop"), cancellationToken);
    }

    private static List<CodexInputItem> MapInput(IEnumerable<ChatMessageDto> messages)
    {
        var input = new List<CodexInputItem>();
        foreach (var message in messages)
        {
            if (!message.Content.IsParts)
            {
                input.Add(new CodexInputItem("text", Text: $"Role: {message.Role}\n\n{message.Content.AsText()}"));
                continue;
            }

            input.Add(new CodexInputItem("text", Text: $"Role: {message.Role}"));
            foreach (var part in message.Content.Parts)
            {
                if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    input.Add(new CodexInputItem("text", Text: part.Text ?? ""));
                }
                else if (string.Equals(part.Type, "image_url", StringComparison.OrdinalIgnoreCase) && part.ImageUrl is not null)
                {
                    ValidateImageUrl(part.ImageUrl.Url);
                    input.Add(new CodexInputItem("image", Url: part.ImageUrl.Url));
                }
            }
        }

        return input;
    }

    private static void ValidateImageUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return;
        }

        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw InvalidImage();

        var comma = url.IndexOf(',');
        if (comma <= 5) throw InvalidImage();
        var metadata = url[5..comma].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (metadata.Length != 2 ||
            !SupportedImageTypes.Contains(metadata[0].ToLowerInvariant()) ||
            !string.Equals(metadata[1], "base64", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidImage();
        }

        var encoded = url[(comma + 1)..];
        if (encoded.Length == 0 || encoded.Length % 4 != 0 || encoded.Any(char.IsWhiteSpace))
            throw InvalidImage();
        var padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
        var decodedLength = encoded.Length / 4 * 3 - padding;
        if (decodedLength > MaxDataImageBytes) throw InvalidImage("Data image exceeds the 20 MiB limit.");

        var buffer = new byte[decodedLength];
        if (!Convert.TryFromBase64String(encoded, buffer, out var written) || written != decodedLength)
            throw InvalidImage();
    }

    private static PipelineValidationException InvalidImage(string message = "Image URL is invalid or unsupported.") =>
        new(message, "messages", "invalid_image_url");

    private static ChatCompletionResponse ToResponse(CodexTurnResult result, string? requestedModel) => new()
    {
        Id = "chatcmpl_" + result.Id,
        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Model = string.IsNullOrWhiteSpace(result.Model) ? requestedModel ?? "codex" : result.Model,
        Choices =
        [
            new ChatCompletionChoiceDto
            {
                Index = 0,
                Message = new ChatMessageDto { Role = "assistant", Content = result.Content },
                FinishReason = "stop"
            }
        ],
        Usage = result.InputTokens is null && result.OutputTokens is null
            ? null
            : new UsageDto
            {
                PromptTokens = result.InputTokens,
                CompletionTokens = result.OutputTokens,
                TotalTokens = result.InputTokens + result.OutputTokens
            }
    };

    private static string CreateChunk(
        string id,
        long created,
        string model,
        string? content = null,
        string? reasoning = null,
        string? finishReason = null) =>
        JsonSerializer.Serialize(new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content, reasoning },
                    finish_reason = finishReason
                }
            }
        }, JsonOptions.Default);
}
