using VirtuaAgent.Codex;
using VirtuaAgent.OpenAi;

namespace VirtuaAgent.Endpoints;

public sealed record StartCodexLoginRequest;

public static class CodexAccountEndpoint
{
    public static async Task<IResult> GetAsync(
        CodexAccountManager manager,
        CancellationToken cancellationToken) =>
        Results.Json(await manager.GetStateAsync(cancellationToken), JsonOptions.Default);

    public static async Task<IResult> StartLoginAsync(
        StartCodexLoginRequest _,
        CodexAccountManager manager,
        CancellationToken cancellationToken) =>
        Command(await manager.StartLoginAsync(cancellationToken));

    public static async Task<IResult> CancelLoginAsync(
        CodexAccountManager manager,
        CancellationToken cancellationToken) =>
        Command(await manager.CancelLoginAsync(cancellationToken));

    public static async Task<IResult> LogoutAsync(
        CodexAccountManager manager,
        CancellationToken cancellationToken) =>
        Command(await manager.LogoutAsync(cancellationToken));

    private static IResult Command(CodexAccountState state) =>
        Results.Json(
            state,
            JsonOptions.Default,
            statusCode: state.Status == CodexAccountStatuses.Unavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK);
}
