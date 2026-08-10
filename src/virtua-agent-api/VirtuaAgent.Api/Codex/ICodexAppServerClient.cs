namespace VirtuaAgent.Codex;

public sealed record CodexInputItem(string Type, string? Text = null, string? Url = null);

public sealed record CodexTurnDelta(string? Content = null, string? Reasoning = null);

public sealed record CodexModel(string Id);

public sealed record CodexTurnResult
{
    public string Id { get; init; } = "";
    public string Model { get; init; } = "";
    public string Content { get; init; } = "";
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

public interface ICodexAppServerClient
{
    Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<CodexTurnResult> RunTurnAsync(
        string? model,
        IReadOnlyList<CodexInputItem> input,
        Func<CodexTurnDelta, CancellationToken, Task>? onDelta = null,
        CancellationToken cancellationToken = default);
}
