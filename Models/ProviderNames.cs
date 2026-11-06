namespace UsageAI.Models;

/// <summary>
/// The short provider names the tray tooltip, alert titles, account banners and the editor
/// Status Bar share, so "Claude Code" reads as Claude everywhere.
/// </summary>
internal static class ProviderNames
{
    /// <summary>The short name of a provider or account key, else <paramref name="fallback"/>.</summary>
    public static string Short(string providerId, string? fallback = null) =>
        ProviderKey.ProviderOf(providerId).ToLowerInvariant() switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            "gemini" => "Gemini",
            _ => fallback ?? providerId,
        };

    /// <summary>
    /// The name the compact popup shows, decided by the provider rather than by its display text.
    /// Claude Code reads as Claude, so an account's tile, "Claude · Personal", fits half the popup's
    /// width instead of ending in an ellipsis. The <paramref name="headline"/> sentence also
    /// shortens GitHub Copilot to Copilot; every other provider keeps its own name. The account's
    /// label follows, as it does everywhere.
    /// </summary>
    public static string Popup(ProviderStatus status, bool headline = false)
    {
        var provider = status.Kind.ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "copilot" when headline => "Copilot",
            _ => null,
        };
        if (provider is null)
        {
            return status.ProviderName;
        }

        return status.AccountLabel is { } label ? $"{provider} · {label}" : provider;
    }
}
