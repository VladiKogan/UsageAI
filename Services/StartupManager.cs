using Microsoft.Win32;

namespace UsageAI.Services;

/// <summary>
/// Why a registered startup entry would not actually launch UsageAI. The registry value existing
/// is not the same as Windows honouring it, and a checkbox that only reports the value would keep
/// claiming "on" in both of the states below.
/// </summary>
internal enum StartupStatus
{
    /// <summary>No entry at all: UsageAI does not start with Windows.</summary>
    NotRegistered,

    /// <summary>Registered, approved, and pointing at the copy that is running.</summary>
    Enabled,

    /// <summary>
    /// Registered, but switched off in Task Manager or Settings ▸ Startup Apps. Windows records
    /// that veto beside the entry rather than deleting it, so the entry outlives its own effect.
    /// </summary>
    BlockedByWindows,

    /// <summary>
    /// Registered for a different executable — the copy that enabled it was moved, deleted, or
    /// replaced by an install elsewhere. Windows still runs that path, or fails silently.
    /// </summary>
    PathMismatch,
}

/// <summary>
/// The effective startup state, not merely the presence of a registry value.
/// </summary>
/// <param name="Status">Why UsageAI would or would not launch with Windows.</param>
/// <param name="RegisteredPath">
/// The executable the entry points at, or <see langword="null"/> when nothing is registered.
/// </param>
internal readonly record struct StartupState(StartupStatus Status, string? RegisteredPath)
{
    /// <summary>Whether Windows would really launch this copy at logon.</summary>
    public bool WillRun => Status == StartupStatus.Enabled;
}

internal static class StartupManager
{
    private const string LiveRegistryRoot = @"Software\Microsoft\Windows\CurrentVersion";
    private const string ValueName = "UsageAI";
    private static string _registryRoot = LiveRegistryRoot;

    private static string RunKeyPath => $@"{_registryRoot}\Run";

    /// <summary>
    /// Where Task Manager and Settings ▸ Startup Apps record a per-entry veto. A first byte with
    /// bit 0 set means disabled; an absent value means the entry is approved.
    /// </summary>
    private static string ApprovedKeyPath => $@"{_registryRoot}\Explorer\StartupApproved\Run";

    public static StartupState Current =>
        ReadStateAt(RunKeyPath, ApprovedKeyPath, ValueName, Environment.ProcessPath);

    /// <summary>
    /// Points the manager at a scratch key so a test that exercises a settings dialog end to end
    /// cannot register — or unregister — the real machine, the same way the test harness redirects
    /// <c>USAGEAI_DATA_DIR</c> away from the real profile.
    /// </summary>
    internal static void SetRegistryRootForTests(string? root) =>
        _registryRoot = string.IsNullOrWhiteSpace(root) ? LiveRegistryRoot : root;

    /// <summary>
    /// Makes the effective state match <paramref name="enabled"/>: enabling re-points the entry at
    /// the running executable and clears any Startup Apps veto, so a tick always means "this copy
    /// will launch", and disabling removes both the entry and the stale veto.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        var executable = enabled
            ? Environment.ProcessPath
              ?? throw new InvalidOperationException("Could not determine the UsageAI executable path.")
            : null;
        SetEnabledAt(RunKeyPath, ApprovedKeyPath, ValueName, enabled, executable);
    }

    internal static StartupState ReadStateAt(
        string runKeyPath,
        string approvedKeyPath,
        string valueName,
        string? currentExecutable)
    {
        string? registered;
        using (var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false))
        {
            registered = runKey?.GetValue(valueName) as string;
        }

        if (registered is null)
        {
            return new StartupState(StartupStatus.NotRegistered, null);
        }

        var registeredPath = UnquotePath(registered);
        if (IsBlockedAt(approvedKeyPath, valueName))
        {
            return new StartupState(StartupStatus.BlockedByWindows, registeredPath);
        }

        // A null ProcessPath is not something a normal run produces, and guessing "mismatch" from
        // it would report a broken entry that is fine.
        if (currentExecutable is { Length: > 0 } && !SamePath(registeredPath, currentExecutable))
        {
            return new StartupState(StartupStatus.PathMismatch, registeredPath);
        }

        return new StartupState(StartupStatus.Enabled, registeredPath);
    }

    internal static void SetEnabledAt(
        string runKeyPath,
        string approvedKeyPath,
        string valueName,
        bool enabled,
        string? executablePath)
    {
        using (var runKey = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true))
        {
            if (enabled)
            {
                var executable = executablePath
                    ?? throw new InvalidOperationException("Could not determine the UsageAI executable path.");
                runKey.SetValue(valueName, $"\"{executable}\"");
            }
            else
            {
                runKey.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }

        // Either direction clears the Startup Apps veto: an absent value is how Windows spells
        // "approved", and leaving a stale one behind after a disable would silently suppress the
        // next enable. The key itself is left alone when it does not exist and we have nothing to
        // remove, so a machine that has never vetoed anything does not gain the key from us.
        using var approvedKey = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: true);
        approvedKey?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static bool IsBlockedAt(string approvedKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: false);
        return key?.GetValue(valueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) != 0;
    }

    /// <summary>
    /// Recovers the executable from a Run value. UsageAI writes its own path quoted, but the entry
    /// can have been edited by hand or by another tool, so an unquoted path is read too.
    /// </summary>
    private static string UnquotePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 0 ? trimmed[1..closing] : trimmed[1..];
        }

        return trimmed;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed registered path cannot be the running executable, so it is a mismatch
            // rather than a reason to fail reading the state.
            return false;
        }
    }
}
