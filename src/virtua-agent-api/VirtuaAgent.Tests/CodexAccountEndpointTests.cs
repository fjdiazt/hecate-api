using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VirtuaAgent.Codex;
using VirtuaAgent.OpenAi;

namespace VirtuaAgent.Tests;

public sealed class CodexAccountEndpointTests
{
    [Fact]
    public async Task GetReturnsConnectedStateWithOpenAiStyleNames()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/read", """{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"}}""");
        });
        await using var factory = Factory(server.Path);

        var response = await factory.CreateClient().GetAsync("/v1/codex/account");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"connected\"", json, StringComparison.Ordinal);
        Assert.Contains("\"plan_type\":\"plus\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("planType", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginRequiresJsonContentType()
    {
        await using var factory = Factory(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");

        var response = await factory.CreateClient().PostAsync("/v1/codex/account/login", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task LoginUnavailableReturnsStateWithServiceUnavailable()
    {
        await using var factory = Factory(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));

        var response = await factory.CreateClient().PostAsJsonAsync("/v1/codex/account/login", new { });
        var state = await response.Content.ReadFromJsonAsync<CodexAccountState>(JsonOptions.Default);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(CodexAccountStatuses.Unavailable, state!.Status);
    }

    [Fact]
    public async Task CancelWithoutAttemptIsIdempotent()
    {
        await using var factory = Factory(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));

        var response = await factory.CreateClient().DeleteAsync("/v1/codex/account/login");
        var state = await response.Content.ReadFromJsonAsync<CodexAccountState>(JsonOptions.Default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CodexAccountStatuses.Disconnected, state!.Status);
    }

    [Fact]
    public async Task LogoutReturnsDisconnected()
    {
        await using var server = await ScriptedUnixWebSocketServer.StartAsync(async socket =>
        {
            await InitializeAsync(socket);
            await RespondAsync(socket, "account/logout", "{}");
        });
        await using var factory = Factory(server.Path);

        var response = await factory.CreateClient().DeleteAsync("/v1/codex/account");
        var state = await response.Content.ReadFromJsonAsync<CodexAccountState>(JsonOptions.Default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CodexAccountStatuses.Disconnected, state!.Status);
    }

    private static WebApplicationFactory<Program> Factory(string socketPath) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Codex:SocketPath"] = socketPath,
                        ["Codex:ConnectTimeoutSeconds"] = "1",
                        ["Codex:InterruptTimeoutSeconds"] = "1",
                        ["Codex:LoginTimeoutSeconds"] = "2"
                    });
                });
            });

    private static async Task InitializeAsync(WebSocket socket)
    {
        await RespondAsync(socket, "initialize", "{}");
        using var initialized = await ScriptedUnixWebSocketServer.ReceiveJsonAsync(socket);
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
    }

    private static async Task RespondAsync(WebSocket socket, string method, string result)
    {
        using var request = await ScriptedUnixWebSocketServer.ReceiveJsonAsync(socket);
        Assert.Equal(method, request.RootElement.GetProperty("method").GetString());
        var id = request.RootElement.GetProperty("id").GetInt32();
        await ScriptedUnixWebSocketServer.SendJsonAsync(socket, $"{{\"id\":{id},\"result\":{result}}}");
    }
}
