using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VirtuaAgent.Codex;

namespace VirtuaAgent.Tests;

public sealed class CodexAccountManagerTests
{
    [Fact]
    public async Task GetStateReturnsConnectedAccount()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"}}""");
        });
        await using var manager = CreateManager(server.Path);

        var state = await manager.GetStateAsync();

        Assert.Equal(CodexAccountStatuses.Connected, state.Status);
        Assert.Equal("user@example.com", state.Email);
        Assert.Equal("plus", state.PlanType);
    }

    [Fact]
    public async Task GetStateReturnsDisconnectedWithoutChatGptAccount()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
        });
        await using var manager = CreateManager(server.Path);

        var state = await manager.GetStateAsync();

        Assert.Equal(CodexAccountStatuses.Disconnected, state.Status);
    }

    [Fact]
    public async Task GetStateSanitizesUnavailableSidecar()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock");
        await using var manager = CreateManager(missing);

        var state = await manager.GetStateAsync();

        Assert.Equal(CodexAccountStatuses.Unavailable, state.Status);
        Assert.DoesNotContain(missing, state.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartLoginUsesDeviceCodeAndCompletesWithAccountRead()
    {
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
            using var start = await ReadAsync(socket, "account/login/start");
            Assert.Equal("chatgptDeviceCode", start.RootElement.GetProperty("params").GetProperty("type").GetString());
            await SendResultAsync(socket, start, """{"loginId":"login-1","verificationUrl":"https://example.com/device","userCode":"ABCD-EFGH"}""");
            await allowCompletion.Task;
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"account/login/completed","params":{"loginId":"login-1","success":true,"error":null}}""");
            await RespondAsync(socket, "account/read", """{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"}}""");
            await ExpectCloseAsync(socket);
            loginClosed.SetResult();
        });
        await using var manager = CreateManager(server.Path);

        var state = await manager.StartLoginAsync();

        Assert.Equal(CodexAccountStatuses.Connecting, state.Status);
        Assert.Equal("https://example.com/device", state.VerificationUrl);
        Assert.Equal("ABCD-EFGH", state.UserCode);
        allowCompletion.SetResult();
        await loginClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletionBeforeStartResponseIsPreserved()
    {
        var loginClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
            using var start = await ReadAsync(socket, "account/login/start");
            await ScriptedUnixWebSocketServer.SendJsonAsync(socket, """{"method":"account/login/completed","params":{"loginId":"login-1","success":true,"error":null}}""");
            await SendResultAsync(socket, start, """{"loginId":"login-1","verificationUrl":"https://example.com/device","userCode":"ABCD-EFGH"}""");
            await RespondAsync(socket, "account/read", """{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"}}""");
            await ExpectCloseAsync(socket);
            loginClosed.SetResult();
        });
        await using var manager = CreateManager(server.Path);

        var state = await manager.StartLoginAsync();

        Assert.Equal(CodexAccountStatuses.Connecting, state.Status);
        await loginClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentStartsShareOperationAndCallerCancellationDoesNotCancelLogin()
    {
        var startReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
            using var start = await ReadAsync(socket, "account/login/start");
            startReceived.SetResult();
            await releaseStart.Task;
            await SendResultAsync(socket, start, """{"loginId":"login-1","verificationUrl":"https://example.com/device","userCode":"ABCD-EFGH"}""");
            using var cancel = await ReadAsync(socket, "account/login/cancel");
            Assert.Equal("login-1", cancel.RootElement.GetProperty("params").GetProperty("loginId").GetString());
            await SendResultAsync(socket, cancel, "{}");
            await ExpectCloseAsync(socket);
        });
        await using var manager = CreateManager(server.Path);
        using var cancellation = new CancellationTokenSource();
        var cancelledCaller = manager.StartLoginAsync(cancellation.Token);
        await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var survivingCaller = manager.StartLoginAsync();

        cancellation.Cancel();
        releaseStart.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);
        Assert.Equal(CodexAccountStatuses.Connecting, (await survivingCaller).Status);
        Assert.Equal(CodexAccountStatuses.Disconnected, (await manager.CancelLoginAsync()).Status);
    }

    [Fact]
    public async Task LoginTimeoutCancelsAttemptAndReturnsError()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
            using var start = await ReadAsync(socket, "account/login/start");
            await SendResultAsync(socket, start, """{"loginId":"login-1","verificationUrl":"https://example.com/device","userCode":"ABCD-EFGH"}""");
            using var cancel = await ReadAsync(socket, "account/login/cancel");
            await SendResultAsync(socket, cancel, "{}");
            await ExpectCloseAsync(socket);
            cancelled.SetResult();
        });
        await using var manager = CreateManager(server.Path, loginTimeoutSeconds: 1);

        Assert.Equal(CodexAccountStatuses.Connecting, (await manager.StartLoginAsync()).Status);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var state = await manager.GetStateAsync();
        Assert.Equal(CodexAccountStatuses.Error, state.Status);
        Assert.Contains("timed out", state.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginTimeoutIncludesStartRequest()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
            using var start = await ReadAsync(socket, "account/login/start");
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
        });
        await using var manager = CreateManager(server.Path, loginTimeoutSeconds: 1);

        var state = await manager.StartLoginAsync();

        Assert.Equal(CodexAccountStatuses.Error, state.Status);
        Assert.Contains("timed out", state.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidLoginResponseReturnsSanitizedError()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":null}""");
            using var start = await ReadAsync(socket, "account/login/start");
            await SendResultAsync(socket, start, """{"loginId":"secret-login","verificationUrl":"http://unsafe.test/device","userCode":"SECRET"}""");
        });
        await using var manager = CreateManager(server.Path);

        var state = await manager.StartLoginAsync();

        Assert.Equal(CodexAccountStatuses.Error, state.Status);
        Assert.DoesNotContain("secret-login", state.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe.test", state.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", state.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogoutReturnsDisconnected()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/logout", "{}");
        });
        await using var manager = CreateManager(server.Path);

        var state = await manager.LogoutAsync();

        Assert.Equal(CodexAccountStatuses.Disconnected, state.Status);
    }

    [Fact]
    public async Task LogoutFailureReadsVerifiedStateOnNewConnection()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(
            async socket =>
            {
                await InitializeAsync(socket);
                using var logout = await ReadAsync(socket, "account/logout");
                await SendErrorAsync(socket, logout, "logout failed");
            },
            async socket =>
            {
                await InitializeAsync(socket);
                await RespondAsync(socket, "account/read", """{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"}}""");
            });
        await using var manager = CreateManager(server.Path);

        var state = await manager.LogoutAsync();

        Assert.Equal(CodexAccountStatuses.Connected, state.Status);
        Assert.Equal("user@example.com", state.Email);
    }

    private static CodexAccountManager CreateManager(string socketPath, int loginTimeoutSeconds = 10) =>
        new(Options.Create(new CodexOptions
        {
            SocketPath = socketPath,
            WorkingDirectory = "/work",
            ConnectTimeoutSeconds = 1,
            InterruptTimeoutSeconds = 1,
            LoginTimeoutSeconds = loginTimeoutSeconds
        }));

    private static async Task InitializeAsync(WebSocket socket)
    {
        await RespondAsync(socket, "initialize", "{}");
        using var initialized = await ScriptedUnixWebSocketServer.ReceiveJsonAsync(socket);
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
    }

    private static async Task RespondAsync(WebSocket socket, string method, string result)
    {
        using var request = await ReadAsync(socket, method);
        await SendResultAsync(socket, request, result);
    }

    private static async Task<JsonDocument> ReadAsync(WebSocket socket, string method)
    {
        var request = await ScriptedUnixWebSocketServer.ReceiveJsonAsync(socket);
        Assert.Equal(method, request.RootElement.GetProperty("method").GetString());
        return request;
    }

    private static Task SendResultAsync(WebSocket socket, JsonDocument request, string result) =>
        ScriptedUnixWebSocketServer.SendJsonAsync(
            socket,
            $"{{\"id\":{request.RootElement.GetProperty("id").GetInt32()},\"result\":{result}}}");

    private static Task SendErrorAsync(WebSocket socket, JsonDocument request, string message) =>
        ScriptedUnixWebSocketServer.SendJsonAsync(
            socket,
            $"{{\"id\":{request.RootElement.GetProperty("id").GetInt32()},\"error\":{{\"message\":{JsonSerializer.Serialize(message)}}}}}");

    private static async Task ExpectCloseAsync(WebSocket socket)
    {
        var buffer = new byte[1];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }
}
