using UsageAI.Models;

namespace UsageAI.UI;

internal enum DashboardMode
{
    Compact,
    Full,
}

internal sealed record ProviderViewState(
    string ProviderId,
    string ProviderName,
    UsageSnapshot? Snapshot,
    string? Error,
    bool IsLoading)
{
    public bool IsConnected => Snapshot is not null;
}
