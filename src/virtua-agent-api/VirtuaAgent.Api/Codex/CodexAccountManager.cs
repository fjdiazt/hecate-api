using System.Text.Json;
using Microsoft.Extensions.Options;

namespace VirtuaAgent.Codex;

public sealed class CodexAccountManager(IOptions<CodexOptions> options) : IAsyncDisposable
{
    private readonly CodexOptions _options = options.Value;
    private readonly object _gate = new();
    private LoginAttempt? _attempt;
    private CodexAccountState? _lastError;
    private bool _disposed;

    public async Task<CodexAccountState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_attempt is not null) return _attempt.State;
            if (_lastError is not null) return _lastError;
        }

        return await ReadAccountFreshAsync(cancellationToken);
    }

    public async Task<CodexAccountState> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        Task<CodexAccountState> started;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_attempt is null)
            {
                _lastError = null;
                var attempt = new LoginAttempt(_options.LoginTimeoutSeconds);
                _attempt = attempt;
                attempt.Lifetime = RunLoginAsync(attempt);
            }

            started = _attempt.Started.Task;
        }

        return await started.WaitAsync(cancellationToken);
    }

    public async Task<CodexAccountState> CancelLoginAsync(CancellationToken cancellationToken = default)
    {
        LoginAttempt? attempt;
        lock (_gate)
        {
            ThrowIfDisposed();
            attempt = _attempt;
            attempt?.RequestCancel();
        }

        return attempt is null
            ? Disconnected()
            : await attempt.Completed.Task.WaitAsync(cancellationToken);
    }

    public async Task<CodexAccountState> LogoutAsync(CancellationToken cancellationToken = default)
    {
        LoginAttempt? attempt;
        lock (_gate)
        {
            ThrowIfDisposed();
            attempt = _attempt;
            attempt?.RequestCancel();
        }

        if (attempt is not null) await attempt.Completed.Task.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await CodexAppServerConnection.OpenInitializedAsync(_options, cancellationToken);
            _ = CodexAppServerProtocol.Result(await connection.RequestAsync("account/logout", new { }, cancellationToken));
            lock (_gate) _lastError = null;
            return Disconnected();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await ReadAccountFreshAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        LoginAttempt? attempt;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            attempt = _attempt;
            attempt?.RequestCancel();
        }

        if (attempt is not null) await attempt.Completed.Task;
    }

    private async Task RunLoginAsync(LoginAttempt attempt)
    {
        CodexAppServerConnection? connection = null;
        var finalState = Error("Codex login failed.");
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            attempt.Deadline.Token,
            attempt.StartupCancellation.Token);
        try
        {
            connection = await CodexAppServerConnection.OpenInitializedAsync(_options, startupCancellation.Token);
            var account = await ReadAccountAsync(connection, startupCancellation.Token);
            if (account.Status == CodexAccountStatuses.Connected)
            {
                finalState = account;
                attempt.Started.TrySetResult(account);
                return;
            }

            var response = CodexAppServerProtocol.Result(await connection.RequestAsync(
                "account/login/start",
                new { type = "chatgptDeviceCode" },
                startupCancellation.Token));
            var login = ParseLogin(response);
            if (login is null)
            {
                finalState = Error("Codex returned invalid device login data.");
                attempt.Started.TrySetResult(finalState);
                return;
            }

            var connecting = new CodexAccountState(
                CodexAccountStatuses.Connecting,
                VerificationUrl: login.VerificationUrl,
                UserCode: login.UserCode);
            SetAttemptState(attempt, connecting);
            attempt.Started.TrySetResult(connecting);
            finalState = await MonitorLoginAsync(attempt, connection, login.LoginId);
        }
        catch
        {
            finalState = attempt.Deadline.IsCancellationRequested
                ? Error("Codex login timed out.")
                : attempt.Cancel.Task.IsCompleted
                    ? Disconnected()
                    : connection is null
                        ? Unavailable()
                        : Error("Codex login failed.");
            attempt.Started.TrySetResult(finalState);
        }
        finally
        {
            SetAttemptState(attempt, finalState);
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                }
            }

            FinishAttempt(attempt, finalState);
        }
    }

    private async Task<CodexAccountState> MonitorLoginAsync(
        LoginAttempt attempt,
        CodexAppServerConnection connection,
        string loginId)
    {
        var timeoutTask = Task.Delay(Timeout.Infinite, attempt.Deadline.Token);
        Task<JsonElement>? pendingRead = connection.ReadAsync(CancellationToken.None);
        while (true)
        {
            var winner = await Task.WhenAny(pendingRead, attempt.Cancel.Task, timeoutTask);
            if (winner == pendingRead)
            {
                var message = await pendingRead;
                pendingRead = null;
                if (IsMatchingLoginCompletion(message, loginId, out var success))
                {
                    return success
                        ? await ReadAccountAsync(connection, attempt.Deadline.Token)
                        : Error("Codex login failed.");
                }

                pendingRead = connection.ReadAsync(CancellationToken.None);
                continue;
            }

            var timedOut = attempt.Deadline.IsCancellationRequested;
            await CancelCodexLoginAsync(connection, loginId, pendingRead);
            return timedOut
                ? Error("Codex login timed out.")
                : Disconnected();
        }
    }

    private async Task CancelCodexLoginAsync(
        CodexAppServerConnection connection,
        string loginId,
        Task<JsonElement>? pendingRead)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _options.InterruptTimeoutSeconds)));
        try
        {
            var requestId = await connection.WriteRequestAsync(
                "account/login/cancel",
                new { loginId },
                timeout.Token);
            while (true)
            {
                var message = pendingRead is null
                    ? await connection.ReadAsync(timeout.Token)
                    : await pendingRead.WaitAsync(timeout.Token);
                pendingRead = null;
                if (CodexAppServerProtocol.ResponseId(message) == requestId) return;
            }
        }
        catch
        {
        }
    }

    private async Task<CodexAccountState> ReadAccountFreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await CodexAppServerConnection.OpenInitializedAsync(_options, cancellationToken);
            return await ReadAccountAsync(connection, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }
    }

    private static async Task<CodexAccountState> ReadAccountAsync(
        CodexAppServerConnection connection,
        CancellationToken cancellationToken)
    {
        var result = CodexAppServerProtocol.Result(await connection.RequestAsync(
            "account/read",
            new { refreshToken = true },
            cancellationToken));
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
            return Disconnected();
        if (!account.TryGetProperty("type", out var type) || type.GetString() != "chatgpt")
            return Disconnected();

        return new CodexAccountState(
            CodexAccountStatuses.Connected,
            Email: ReadString(account, "email"),
            PlanType: ReadString(account, "planType"));
    }

    private static LoginStart? ParseLogin(JsonElement result)
    {
        var loginId = ReadString(result, "loginId");
        var verificationUrl = ReadString(result, "verificationUrl");
        var userCode = ReadString(result, "userCode");
        if (string.IsNullOrWhiteSpace(loginId) ||
            string.IsNullOrWhiteSpace(userCode) ||
            !Uri.TryCreate(verificationUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return new LoginStart(loginId, verificationUrl!, userCode);
    }

    private static bool IsMatchingLoginCompletion(JsonElement message, string loginId, out bool success)
    {
        success = false;
        if (CodexAppServerProtocol.Method(message) != "account/login/completed" ||
            !message.TryGetProperty("params", out var parameters) ||
            ReadString(parameters, "loginId") != loginId)
        {
            return false;
        }

        success = parameters.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True;
        return true;
    }

    private void SetAttemptState(LoginAttempt attempt, CodexAccountState state)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_attempt, attempt)) attempt.State = state;
        }
    }

    private void FinishAttempt(LoginAttempt attempt, CodexAccountState state)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_attempt, attempt))
            {
                _lastError = state.Status == CodexAccountStatuses.Error ? state : null;
                _attempt = null;
            }
        }

        attempt.Started.TrySetResult(state);
        attempt.Completed.TrySetResult(state);
        attempt.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CodexAccountManager));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static CodexAccountState Disconnected() => new(CodexAccountStatuses.Disconnected);

    private static CodexAccountState Unavailable() =>
        new(CodexAccountStatuses.Unavailable, Error: "Codex sidecar is unavailable.");

    private static CodexAccountState Error(string message) =>
        new(CodexAccountStatuses.Error, Error: message);

    private sealed record LoginStart(string LoginId, string VerificationUrl, string UserCode);

    private sealed class LoginAttempt : IDisposable
    {
        public LoginAttempt(int timeoutSeconds)
        {
            Deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        }

        public CodexAccountState State { get; set; } = new(CodexAccountStatuses.Connecting);
        public TaskCompletionSource<CodexAccountState> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CodexAccountState> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancel { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Deadline { get; } = new();
        public CancellationTokenSource StartupCancellation { get; } = new();
        public Task Lifetime { get; set; } = Task.CompletedTask;

        public void RequestCancel()
        {
            Cancel.TrySetResult();
            StartupCancellation.Cancel();
        }

        public void Dispose()
        {
            Deadline.Dispose();
            StartupCancellation.Dispose();
        }
    }
}
