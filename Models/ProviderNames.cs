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
}
