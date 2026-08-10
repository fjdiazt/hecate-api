namespace VirtuaAgent.Codex;

public sealed record CodexOptions
{
    public string SocketPath { get; init; } = "/run/codex/app-server.sock";
    public string WorkingDirectory { get; init; } = "/work";
    public int ConnectTimeoutSeconds { get; init; } = 5;
    public int InterruptTimeoutSeconds { get; init; } = 5;
}
