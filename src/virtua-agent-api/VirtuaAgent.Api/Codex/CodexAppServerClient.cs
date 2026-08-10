using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VirtuaAgent.OpenAi;

namespace VirtuaAgent.Codex;

public sealed class CodexAppServerClient(IOptions<CodexOptions> options) : ICodexAppServerClient
{
    private readonly CodexOptions _options = options.Value;

    public async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenInitializedAsync(cancellationToken);
        var models = new List<CodexModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var parameters = new Dictionary<string, object>
            {
                ["limit"] = 100,
                ["includeHidden"] = false
            };
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = CodexAppServerProtocol.Result(await connection.RequestAsync("model/list", parameters, cancellationToken));
            foreach (var model in result.GetProperty("data").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) models.Add(new CodexModel(id));
            }

            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (cursor is not null);

        return models;
    }

    public async Task<CodexTurnResult> RunTurnAsync(
        string? model,
        IReadOnlyList<CodexInputItem> input,
        Func<CodexTurnDelta, CancellationToken, Task>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenInitializedAsync(cancellationToken);
        string? threadId = null;
        string? turnId = null;
        try
        {
            var threadResult = CodexAppServerProtocol.Result(await connection.RequestAsync("thread/start", new
            {
                cwd = _options.WorkingDirectory,
                approvalPolicy = "never",
                sandbox = "read-only",
                ephemeral = true,
                serviceName = "virtua-agent",
                model
            }, cancellationToken));
            threadId = threadResult.GetProperty("thread").GetProperty("id").GetString();

            var turnResult = CodexAppServerProtocol.Result(await connection.RequestAsync("turn/start", new
            {
                threadId,
                input,
                sandboxPolicy = new { type = "readOnly", networkAccess = false }
            }, cancellationToken));
            turnId = turnResult.GetProperty("turn").GetProperty("id").GetString();

            var content = "";
            int? inputTokens = null;
            int? outputTokens = null;
            while (true)
            {
                var message = await connection.ReadAsync(cancellationToken);
                var method = CodexAppServerProtocol.Method(message);
                if (method is "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta")
                {
                    var delta = CodexAppServerProtocol.Delta(message);
                    if (!string.IsNullOrEmpty(delta) && onDelta is not null)
                        await onDelta(new CodexTurnDelta(Reasoning: delta), cancellationToken);
                }
                else if (method == "item/agentMessage/delta")
                {
                    var delta = CodexAppServerProtocol.Delta(message);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        content += delta;
                        if (onDelta is not null) await onDelta(new CodexTurnDelta(Content: delta), cancellationToken);
                    }
                }
                else if (method == "item/completed")
                {
                    content = CodexAppServerProtocol.CompletedMessage(message) ?? content;
                }
                else if (method == "thread/tokenUsage/updated")
                {
                    (inputTokens, outputTokens) = CodexAppServerProtocol.TokenUsage(message);
                }
                else if (method == "turn/completed")
                {
                    var status = CodexAppServerProtocol.TurnStatus(message);
                    if (status == "failed")
                        throw new InvalidOperationException(CodexAppServerProtocol.TurnError(message) ?? "Codex turn failed.");
                    if (status == "interrupted") throw new OperationCanceledException(cancellationToken);
                    break;
                }
            }

            return new CodexTurnResult
            {
                Id = turnId ?? "",
                Model = string.IsNullOrWhiteSpace(model) ? "codex" : model,
                Content = content,
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && threadId is not null && turnId is not null)
        {
            await InterruptAsync(connection, threadId, turnId);
            throw;
        }
    }

    private async Task<CodexConnection> OpenInitializedAsync(CancellationToken cancellationToken)
    {
        CodexConnection connection;
        try
        {
            connection = await CodexConnection.OpenAsync(_options, cancellationToken);
            _ = CodexAppServerProtocol.Result(await connection.RequestAsync("initialize", new
            {
                clientInfo = new { name = "virtua-agent", title = "Virtua Agent", version = "1" },
                capabilities = new { experimentalApi = false }
            }, cancellationToken));
            await connection.NotifyAsync("initialized", cancellationToken);
            var account = CodexAppServerProtocol.Result(await connection.RequestAsync("account/read", new { refreshToken = true }, cancellationToken));
            if (!account.TryGetProperty("account", out var value) ||
                value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("type", out var type) ||
                type.GetString() != "chatgpt")
            {
                await connection.DisposeAsync();
                throw new InvalidOperationException("Codex subscription is not authenticated. Run 'codex login --device-auth' in the Codex sidecar.");
            }

            return connection;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Codex sidecar is unavailable.");
        }
    }

    private async Task InterruptAsync(CodexConnection connection, string threadId, string turnId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.InterruptTimeoutSeconds)));
        try
        {
            var requestId = await connection.WriteRequestAsync("turn/interrupt", new { threadId, turnId }, timeout.Token);
            var interrupted = false;
            var acknowledged = false;
            while (!interrupted || !acknowledged)
            {
                var message = await connection.ReadAsync(timeout.Token);
                acknowledged |= CodexAppServerProtocol.ResponseId(message) == requestId;
                interrupted |= CodexAppServerProtocol.Method(message) == "turn/completed" &&
                    CodexAppServerProtocol.TurnStatus(message) == "interrupted";
            }
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
        }
    }

    private sealed class CodexConnection : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private int _nextId;

        private CodexConnection(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
            _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        }

        public static async Task<CodexConnection> OpenAsync(CodexOptions options, CancellationToken cancellationToken)
        {
            if (!Path.IsPathRooted(options.SocketPath)) throw new InvalidOperationException("Codex socket path must be absolute.");
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ConnectTimeoutSeconds)));
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), timeout.Token);
                return new CodexConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            var id = await WriteRequestAsync(method, parameters, cancellationToken);
            while (true)
            {
                var message = await ReadAsync(cancellationToken);
                if (CodexAppServerProtocol.ResponseId(message) == id) return message;
            }
        }

        public async Task<int> WriteRequestAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            var id = ++_nextId;
            await WriteAsync(new { method, id, @params = parameters }, cancellationToken);
            return id;
        }

        public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
            WriteAsync(new { method }, cancellationToken);

        public async Task<JsonElement> ReadAsync(CancellationToken cancellationToken)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line is null) throw new EndOfStreamException();
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        private Task WriteAsync(object message, CancellationToken cancellationToken) =>
            _writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions.Default).AsMemory(), cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            _reader.Dispose();
            await _stream.DisposeAsync();
            _socket.Dispose();
        }
    }
}
