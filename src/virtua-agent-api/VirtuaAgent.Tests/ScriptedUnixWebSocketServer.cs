using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtuaAgent.Tests;

internal sealed class ScriptedUnixWebSocketServer : IAsyncDisposable
{
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly Socket _listener;
    private readonly Task _run;

    private ScriptedUnixWebSocketServer(string path, Socket listener, Task run)
    {
        Path = path;
        _listener = listener;
        _run = run;
    }

    public string Path { get; }

    public static Task<ScriptedUnixWebSocketServer> StartAsync(Func<WebSocket, Task> script)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"va-{Guid.NewGuid():N}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var run = Task.Run(async () =>
        {
            using var socket = await listener.AcceptAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await UpgradeAsync(stream);
            using var webSocket = WebSocket.CreateFromStream(
                stream,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: Timeout.InfiniteTimeSpan);
            await script(webSocket);
        });
        return Task.FromResult(new ScriptedUnixWebSocketServer(path, listener, run));
    }

    public static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException($"Expected text frame, received {result.MessageType}.");
            content.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        content.Position = 0;
        return await JsonDocument.ParseAsync(content);
    }

    public static Task SendJsonAsync(WebSocket socket, string json) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

    public static async Task SendFragmentedJsonAsync(WebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var split = Math.Max(1, bytes.Length / 2);
        await socket.SendAsync(
            bytes.AsMemory(0, split),
            WebSocketMessageType.Text,
            endOfMessage: false,
            CancellationToken.None);
        await socket.SendAsync(
            bytes.AsMemory(split),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _run.WaitAsync(TimeSpan.FromSeconds(5));
        if (File.Exists(Path)) File.Delete(Path);
    }

    private static async Task UpgradeAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        if (requestLine is null || !requestLine.StartsWith("GET ", StringComparison.Ordinal))
            throw new InvalidDataException("Expected WebSocket upgrade request.");

        string? key = null;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                key = line[(line.IndexOf(':') + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Missing Sec-WebSocket-Key.");
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic)));
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }
}
