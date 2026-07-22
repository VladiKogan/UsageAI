using Microsoft.Win32;

namespace UsageAI.Services;

internal static class UsageProviderSettings
{
    private const string RegistryPath = @"Software\UsageAI";
    private const string ProviderValueName = "UsageProvider";

    public static string SelectedProviderId
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
                return key?.GetValue(ProviderValueName) as string ?? "codex";
            }
            catch
            {
                return "codex";
            }
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            key.SetValue(ProviderValueName, value, RegistryValueKind.String);
        }
    }
}
