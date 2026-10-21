namespace UsageAI.Models;

/// <summary>
/// Which side of a metered quota the meters and headlines present. Only presentation flips:
/// warning colours, alerts, forecasts, and selection keep working from the used percentage.
/// </summary>
internal enum QuotaDisplay
{
    Used,
    Remaining,
}
