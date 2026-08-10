using System.Text.Json;

namespace VirtuaAgent.Codex;

internal static class CodexAppServerProtocol
{
    public static int? ResponseId(JsonElement message) =>
        message.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ? value : null;

    public static string? Method(JsonElement message) =>
        message.TryGetProperty("method", out var method) ? method.GetString() : null;

    public static JsonElement Result(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var value) ? value.GetString() : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Codex request failed." : message);
        }

        return response.GetProperty("result");
    }

    public static string? Delta(JsonElement message) =>
        message.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("delta", out var delta)
            ? delta.GetString()
            : null;

    public static string? CompletedMessage(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("item", out var item) ||
            !item.TryGetProperty("type", out var type) ||
            type.GetString() != "agentMessage")
        {
            return null;
        }

        return item.TryGetProperty("text", out var text) ? text.GetString() : null;
    }

    public static string? TurnStatus(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("turn", out var turn) ||
            !turn.TryGetProperty("status", out var status))
        {
            return null;
        }

        return status.GetString();
    }

    public static string? TurnError(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("turn", out var turn) ||
            !turn.TryGetProperty("error", out var error) ||
            !error.TryGetProperty("message", out var errorMessage))
        {
            return null;
        }

        return errorMessage.GetString();
    }

    public static (int? Input, int? Output) TokenUsage(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters)) return (null, null);
        var usage = parameters.TryGetProperty("tokenUsage", out var direct)
            ? direct
            : parameters.TryGetProperty("usage", out var fallback) ? fallback : default;
        if (usage.ValueKind != JsonValueKind.Object) return (null, null);

        return (ReadInt(usage, "inputTokens"), ReadInt(usage, "outputTokens"));
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
