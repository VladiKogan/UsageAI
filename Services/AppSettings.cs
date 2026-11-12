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

    /// <summary>Each metric's last reading, so usage alerts survive a restart.</summary>
    public static string AlertStateFile => Path.Combine(DataDirectory, "alert-state.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    /// <summary>The login folders UsageAI created, one per line, for the uninstaller's offer to delete them.</summary>
    public static string CreatedLoginsFile => Path.Combine(DataDirectory, "created-logins.txt");

    /// <summary>The note an in-app update leaves for the copy that starts after it.</summary>
    public static string UpdateHandoffFile => Path.Combine(DataDirectory, "update-handoff.json");

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

    /// <summary>
    /// Reads the providers' public status pages and marks an ongoing incident on the tray icon,
    /// its tooltip and the dashboard. Only the published status JSON is fetched; no credential.
    /// </summary>
    public bool ShowProviderIncidents { get; set; } = true;

    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Last GitHub release check, used to keep the automatic request to once per day.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>Installed version whose bundled release notes were last acknowledged.</summary>
    public string? LastRunVersion { get; set; }

    public string[] HiddenProviders { get; set; } = Array.Empty<string>();

    /// <summary>Provider ids in display order; unknown or missing ids fall back to registration order.</summary>
    public string[] ProviderOrder { get; set; } = Array.Empty<string>();

    /// <summary>
    /// <see cref="TrayProviderId"/> value that lists every connected provider in the tray tooltip,
    /// with the logo's tank filled to the busiest limit among them.
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
    /// Every login UsageAI shows besides — or as well as — each provider's default one, with the
    /// user's choices for it. The default login only gets an entry once something about it is
    /// remembered (who is signed in, a name, hidden, alerts off, its position).
    /// </summary>
    public List<ProviderAccount> Accounts { get; set; } = new();

    /// <summary>
    /// False until the first account scan ran. That scan shows every login it finds, since they
    /// are the user's own; later scans hold a new one back until the user answers its banner.
    /// </summary>
    public bool AccountsInitialized { get; set; }

    /// <summary>Lists <c>%USERPROFILE%\.claude…</c> / <c>.codex…</c> at startup and once a day.</summary>
    public bool FindAccountsAutomatically { get; set; } = true;

    /// <summary>A login found after the first scan waits for Show or Ignore before it is shown.</summary>
    public bool AskBeforeShowingNewAccounts { get; set; } = true;

    /// <summary>
    /// Account ids of found folders the user forgot, so the next scan does not offer them again.
    /// Adding the folder by hand clears its entry.
    /// </summary>
    public string[] ForgottenAccounts { get; set; } = Array.Empty<string>();

    /// <summary>How many forgotten folders are remembered; the oldest go first.</summary>
    public const int MaxForgottenAccounts = 64;

    /// <summary>The last account scan, kept apart from the update check so neither skips the other.</summary>
    public DateTimeOffset? LastAccountScanUtc { get; set; }

    public ProviderAccount? FindAccount(string unitKey)
    {
        var providerId = ProviderKey.ProviderOf(unitKey);
        var accountId = ProviderKey.AccountOf(unitKey);
        return Accounts.FirstOrDefault(account =>
            account.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
            account.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The account entry for a key, created (and kept) when it does not exist yet.</summary>
    public ProviderAccount EnsureAccount(string unitKey)
    {
        if (FindAccount(unitKey) is { } existing)
        {
            return existing;
        }

        var account = new ProviderAccount
        {
            Id = ProviderKey.AccountOf(unitKey),
            ProviderId = ProviderKey.ProviderOf(unitKey),
            Source = AccountSource.Default,
            Order = ProviderKey.IsDefault(unitKey) ? 0 : NextAccountOrder(ProviderKey.ProviderOf(unitKey)),
        };
        Accounts.Add(account);
        return account;
    }

    public int NextAccountOrder(string providerId) => Accounts
        .Where(account => account.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        .Select(account => account.Order + 1)
        .DefaultIfEmpty(1)
        .Max();

    /// <summary>
    /// Whether a provider key is shown: its provider is ticked and, for an account, it is not hidden,
    /// waiting for an answer, or a repeat of an account already shown.
    /// </summary>
    public bool IsUnitVisible(string unitKey) =>
        IsProviderVisible(ProviderKey.ProviderOf(unitKey)) &&
        (FindAccount(unitKey) is not { } account ||
         (!account.Hidden && !account.Pending && account.DuplicateOf is null));

    /// <summary>
    /// Moves a shown card before or after another, as a drag in the popup or dashboard asks. A
    /// provider's accounts stay together: within one provider the account changes place among
    /// its siblings (their <see cref="ProviderAccount.Order"/>), and onto another provider's card
    /// the whole provider moves in <see cref="ProviderOrder"/>. False when nothing changed.
    /// </summary>
    public bool MoveUnit(IReadOnlyList<string> unitKeys, string moved, string target, bool after)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var ordered = OrderUnits(unitKeys);
        if (comparer.Equals(moved, target) || !ordered.Contains(moved, comparer) || !ordered.Contains(target, comparer))
        {
            return false;
        }

        var movedProvider = ProviderKey.ProviderOf(moved);
        var targetProvider = ProviderKey.ProviderOf(target);
        if (comparer.Equals(movedProvider, targetProvider))
        {
            var siblings = ordered.Where(key => comparer.Equals(ProviderKey.ProviderOf(key), movedProvider)).ToList();
            var before = siblings.ToArray();
            siblings.RemoveAll(key => comparer.Equals(key, moved));
            siblings.Insert(siblings.FindIndex(key => comparer.Equals(key, target)) + (after ? 1 : 0), moved);
            if (siblings.SequenceEqual(before, comparer))
            {
                return false;
            }

            for (var index = 0; index < siblings.Count; index++)
            {
                EnsureAccount(siblings[index]).Order = index;
            }

            return true;
        }

        // Providers not shown keep their saved place; shown ones the order has not named yet join it.
        var providers = ProviderOrder.Concat(ordered.Select(ProviderKey.ProviderOf)).Distinct(comparer).ToList();
        var previous = providers.ToArray();
        providers.RemoveAll(provider => comparer.Equals(provider, movedProvider));
        providers.Insert(providers.FindIndex(provider => comparer.Equals(provider, targetProvider)) + (after ? 1 : 0), movedProvider);
        if (providers.SequenceEqual(previous, comparer))
        {
            return false;
        }

        ProviderOrder = providers.ToArray();
        return true;
    }

    public bool AccountAlertsEnabled(string unitKey) => FindAccount(unitKey)?.Alerts ?? true;

    /// <summary>
    /// Orders provider keys by provider (the saved provider order), then each provider's accounts by
    /// their position, so one provider's accounts always sit together.
    /// </summary>
    public IReadOnlyList<string> OrderUnits(IReadOnlyList<string> unitKeys)
    {
        var providers = OrderProviders(unitKeys
            .Select(ProviderKey.ProviderOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        return providers
            .SelectMany(providerId => unitKeys
                .Select((key, index) => (Key: key, Index: index))
                .Where(entry => ProviderKey.ProviderOf(entry.Key).Equals(providerId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => FindAccount(entry.Key)?.Order ?? (ProviderKey.IsDefault(entry.Key) ? 0 : int.MaxValue))
                .ThenBy(entry => ProviderKey.IsDefault(entry.Key) ? 0 : 1)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Key))
            .ToArray();
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

        Accounts = NormalizeAccounts(Accounts);
        ForgottenAccounts = ForgottenAccounts is { Length: > 0 } forgotten
            ? forgotten
                .Where(id => id is { Length: > 0 and <= 80 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '@'))
                .Reverse()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxForgottenAccounts)
                .Reverse()
                .ToArray()
            : Array.Empty<string>();
        if (LastAccountScanUtc is { } lastScan)
        {
            var scannedUtc = lastScan.ToUniversalTime();
            LastAccountScanUtc = scannedUtc > DateTimeOffset.UtcNow.AddMinutes(5) ? null : scannedUtc;
        }

        // Deserialisation hands back a case-sensitive dictionary, or null for an explicit null.
        PopupMetricGroups = (PopupMetricGroups ?? new Dictionary<string, string>())
            .Where(entry => NormalizeId(entry.Key) is not null &&
                            !string.IsNullOrWhiteSpace(entry.Value) &&
                            entry.Value.Trim().Length <= 128)
            .Take(32)
            .GroupBy(entry => entry.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private const int MaxAccounts = 32;

    /// <summary>
    /// Keeps only well-formed accounts of providers that have them. A folder account's id is
    /// recomputed from its path, so a hand-edited id can never point one key at another folder.
    /// </summary>
    private static List<ProviderAccount> NormalizeAccounts(List<ProviderAccount>? accounts)
    {
        var kept = new List<ProviderAccount>();
        foreach (var account in accounts ?? new List<ProviderAccount>())
        {
            if (account is null ||
                string.IsNullOrWhiteSpace(account.ProviderId) ||
                !AccountDiscovery.SupportsAccounts(account.ProviderId.Trim()) ||
                !Enum.IsDefined(account.Source))
            {
                continue;
            }

            account.ProviderId = account.ProviderId.Trim().ToLowerInvariant();
            // The source decides, not the id: a folder entry whose id was left out is still a folder.
            if (account.Source == AccountSource.Default)
            {
                account.Id = ProviderKey.DefaultAccountId;
                account.Source = AccountSource.Default;
                account.Path = null;
                account.Pending = false;
                account.DuplicateOf = null;
                account.Command = null;
                account.UserCommands = Array.Empty<string>();
            }
            else
            {
                if (AccountDiscovery.PathProblem(account.Path) is not null)
                {
                    continue;
                }

                account.Path = ProviderKey.NormalizeFolder(account.Path!);
                account.Id = ProviderKey.AccountIdFor(account.Path);
            }

            account.Label = BoundedText(account.Label, 64);
            account.Email = BoundedText(account.Email, 320);
            account.Organization = BoundedText(account.Organization, 128);
            account.Identity = account.Identity is { Length: > 0 and <= 64 } identity && identity.All(char.IsAsciiLetterOrDigit)
                ? identity
                : null;
            account.DuplicateOf = BoundedText(account.DuplicateOf, 320);
            account.Command = TerminalCommands.NameProblem(account.Command) is null ? account.Command!.Trim() : null;
            account.UserCommands = (account.UserCommands ?? Array.Empty<string>())
                .Where(file => file is { Length: > 0 } && file.Length <= TerminalCommands.MaxUserCommandPath &&
                    Path.IsPathFullyQualified(file) &&
                    TerminalCommands.UserCommandExtensions.Contains(Path.GetExtension(file)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(AccountManager.MaxUserCommands)
                .ToArray();
            account.Order = Math.Clamp(account.Order, 0, 1000);
            if (kept.Any(other => other.UnitKey.Equals(account.UnitKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            kept.Add(account);
            if (kept.Count >= MaxAccounts)
            {
                break;
            }
        }

        return kept;
    }

    private static string? BoundedText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength && !trimmed.Any(char.IsControl) ? trimmed : null;
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
