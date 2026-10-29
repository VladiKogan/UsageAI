namespace UsageAI.UI;

/// <summary>
/// The UsageAI logo for window title bars and the taskbar. A window that sets no icon shows
/// WinForms' generic placeholder, which read as a different, unbranded program. The multi-size
/// icon is embedded rather than read from beside the executable, which a single-file or moved
/// copy may not have, and is loaded once and shared, so no window owns or disposes it.
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "UsageAI.Resources.app.ico";

    private static readonly Lazy<Icon?> Shared = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The logo, or null if it cannot be read, in which case the window keeps the default.</summary>
    public static Icon? Window => Shared.Value;

    /// <summary>Gives <paramref name="form"/> the logo in its title bar and taskbar button.</summary>
    public static void Apply(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (Window is { } icon)
        {
            form.Icon = icon;
            form.ShowIcon = true;
        }
    }

    private static Icon? Load()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            return stream is null ? null : new Icon(stream);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
