using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VirtuaAgent.Codex;

namespace VirtuaAgent.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task ListModelsInitializesAuthenticatesAndPaginates()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await RespondAsync(socket, "initialize", """{"id":1,"result":{"platformFamily":"unix"}}""");
            Assert.Equal("initialized", await ReadMethodAsync(socket));
            await RespondAsync(socket, "account/read", """{"id":2,"result":{"account":{"type":"chatgpt","email":null,"planType":"plus"},"requiresOpenaiAuth":true}}""");
            var first = await ReadAsync(socket, "model/list");
            Assert.False(first.RootElement.GetProperty("params").TryGetProperty("cursor", out _));
            await ScriptedUnixWebSocketServer.SendFragmentedJsonAsync(socket, """{"id":3,"result":{"data":[{"id":"gpt-5.3-codex","hidden":false}],"nextCursor":"page-2"}}""");
            var second = await ReadAsync(socket, "model/list");
            Assert.Equal("page-2", second.RootElement.GetProperty("params").GetProperty("cursor").GetString());
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"id":4,"result":{"data":[{"id":"gpt-5.3-codex","hidden":false},{"id":"gpt-5.2-codex","hidden":false}],"nextCursor":null}}""");
        });
        var client = CreateClient(server.Path);

        var models = await client.ListModelsAsync();

        Assert.Equal(["gpt-5.3-codex", "gpt-5.2-codex"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task RunTurnUsesEphemeralReadOnlyThreadAndStreamsDeltas()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var thread = await ReadAsync(socket, "thread/start");
            var threadParams = thread.RootElement.GetProperty("params");
            Assert.True(threadParams.GetProperty("ephemeral").GetBoolean());
            Assert.Equal("read-only", threadParams.GetProperty("sandbox").GetString());
            Assert.Equal("never", threadParams.GetProperty("approvalPolicy").GetString());
            Assert.Equal("/work", threadParams.GetProperty("cwd").GetString());
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"id":3,"result":{"thread":{"id":"thr_1"}}}""");

            var turn = await ReadAsync(socket, "turn/start");
            var turnParams = turn.RootElement.GetProperty("params");
            var sandbox = turnParams.GetProperty("sandboxPolicy");
            Assert.Equal("readOnly", sandbox.GetProperty("type").GetString());
            Assert.False(sandbox.GetProperty("networkAccess").GetBoolean());
            var input = turnParams.GetProperty("input")[0];
            Assert.Equal("text", input.GetProperty("type").GetString());
            Assert.Equal("hello", input.GetProperty("text").GetString());
            Assert.False(input.TryGetProperty("url", out _));
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"id":4,"result":{"turn":{"id":"turn_1","status":"inProgress","items":[]}}}""");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"item/reasoning/summaryTextDelta","params":{"threadId":"thr_1","turnId":"turn_1","delta":"checking"}}""");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"item/agentMessage/delta","params":{"threadId":"thr_1","turnId":"turn_1","delta":"answer"}}""");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"item/completed","params":{"threadId":"thr_1","turnId":"turn_1","item":{"type":"agentMessage","id":"item_1","text":"final answer"}}}""");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"completed","items":[]}}}""");
        });
        var deltas = new List<CodexTurnDelta>();
        var client = CreateClient(server.Path);

        var result = await client.RunTurnAsync(
            "gpt-5.3-codex",
            [new CodexInputItem("text", Text: "hello")],
            (delta, _) => { deltas.Add(delta); return Task.CompletedTask; });

        Assert.Equal("turn_1", result.Id);
        Assert.Equal("gpt-5.3-codex", result.Model);
        Assert.Equal("final answer", result.Content);
        Assert.Contains(deltas, delta => delta.Reasoning == "checking");
        Assert.Contains(deltas, delta => delta.Content == "answer");
    }

    [Fact]
    public async Task NotificationBeforeResponseIsPreserved()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            await RespondAsync(socket, "thread/start", """{"id":3,"result":{"thread":{"id":"thr_1"}}}""");
            _ = await ReadAsync(socket, "turn/start");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"completed","items":[]}}}""");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"id":4,"result":{"turn":{"id":"turn_1","status":"inProgress","items":[]}}}""");
        });
        var client = CreateClient(server.Path);

        var result = await client.RunTurnAsync(null, [new CodexInputItem("text", Text: "hello")]);

        Assert.Equal("turn_1", result.Id);
    }

    [Fact]
    public async Task CancellationSendsTurnInterrupt()
    {
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            await RespondAsync(socket, "thread/start", """{"id":3,"result":{"thread":{"id":"thr_1"}}}""");
            await RespondAsync(socket, "turn/start", """{"id":4,"result":{"turn":{"id":"turn_1","status":"inProgress","items":[]}}}""");
            turnStarted.SetResult();
            var interrupt = await ReadAsync(socket, "turn/interrupt");
            Assert.Equal("thr_1", interrupt.RootElement.GetProperty("params").GetProperty("threadId").GetString());
            Assert.Equal("turn_1", interrupt.RootElement.GetProperty("params").GetProperty("turnId").GetString());
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"id":5,"result":{}}""");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"interrupted","items":[]}}}""");
        });
        using var cancellation = new CancellationTokenSource();
        var client = CreateClient(server.Path);
        var run = client.RunTurnAsync(null, [new CodexInputItem("text", Text: "wait")], cancellationToken: cancellation.Token);
        await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task NonChatGptAccountIsRejectedWithoutLeakingAccountData()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await RespondAsync(socket, "initialize", """{"id":1,"result":{}}""");
            Assert.Equal("initialized", await ReadMethodAsync(socket));
            await RespondAsync(socket, "account/read", """{"id":2,"result":{"account":{"type":"apiKey","email":"secret@example.com"},"requiresOpenaiAuth":true}}""");
        });
        var client = CreateClient(server.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());

        Assert.Contains("Codex subscription is not authenticated", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedWebSocketReturnsCodexUnavailableError()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(_ => Task.CompletedTask);
        var client = CreateClient(server.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());

        Assert.Equal("Codex sidecar is unavailable.", error.Message);
    }

    [Fact]
    public async Task HandshakeErrorClosesConnection()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await RespondAsync(socket, "initialize", """{"id":1,"error":{"message":"initialize failed"}}""");
            var buffer = new byte[1];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        });
        var client = CreateClient(server.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());

        Assert.Equal("initialize failed", error.Message);
    }

    private static CodexAppServerClient CreateClient(string socketPath) =>
        new(Options.Create(new CodexOptions
        {
            SocketPath = socketPath,
            WorkingDirectory = "/work",
            ConnectTimeoutSeconds = 5,
            InterruptTimeoutSeconds = 5
        }));

    private static async Task CompleteHandshakeAsync(WebSocket socket)
    {
        await RespondAsync(socket, "initialize", """{"id":1,"result":{}}""");
        Assert.Equal("initialized", await ReadMethodAsync(socket));
        await RespondAsync(socket, "account/read", """{"id":2,"result":{"account":{"type":"chatgpt","email":null,"planType":"plus"},"requiresOpenaiAuth":true}}""");
    }

    private static async Task RespondAsync(WebSocket socket, string method, string response)
    {
        _ = await ReadAsync(socket, method);
        await ScriptedUnixWebSocketServer.SendJsonAsync(socket, response);
    }

    private static async Task<JsonDocument> ReadAsync(WebSocket socket, string method)
    {
        var document = await ScriptedUnixWebSocketServer.ReceiveJsonAsync(socket);
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        return document;
    }

    private static async Task<string?> ReadMethodAsync(WebSocket socket)
    {
        using var document = await ReadAsync(socket, "initialized");
        return document.RootElement.GetProperty("method").GetString();
    }
}
