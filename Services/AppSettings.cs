using System.Text.Json;
using System.Text.Json.Serialization;

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

    public bool HistoryEnabled { get; set; } = true;

    public bool ForecastEnabled { get; set; } = true;

    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Last GitHub release check, used to keep the automatic request to once per day.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public string[] HiddenProviders { get; set; } = Array.Empty<string>();

    /// <summary>Provider ids in display order; unknown or missing ids fall back to registration order.</summary>
    public string[] ProviderOrder { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Provider whose usage drives the tray gauge. Null means follow the connected provider
    /// with the highest consumption.
    /// </summary>
    public string? TrayProviderId { get; set; }

    /// <summary>Saved dashboard bounds as x, y, width, height.</summary>
    public int[]? DashboardBounds { get; set; }

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

        if (DashboardBounds is { Length: not 4 })
        {
            DashboardBounds = null;
        }
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
