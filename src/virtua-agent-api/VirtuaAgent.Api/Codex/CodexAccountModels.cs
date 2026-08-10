using System.Text.Json.Serialization;

namespace VirtuaAgent.Codex;

public static class CodexAccountStatuses
{
    public const string Unavailable = "unavailable";
    public const string Disconnected = "disconnected";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Error = "error";
}

public sealed record CodexAccountState(
    string Status,
    string? Email = null,
    [property: JsonPropertyName("plan_type")] string? PlanType = null,
    [property: JsonPropertyName("verification_url")] string? VerificationUrl = null,
    [property: JsonPropertyName("user_code")] string? UserCode = null,
    string? Error = null);
