using System.Text.Json;
using Microsoft.Extensions.Options;
using VirtuaAgent.OpenAi;

namespace VirtuaAgent.Codex;

public sealed class CodexAppServerClient(IOptions<CodexOptions> options) : ICodexAppServerClient
{
    private readonly CodexOptions _options = options.Value;

    public async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAuthenticatedAsync(cancellationToken);
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
        await using var connection = await OpenAuthenticatedAsync(cancellationToken);
        string? threadId = null;
        string? turnId = null;
        Task<JsonElement>? pendingRead = null;
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
                pendingRead = connection.ReadAsync(CancellationToken.None);
                var message = await pendingRead.WaitAsync(cancellationToken);
                pendingRead = null;
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
            await InterruptAsync(connection, threadId, turnId, pendingRead);
            throw;
        }
    }

    private async Task<CodexAppServerConnection> OpenAuthenticatedAsync(CancellationToken cancellationToken)
    {
        CodexAppServerConnection? connection = null;
        try
        {
            connection = await CodexAppServerConnection.OpenInitializedAsync(_options, cancellationToken);
            var account = CodexAppServerProtocol.Result(await connection.RequestAsync("account/read", new { refreshToken = true }, cancellationToken));
            if (!account.TryGetProperty("account", out var value) ||
                value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("type", out var type) ||
                type.GetString() != "chatgpt")
            {
                throw new InvalidOperationException("Codex subscription is not authenticated. Connect ChatGPT in Settings.");
            }

            return connection;
        }
        catch (Exception ex)
        {
            if (connection is not null) await connection.DisposeAsync();
            if (ex is InvalidOperationException ||
                ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException("Codex sidecar is unavailable.");
        }
    }

    private async Task InterruptAsync(
        CodexAppServerConnection connection,
        string threadId,
        string turnId,
        Task<JsonElement>? pendingRead)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.InterruptTimeoutSeconds)));
        try
        {
            var requestId = await connection.WriteRequestAsync("turn/interrupt", new { threadId, turnId }, timeout.Token);
            var interrupted = false;
            var acknowledged = false;
            while (!interrupted || !acknowledged)
            {
                var message = pendingRead is null
                    ? await connection.ReadAsync(timeout.Token)
                    : await pendingRead.WaitAsync(timeout.Token);
                pendingRead = null;
                acknowledged |= CodexAppServerProtocol.ResponseId(message) == requestId;
                interrupted |= CodexAppServerProtocol.Method(message) == "turn/completed" &&
                    CodexAppServerProtocol.TurnStatus(message) == "interrupted";
            }
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
        }
    }

}
