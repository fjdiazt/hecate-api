using VirtuaAgent.Codex;
using VirtuaAgent.ModelEndpoints;
using VirtuaAgent.OpenAi;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.Tests;

public sealed class ModelEndpointDispatcherTests
{
    [Fact]
    public async Task HttpEndpointUsesOpenAiCompatibleClient()
    {
        var upstream = new RecordingUpstream();
        var codex = new FakeCodexAppServerClient();
        var dispatcher = new ModelEndpointDispatcher(upstream, new CodexSubscriptionClient(codex));
        var endpoint = new ModelEndpointDefinition
        {
            Id = "llama",
            Kind = ModelEndpointKinds.OpenAiCompatible,
            BaseUrl = "http://llama.test"
        };

        await dispatcher.ChatAsync(Request(), endpoint);

        Assert.Equal("llama", upstream.EndpointIds.Single());
        Assert.Equal(0, codex.Turns);
    }

    [Fact]
    public async Task CodexEndpointUsesCodexClient()
    {
        var upstream = new RecordingUpstream();
        var codex = new FakeCodexAppServerClient();
        var dispatcher = new ModelEndpointDispatcher(upstream, new CodexSubscriptionClient(codex));
        var endpoint = new ModelEndpointDefinition
        {
            Id = "codex",
            Kind = ModelEndpointKinds.CodexSubscription
        };

        var response = await dispatcher.ChatAsync(Request(), endpoint);

        Assert.Equal("codex answer", response.Choices[0].Message.Content.AsText());
        Assert.Equal(1, codex.Turns);
        Assert.Empty(upstream.EndpointIds);
    }

    [Fact]
    public async Task CodexEndpointListsCodexModels()
    {
        var dispatcher = new ModelEndpointDispatcher(
            new RecordingUpstream(),
            new CodexSubscriptionClient(new FakeCodexAppServerClient()));

        var response = await dispatcher.ListModelsAsync(new ModelEndpointDefinition
        {
            Id = "codex",
            Kind = ModelEndpointKinds.CodexSubscription
        });

        Assert.Equal("model", Assert.Single(response.Data).Id);
        Assert.Equal("openai-codex", response.Data[0].OwnedBy);
    }

    private static ChatCompletionRequest Request() => new()
    {
        Model = "model",
        Messages = [new ChatMessageDto { Role = "user", Content = "hello" }]
    };

    private sealed class RecordingUpstream : IOpenAiCompatibleUpstreamClient
    {
        public List<string> EndpointIds { get; } = [];

        public Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelListResponse());

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, ModelEndpointDefinition endpoint, CancellationToken cancellationToken = default)
        {
            EndpointIds.Add(endpoint.Id);
            return Task.FromResult(Response("http answer"));
        }

        public Task StreamChatAsync(ChatCompletionRequest request, Stream output, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCodexAppServerClient : ICodexAppServerClient
    {
        public int Turns { get; private set; }

        public Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodexModel>>([new CodexModel("model")]);

        public Task<CodexTurnResult> RunTurnAsync(string? model, IReadOnlyList<CodexInputItem> input, Func<CodexTurnDelta, CancellationToken, Task>? onDelta = null, CancellationToken cancellationToken = default)
        {
            Turns++;
            return Task.FromResult(new CodexTurnResult { Id = "turn", Model = model ?? "codex", Content = "codex answer" });
        }
    }

    private static ChatCompletionResponse Response(string answer) => new()
    {
        Id = "chatcmpl_test",
        Model = "model",
        Choices = [new ChatCompletionChoiceDto { Message = new ChatMessageDto { Role = "assistant", Content = answer } }]
    };
}
