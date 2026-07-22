using System.Text.Json;

namespace UsageAI.Models;

internal sealed record UsageWindow(
    string Name,
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? DurationMinutes)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record UsageSnapshot(
    string Plan,
    UsageWindow? Session,
    UsageWindow? Weekly,
    string? CreditBalance,
    int AvailableResetCredits,
    DateTimeOffset FetchedAt)
{
    public int HighestUsedPercent => Math.Max(Session?.UsedPercent ?? 0, Weekly?.UsedPercent ?? 0);

    public string ToDiagnosticJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
    });
}
