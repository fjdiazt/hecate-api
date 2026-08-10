using VirtuaAgent.Codex;
using VirtuaAgent.OpenAi;
using VirtuaAgent.Orchestration;

namespace VirtuaAgent.Tests;

public sealed class CodexSubscriptionClientTests
{
    [Fact]
    public async Task ChatMapsRolesAndMultimodalPartsInOrder()
    {
        var appServer = new FakeCodexAppServerClient();
        var client = new CodexSubscriptionClient(appServer);
        var request = new ChatCompletionRequest
        {
            Model = "gpt-5.3-codex",
            Messages =
            [
                new ChatMessageDto
                {
                    Role = "user",
                    Content = ChatMessageContent.FromParts([
                        ChatMessageContentPart.FromText("describe"),
                        ChatMessageContentPart.FromImageUrl("https://example.test/image.png"),
                        ChatMessageContentPart.FromText("briefly")
                    ])
                }
            ]
        };

        await client.ChatAsync(request);

        Assert.Collection(appServer.Input!,
            item => Assert.Equal("Role: user", item.Text),
            item => Assert.Equal("describe", item.Text),
            item => Assert.Equal("https://example.test/image.png", item.Url),
            item => Assert.Equal("briefly", item.Text));
    }

    [Fact]
    public async Task ChatPassesValidatedDataImageWithoutDecodingToDisk()
    {
        var appServer = new FakeCodexAppServerClient();
        var client = new CodexSubscriptionClient(appServer);
        var dataUrl = "data:image/png;base64,iVBORw0KGgo=";

        await client.ChatAsync(RequestWithImage(dataUrl));

        Assert.Contains(appServer.Input!, item => item.Type == "image" && item.Url == dataUrl);
    }

    [Fact]
    public async Task ChatRejectsUnsupportedDataImageMimeType()
    {
        var client = new CodexSubscriptionClient(new FakeCodexAppServerClient());

        var error = await Assert.ThrowsAsync<PipelineValidationException>(() =>
            client.ChatAsync(RequestWithImage("data:image/svg+xml;base64,PHN2Zz4=")));

        Assert.Equal("messages", error.Param);
        Assert.Equal("invalid_image_url", error.Code);
    }

    [Fact]
    public async Task ChatRejectsDataImageOverTwentyMibibytes()
    {
        var client = new CodexSubscriptionClient(new FakeCodexAppServerClient());
        var encoded = new string('A', ((20 * 1024 * 1024 + 1 + 2) / 3) * 4);

        var error = await Assert.ThrowsAsync<PipelineValidationException>(() =>
            client.ChatAsync(RequestWithImage($"data:image/png;base64,{encoded}")));

        Assert.Equal("messages", error.Param);
        Assert.Equal("invalid_image_url", error.Code);
    }

    [Fact]
    public async Task ChatMapsFinalResponseAndUsage()
    {
        var appServer = new FakeCodexAppServerClient
        {
            Result = new CodexTurnResult
            {
                Id = "turn_1",
                Model = "gpt-5.3-codex",
                Content = "final answer",
                InputTokens = 12,
                OutputTokens = 5
            }
        };
        var client = new CodexSubscriptionClient(appServer);

        var response = await client.ChatAsync(TextRequest());

        Assert.Equal("chatcmpl_turn_1", response.Id);
        Assert.Equal("gpt-5.3-codex", response.Model);
        Assert.Equal("assistant", response.Choices[0].Message.Role);
        Assert.Equal("final answer", response.Choices[0].Message.Content.AsText());
        Assert.Equal("stop", response.Choices[0].FinishReason);
        Assert.Equal(12, response.Usage!.PromptTokens);
        Assert.Equal(5, response.Usage.CompletionTokens);
        Assert.Equal(17, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task ListModelsMapsOwnershipAndPreservesOrder()
    {
        var appServer = new FakeCodexAppServerClient
        {
            Models = [new CodexModel("model-b"), new CodexModel("model-a")]
        };
        var client = new CodexSubscriptionClient(appServer);

        var response = await client.ListModelsAsync();

        Assert.Equal(["model-b", "model-a"], response.Data.Select(model => model.Id));
        Assert.All(response.Data, model => Assert.Equal("openai-codex", model.OwnedBy));
    }

    [Fact]
    public async Task StreamMapsReasoningContentAndFinishChunks()
    {
        var appServer = new FakeCodexAppServerClient
        {
            Deltas =
            [
                new CodexTurnDelta(Reasoning: "checking"),
                new CodexTurnDelta(Content: "answer")
            ]
        };
        var client = new CodexSubscriptionClient(appServer);
        var data = new List<string>();

        await client.StreamChatAsync(TextRequest(), (chunk, _) =>
        {
            data.Add(chunk);
            return Task.CompletedTask;
        });

        var deltas = data.Select(OpenAiStreamData.ParseDelta).Where(delta => delta is not null).ToList();
        Assert.Contains(deltas, delta => delta!.Reasoning == "checking");
        Assert.Contains(deltas, delta => delta!.Content == "answer");
        Assert.Contains(deltas, delta => delta!.FinishReason == "stop");
        Assert.DoesNotContain(data, chunk => chunk.StartsWith("data:", StringComparison.Ordinal));
    }

    private static ChatCompletionRequest TextRequest() => new()
    {
        Model = "gpt-5.3-codex",
        Messages = [new ChatMessageDto { Role = "user", Content = "hello" }]
    };

    private static ChatCompletionRequest RequestWithImage(string url) => new()
    {
        Model = "gpt-5.3-codex",
        Messages =
        [
            new ChatMessageDto
            {
                Role = "user",
                Content = ChatMessageContent.FromParts([ChatMessageContentPart.FromImageUrl(url)])
            }
        ]
    };

    private sealed class FakeCodexAppServerClient : ICodexAppServerClient
    {
        public IReadOnlyList<CodexModel> Models { get; init; } = [new CodexModel("gpt-5.3-codex")];
        public IReadOnlyList<CodexTurnDelta> Deltas { get; init; } = [];
        public CodexTurnResult Result { get; init; } = new()
        {
            Id = "turn_test",
            Model = "gpt-5.3-codex",
            Content = "answer"
        };
        public IReadOnlyList<CodexInputItem>? Input { get; private set; }

        public Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Models);

        public async Task<CodexTurnResult> RunTurnAsync(
            string? model,
            IReadOnlyList<CodexInputItem> input,
            Func<CodexTurnDelta, CancellationToken, Task>? onDelta = null,
            CancellationToken cancellationToken = default)
        {
            Input = input;
            if (onDelta is not null)
            {
                foreach (var delta in Deltas) await onDelta(delta, cancellationToken);
            }

            return Result;
        }
    }
}
