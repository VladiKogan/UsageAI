using System.Text.Json;

namespace UsageAI.Models;

internal sealed record UsageWindow(
    string Name,
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? DurationMinutes,
    string? RemainingText = null,
    string? UsageText = null)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);

    public string DisplayRemaining => RemainingText ?? $"{RemainingPercent}% LEFT";

    public string DisplayUsage => UsageText ?? $"{UsedPercent}% used";
}

internal sealed record UsageSnapshot(
    string Plan,
    UsageWindow? Session,
    UsageWindow? Weekly,
    string? CreditBalance,
    int AvailableResetCredits,
    DateTimeOffset FetchedAt,
    string ProviderId = "codex",
    string ProviderName = "Codex",
    string? AccountName = null)
{
    public int HighestUsedPercent => Math.Max(Session?.UsedPercent ?? 0, Weekly?.UsedPercent ?? 0);

    public string ToDiagnosticJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
    });
}
