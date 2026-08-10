using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VirtuaAgent.Codex;

namespace VirtuaAgent.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task ListModelsInitializesAuthenticatesAndPaginates()
    {
        await using var server = await ScriptedUnixServer.StartAsync(async (reader, writer) =>
        {
            await RespondAsync(reader, writer, "initialize", """{"id":1,"result":{"platformFamily":"unix"}}""");
            Assert.Equal("initialized", await ReadMethodAsync(reader));
            await RespondAsync(reader, writer, "account/read", """{"id":2,"result":{"account":{"type":"chatgpt","email":null,"planType":"plus"},"requiresOpenaiAuth":true}}""");
            var first = await ReadAsync(reader, "model/list");
            Assert.False(first.RootElement.GetProperty("params").TryGetProperty("cursor", out _));
            await writer.WriteLineAsync("""{"id":3,"result":{"data":[{"id":"gpt-5.3-codex","hidden":false}],"nextCursor":"page-2"}}""");
            var second = await ReadAsync(reader, "model/list");
            Assert.Equal("page-2", second.RootElement.GetProperty("params").GetProperty("cursor").GetString());
            await writer.WriteLineAsync("""{"id":4,"result":{"data":[{"id":"gpt-5.3-codex","hidden":false},{"id":"gpt-5.2-codex","hidden":false}],"nextCursor":null}}""");
        });
        var client = CreateClient(server.Path);

        var models = await client.ListModelsAsync();

        Assert.Equal(["gpt-5.3-codex", "gpt-5.2-codex"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task RunTurnUsesEphemeralReadOnlyThreadAndStreamsDeltas()
    {
        await using var server = await ScriptedUnixServer.StartAsync(async (reader, writer) =>
        {
            await CompleteHandshakeAsync(reader, writer);
            var thread = await ReadAsync(reader, "thread/start");
            var threadParams = thread.RootElement.GetProperty("params");
            Assert.True(threadParams.GetProperty("ephemeral").GetBoolean());
            Assert.Equal("read-only", threadParams.GetProperty("sandbox").GetString());
            Assert.Equal("never", threadParams.GetProperty("approvalPolicy").GetString());
            Assert.Equal("/work", threadParams.GetProperty("cwd").GetString());
            await writer.WriteLineAsync("""{"id":3,"result":{"thread":{"id":"thr_1"}}}""");

            var turn = await ReadAsync(reader, "turn/start");
            var turnParams = turn.RootElement.GetProperty("params");
            var sandbox = turnParams.GetProperty("sandboxPolicy");
            Assert.Equal("readOnly", sandbox.GetProperty("type").GetString());
            Assert.False(sandbox.GetProperty("networkAccess").GetBoolean());
            var input = turnParams.GetProperty("input")[0];
            Assert.Equal("text", input.GetProperty("type").GetString());
            Assert.Equal("hello", input.GetProperty("text").GetString());
            Assert.False(input.TryGetProperty("url", out _));
            await writer.WriteLineAsync("""{"id":4,"result":{"turn":{"id":"turn_1","status":"inProgress","items":[]}}}""");
            await writer.WriteLineAsync("""{"method":"item/reasoning/summaryTextDelta","params":{"threadId":"thr_1","turnId":"turn_1","delta":"checking"}}""");
            await writer.WriteLineAsync("""{"method":"item/agentMessage/delta","params":{"threadId":"thr_1","turnId":"turn_1","delta":"answer"}}""");
            await writer.WriteLineAsync("""{"method":"item/completed","params":{"threadId":"thr_1","turnId":"turn_1","item":{"type":"agentMessage","id":"item_1","text":"final answer"}}}""");
            await writer.WriteLineAsync("""{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"completed","items":[]}}}""");
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
    public async Task CancellationSendsTurnInterrupt()
    {
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ScriptedUnixServer.StartAsync(async (reader, writer) =>
        {
            await CompleteHandshakeAsync(reader, writer);
            await RespondAsync(reader, writer, "thread/start", """{"id":3,"result":{"thread":{"id":"thr_1"}}}""");
            await RespondAsync(reader, writer, "turn/start", """{"id":4,"result":{"turn":{"id":"turn_1","status":"inProgress","items":[]}}}""");
            turnStarted.SetResult();
            var interrupt = await ReadAsync(reader, "turn/interrupt");
            Assert.Equal("thr_1", interrupt.RootElement.GetProperty("params").GetProperty("threadId").GetString());
            Assert.Equal("turn_1", interrupt.RootElement.GetProperty("params").GetProperty("turnId").GetString());
            await writer.WriteLineAsync("""{"id":5,"result":{}}""");
            await writer.WriteLineAsync("""{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"interrupted","items":[]}}}""");
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
        await using var server = await ScriptedUnixServer.StartAsync(async (reader, writer) =>
        {
            await RespondAsync(reader, writer, "initialize", """{"id":1,"result":{}}""");
            Assert.Equal("initialized", await ReadMethodAsync(reader));
            await RespondAsync(reader, writer, "account/read", """{"id":2,"result":{"account":{"type":"apiKey","email":"secret@example.com"},"requiresOpenaiAuth":true}}""");
        });
        var client = CreateClient(server.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());

        Assert.Contains("Codex subscription is not authenticated", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedSocketReturnsCodexUnavailableError()
    {
        await using var server = await ScriptedUnixServer.StartAsync(async (reader, writer) =>
        {
            _ = await reader.ReadLineAsync();
        });
        var client = CreateClient(server.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());

        Assert.Equal("Codex sidecar is unavailable.", error.Message);
    }

    private static CodexAppServerClient CreateClient(string socketPath) =>
        new(Options.Create(new CodexOptions
        {
            SocketPath = socketPath,
            WorkingDirectory = "/work",
            ConnectTimeoutSeconds = 5,
            InterruptTimeoutSeconds = 5
        }));

    private static async Task CompleteHandshakeAsync(StreamReader reader, StreamWriter writer)
    {
        await RespondAsync(reader, writer, "initialize", """{"id":1,"result":{}}""");
        Assert.Equal("initialized", await ReadMethodAsync(reader));
        await RespondAsync(reader, writer, "account/read", """{"id":2,"result":{"account":{"type":"chatgpt","email":null,"planType":"plus"},"requiresOpenaiAuth":true}}""");
    }

    private static async Task RespondAsync(StreamReader reader, StreamWriter writer, string method, string response)
    {
        _ = await ReadAsync(reader, method);
        await writer.WriteLineAsync(response);
    }

    private static async Task<JsonDocument> ReadAsync(StreamReader reader, string method)
    {
        var line = await reader.ReadLineAsync();
        Assert.NotNull(line);
        var document = JsonDocument.Parse(line);
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        return document;
    }

    private static async Task<string?> ReadMethodAsync(StreamReader reader)
    {
        using var document = await ReadAsync(reader, "initialized");
        return document.RootElement.GetProperty("method").GetString();
    }

    private sealed class ScriptedUnixServer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly Task _run;

        private ScriptedUnixServer(string path, Socket listener, Task run)
        {
            Path = path;
            _listener = listener;
            _run = run;
        }

        public string Path { get; }

        public static Task<ScriptedUnixServer> StartAsync(Func<StreamReader, StreamWriter, Task> script)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"va-{Guid.NewGuid():N}.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var run = Task.Run(async () =>
            {
                using var socket = await listener.AcceptAsync();
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await script(reader, writer);
            });
            return Task.FromResult(new ScriptedUnixServer(path, listener, run));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
