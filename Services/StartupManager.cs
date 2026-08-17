using Microsoft.Win32;

namespace UsageAI.Services;

internal static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsageAI";

    public static bool IsEnabled => IsEnabledAt(RegistryPath, ValueName);

    public static void SetEnabled(bool enabled)
    {
        var executable = enabled
            ? Environment.ProcessPath
              ?? throw new InvalidOperationException("Could not determine the UsageAI executable path.")
            : null;
        SetEnabledAt(RegistryPath, ValueName, enabled, executable);
    }

    internal static bool IsEnabledAt(string registryPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false);
        return key?.GetValue(valueName) is string;
    }

    internal static void SetEnabledAt(
        string registryPath,
        string valueName,
        bool enabled,
        string? executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        var executable = executablePath
            ?? throw new InvalidOperationException("Could not determine the UsageAI executable path.");
        key.SetValue(valueName, $"\"{executable}\"");
    }
}
