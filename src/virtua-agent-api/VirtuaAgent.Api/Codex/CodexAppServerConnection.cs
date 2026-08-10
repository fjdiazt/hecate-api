using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using VirtuaAgent.OpenAi;

namespace VirtuaAgent.Codex;

internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly HttpMessageInvoker _invoker;
    private readonly Queue<JsonElement> _pending = new();
    private int _nextId;

    private CodexAppServerConnection(ClientWebSocket webSocket, HttpMessageInvoker invoker)
    {
        _webSocket = webSocket;
        _invoker = invoker;
    }

    public static async Task<CodexAppServerConnection> OpenInitializedAsync(
        CodexOptions options,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(options.SocketPath))
            throw new InvalidOperationException("Codex socket path must be absolute.");

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var webSocket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ConnectTimeoutSeconds)));
        CodexAppServerConnection? connection = null;
        try
        {
            await webSocket.ConnectAsync(new Uri("ws://localhost/"), invoker, timeout.Token);
            connection = new CodexAppServerConnection(webSocket, invoker);
            _ = CodexAppServerProtocol.Result(await connection.RequestAsync("initialize", new
            {
                clientInfo = new { name = "virtua-agent", title = "Virtua Agent", version = "1" },
                capabilities = new { experimentalApi = false }
            }, cancellationToken));
            await connection.NotifyAsync("initialized", cancellationToken);
            return connection;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            else
            {
                webSocket.Dispose();
                invoker.Dispose();
            }
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = await WriteRequestAsync(method, parameters, cancellationToken);
        while (true)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (CodexAppServerProtocol.ResponseId(message) == id) return message;
            _pending.Enqueue(message);
        }
    }

    public async Task<int> WriteRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        await WriteAsync(new { method, id, @params = parameters }, cancellationToken);
        return id;
    }

    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteAsync(new { method }, cancellationToken);

    public Task<JsonElement> ReadAsync(CancellationToken cancellationToken) =>
        _pending.TryDequeue(out var message)
            ? Task.FromResult(message)
            : ReceiveAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    null,
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }

        _webSocket.Dispose();
        _invoker.Dispose();
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var content = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException();
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Codex app-server sent a non-text WebSocket message.");
            content.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(content.ToArray());
        return document.RootElement.Clone();
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions.Default);
        await _webSocket.SendAsync(
            json.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }
}
