using UsageAI.Models;

namespace UsageAI.UI;

/// <summary>What a click or key on a provider's tile or dashboard row asks the window to do.</summary>
internal enum ProviderCardAction
{
    None,

    /// <summary>Copy the provider's sign-in command to the clipboard.</summary>
    CopyCommand,

    /// <summary>Open the provider's usage page in the browser.</summary>
    OpenAccount,

    /// <summary>A popup tile was chosen: open the dashboard for the full picture.</summary>
    OpenDetails,

    /// <summary>Retry a provider whose last reading failed.</summary>
    Refresh,
}

internal sealed class ProviderCardActionEventArgs : EventArgs
{
    public ProviderCardActionEventArgs(ProviderStatus status, ProviderCardAction action)
    {
        Status = status;
        Action = action;
    }

    public ProviderStatus Status { get; }

    public ProviderCardAction Action { get; }
}
