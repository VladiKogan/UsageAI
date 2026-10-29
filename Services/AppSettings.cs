using System.Text.Json;
using System.Text.Json.Serialization;
using UsageAI.Models;

namespace UsageAI.Services;

internal enum ThemeMode
{
    System,
    Dark,
    Light,
}

/// <summary>Local paths for the small amount of state UsageAI keeps between runs.</summary>
internal static class AppPaths
{
    /// <summary>
    /// Local state lives under LocalAppData unless USAGEAI_DATA_DIR points elsewhere, which
    /// allows a portable install and lets the tests run without touching a real profile.
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    public static string SnapshotCacheFile => Path.Combine(DataDirectory, "last-snapshot.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static void EnsureDirectory() => Directory.CreateDirectory(DataDirectory);

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("USAGEAI_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim().Trim('"');
            if (Path.IsPathFullyQualified(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageAI");
    }

    /// <summary>
    /// Writes non-credential state so a crash mid-write cannot leave a truncated file behind.
    /// Credential files use the hardened path in <see cref="ClaudeCodeUsageClient"/> instead.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        EnsureDirectory();
        var temporaryPath = $"{path}.tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

/// <summary>
/// User-controlled preferences, stored as plain JSON next to the other local state.
/// Every value is validated on load so a hand-edited file cannot put the UI in a bad state.
/// </summary>
internal sealed class AppSettings
{
    public const int MinimumRefreshMinutes = 1;
    public const int MaximumRefreshMinutes = 120;

    private static readonly int[] DefaultThresholds = { 80, 95 };

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public int RefreshIntervalMinutes { get; set; } = 5;

    /// <summary>Multiplies the refresh interval while no window is open, to spare battery.</summary>
    public bool SlowRefreshWhenHidden { get; set; } = true;

    public bool NotificationsEnabled { get; set; } = true;

    public int[] NotifyAtPercent { get; set; } = { 80, 95 };

    /// <summary>Kept for the settings dialog, which shows exactly two alert thresholds.</summary>
    public int AlertThreshold(int index, int fallback) =>
        NotifyAtPercent is { Length: > 0 } thresholds && index < thresholds.Length
            ? thresholds[index]
            : fallback;

    public bool NotifyOnReset { get; set; } = true;

    public int WarningPercent { get; set; } = 72;

    public int CriticalPercent { get; set; } = 90;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public MetricDisplayMode MetricDisplayMode { get; set; } = MetricDisplayMode.All;

    /// <summary>Whether meters and headlines show quota used or quota remaining.</summary>
    public QuotaDisplay QuotaDisplay { get; set; } = QuotaDisplay.Used;

    public bool HistoryEnabled { get; set; } = true;

    public bool ForecastEnabled { get; set; } = true;

    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Last GitHub release check, used to keep the automatic request to once per day.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>Installed version whose bundled release notes were last acknowledged.</summary>
    public string? LastRunVersion { get; set; }

    public string[] HiddenProviders { get; set; } = Array.Empty<string>();

    /// <summary>Provider ids in display order; unknown or missing ids fall back to registration order.</summary>
    public string[] ProviderOrder { get; set; } = Array.Empty<string>();

    /// <summary>
    /// <see cref="TrayProviderId"/> value that lists every connected provider in the tray tooltip.
    /// No single gauge stands for them all, so the tray then shows the UsageAI logo.
    /// </summary>
    public const string AllProvidersTrayId = "all";

    /// <summary>
    /// Provider whose usage drives the tray gauge. Null means follow the connected provider
    /// with the highest consumption; <see cref="AllProvidersTrayId"/> lists them all.
    /// </summary>
    public string? TrayProviderId { get; set; }

    /// <summary>Saved dashboard bounds as x, y, width, height.</summary>
    public int[]? DashboardBounds { get; set; }

    /// <summary>
    /// Whether the saved bounds are a size the user chose. Until they are, the dashboard grows
    /// to fit its table; bounds saved only because the window was hidden do not count.
    /// </summary>
    public bool DashboardSizedByUser { get; set; }

    /// <summary>
    /// The Settings window's client size as width, height at 96 DPI, so a size chosen on one
    /// monitor opens the same on another at a different scale. Null opens the default size.
    /// </summary>
    public int[]? SettingsWindowSize { get; set; }

    /// <summary>
    /// The model group each provider's popup rings show, keyed by provider id. A provider with no
    /// entry, or an entry naming a group it no longer reports, shows its busiest group.
    /// </summary>
    public Dictionary<string, string> PopupMetricGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? PopupMetricGroup(string providerId) =>
        PopupMetricGroups.TryGetValue(providerId, out var group) ? group : null;

    /// <summary>Pins a provider's popup rings to <paramref name="group"/>; null returns to automatic.</summary>
    public void SetPopupMetricGroup(string providerId, string? group)
    {
        var groups = new Dictionary<string, string>(PopupMetricGroups, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(group))
        {
            groups.Remove(providerId);
        }
        else
        {
            groups[providerId] = group.Trim();
        }

        PopupMetricGroups = groups;
    }

    /// <summary>
    /// Keeps these settings in memory only. The interactive preview uses throwaway settings
    /// and must never overwrite the user's real file, whatever the window it opens saves.
    /// </summary>
    [JsonIgnore]
    public bool IsEphemeral { get; init; }

    public static AppSettings Load()
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = SecureLocalFile.ReadAllText(path, maxCharacters: 65_536);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, FileOptions) ?? new AppSettings();
            settings.Validate();
            return settings;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or JsonException
                                              or NotSupportedException)
        {
            // Preferences are a convenience; an unreadable file must never stop the app starting.
            return new AppSettings();
        }
    }

    public void Save()
    {
        if (IsEphemeral)
        {
            Validate();
            return;
        }

        try
        {
            Validate();
            AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(this, FileOptions));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            // Losing a preference change is preferable to interrupting the user with an error.
        }
    }

    public bool IsProviderVisible(string providerId) =>
        !HiddenProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase);

    public void SetProviderVisible(string providerId, bool visible)
    {
        var hidden = HiddenProviders
            .Where(id => !id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!visible)
        {
            hidden.Add(providerId);
        }

        HiddenProviders = hidden.ToArray();
    }

    /// <summary>Orders provider ids by the saved preference, keeping unknown ids in registration order.</summary>
    public IReadOnlyList<string> OrderProviders(IReadOnlyList<string> providerIds)
    {
        var ordered = ProviderOrder
            .Where(id => providerIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ordered.AddRange(providerIds.Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));
        return ordered;
    }

    public TimeSpan EffectiveRefreshInterval(bool anyWindowVisible)
    {
        var minutes = Math.Clamp(RefreshIntervalMinutes, MinimumRefreshMinutes, MaximumRefreshMinutes);
        if (!anyWindowVisible && SlowRefreshWhenHidden)
        {
            minutes = Math.Min(MaximumRefreshMinutes, minutes * 3);
        }

        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// Clamps everything a hand-edited or partially written file could get wrong. Arrays are
    /// pattern-matched rather than null-coalesced because deserialization can still hand back
    /// null for a property the JSON set to null.
    /// </summary>
    private void Validate()
    {
        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes, MinimumRefreshMinutes, MaximumRefreshMinutes);
        WarningPercent = Math.Clamp(WarningPercent, 1, 99);
        CriticalPercent = Math.Clamp(CriticalPercent, WarningPercent + 1, 100);
        if (!Enum.IsDefined(MetricDisplayMode))
        {
            MetricDisplayMode = MetricDisplayMode.All;
        }

        if (!Enum.IsDefined(QuotaDisplay))
        {
            QuotaDisplay = QuotaDisplay.Used;
        }

        NotifyAtPercent = NotifyAtPercent is { Length: > 0 } thresholds
            ? thresholds
                .Where(percent => percent is > 0 and <= 100)
                .Distinct()
                .Order()
                .Take(4)
                .ToArray()
            : DefaultThresholds;
        HiddenProviders = NormalizeIds(HiddenProviders);
        ProviderOrder = NormalizeIds(ProviderOrder);
        TrayProviderId = NormalizeId(TrayProviderId);
        if (LastUpdateCheckUtc is { } lastUpdateCheck)
        {
            var utc = lastUpdateCheck.ToUniversalTime();
            LastUpdateCheckUtc = utc > DateTimeOffset.UtcNow.AddMinutes(5)
                ? null
                : utc;
        }

        if (LastRunVersion is not null)
        {
            var normalizedVersion = LastRunVersion.Trim();
            LastRunVersion = normalizedVersion.Length <= 32 ? normalizedVersion : string.Empty;
        }

        if (DashboardBounds is { Length: not 4 })
        {
            DashboardBounds = null;
        }

        // Within any plausible screen; the window keeps itself on the screen it opens on.
        if (SettingsWindowSize is not [>= 200 and <= 8000, >= 200 and <= 8000])
        {
            SettingsWindowSize = null;
        }

        // Deserialisation hands back a case-sensitive dictionary, or null for an explicit null.
        PopupMetricGroups = (PopupMetricGroups ?? new Dictionary<string, string>())
            .Where(entry => NormalizeId(entry.Key) is not null &&
                            !string.IsNullOrWhiteSpace(entry.Value) &&
                            entry.Value.Trim().Length <= 128)
            .Take(16)
            .GroupBy(entry => entry.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string[] NormalizeIds(string[]? ids) => ids is { Length: > 0 }
        ? ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray()
        : Array.Empty<string>();

    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var normalized = id.Trim();
        return normalized.Length <= 64 ? normalized : null;
    }
}
