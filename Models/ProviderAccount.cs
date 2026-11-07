using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace UsageAI.Models;

/// <summary>Where UsageAI learned about a login.</summary>
internal enum AccountSource
{
    /// <summary>The provider's own default login, read exactly as before accounts existed.</summary>
    Default,

    /// <summary>A <c>%USERPROFILE%\.claude…</c> / <c>.codex…</c> folder the scan found.</summary>
    Found,

    /// <summary>A login folder the user pointed UsageAI at.</summary>
    Added,

    /// <summary>A folder UsageAI created and ran the provider's official CLI login in.</summary>
    Created,
}

/// <summary>
/// One login of a provider. The default login has <see cref="Id"/> <c>"default"</c> and no
/// <see cref="Path"/>; every other account is read from its own folder, which UsageAI never
/// writes to (the provider's CLI does).
/// </summary>
internal sealed class ProviderAccount
{
    public string Id { get; set; } = ProviderKey.DefaultAccountId;

    public string ProviderId { get; set; } = string.Empty;

    public AccountSource Source { get; set; } = AccountSource.Default;

    /// <summary>The login folder (<c>CLAUDE_CONFIG_DIR</c> / <c>CODEX_HOME</c>); null for the default login.</summary>
    public string? Path { get; set; }

    /// <summary>The name the user gave the account, if any.</summary>
    public string? Label { get; set; }

    /// <summary>Kept out of the popup and dashboard.</summary>
    public bool Hidden { get; set; }

    /// <summary>Whether usage alerts are raised for this account.</summary>
    public bool Alerts { get; set; } = true;

    /// <summary>Position among the provider's accounts; the default login starts first.</summary>
    public int Order { get; set; }

    /// <summary>A hash of who is signed in (email and organisation), never the email itself.</summary>
    public string? Identity { get; set; }

    /// <summary>The signed-in email, kept for labels while the login cannot be read.</summary>
    public string? Email { get; set; }

    /// <summary>The organisation or workspace name, used to tell two logins of one email apart.</summary>
    public string? Organization { get; set; }

    /// <summary>Found later and not yet answered with Show or Ignore, so it is not shown yet.</summary>
    public bool Pending { get; set; }

    /// <summary>The label of the account already showing the same person, when this one repeats it.</summary>
    public string? DuplicateOf { get; set; }

    /// <summary>The terminal command UsageAI wrote for this login folder, such as <c>claude-work</c>.</summary>
    public string? Command { get; set; }

    [JsonIgnore]
    public bool IsDefault => ProviderKey.IsDefaultAccount(Id);

    [JsonIgnore]
    public string UnitKey => ProviderKey.Compose(ProviderId, Id);

    /// <summary>Shown in the popup, dashboard and alerts: the user's name, else the email, else the folder.</summary>
    [JsonIgnore]
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label!
        : !string.IsNullOrWhiteSpace(Email) ? Email!
        : Path is { Length: > 0 } path ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path
        : "Default";

    public ProviderAccount Clone() => (ProviderAccount)MemberwiseClone();
}

/// <summary>
/// The key every per-provider record uses — snapshots, history, alerts, motion, popup groups and the
/// tray choice. The default login keeps the bare provider id, so a single-account profile reads
/// exactly as it did; other accounts add <c>@</c> and their account id.
/// </summary>
internal static class ProviderKey
{
    public const string DefaultAccountId = "default";

    public const char Separator = '@';

    public static string Compose(string providerId, string accountId) =>
        IsDefaultAccount(accountId) ? providerId : $"{providerId}{Separator}{accountId}";

    /// <summary>The provider part of a key: what decides its icon, colour, incidents and visibility.</summary>
    public static string ProviderOf(string key)
    {
        var index = key.IndexOf(Separator, StringComparison.Ordinal);
        return index < 0 ? key : key[..index];
    }

    public static string AccountOf(string key)
    {
        var index = key.IndexOf(Separator, StringComparison.Ordinal);
        return index < 0 ? DefaultAccountId : key[(index + 1)..];
    }

    public static bool IsDefault(string key) => key.IndexOf(Separator, StringComparison.Ordinal) < 0;

    public static bool IsDefaultAccount(string? accountId) =>
        string.IsNullOrEmpty(accountId) || accountId.Equals(DefaultAccountId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The account id of a login folder: eight hex characters of SHA-256 over the normalised path,
    /// the same as the editor extension computes, so both name a folder alike.
    /// </summary>
    public static string AccountIdFor(string folder)
    {
        var normalized = NormalizeFolder(folder).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>A full path without trailing separators (a drive root keeps its own).</summary>
    public static string NormalizeFolder(string folder)
    {
        var full = System.IO.Path.GetFullPath(folder.Trim().Trim('"'));
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd('\\', '/');
        return trimmed.Length < root.Length ? root : trimmed;
    }
}

/// <summary>The rule that keeps two accounts' labels apart.</summary>
internal static class AccountLabels
{
    /// <summary>
    /// Each key's label: the account's own, and when two read the same, the organisation is added,
    /// else "Account 2", "Account 3" … in order.
    /// </summary>
    public static IEnumerable<(string Key, string Label)> Distinct(IEnumerable<(string Key, ProviderAccount? Account)> accounts)
    {
        var entries = accounts
            .Select(entry => (entry.Key, entry.Account, Label: entry.Account?.DisplayLabel ?? "Default"))
            .ToArray();
        foreach (var group in entries.GroupBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToArray();
            if (members.Length == 1)
            {
                yield return (members[0].Key, members[0].Label);
                continue;
            }

            var organisations = members.Select(member => member.Account?.Organization).ToArray();
            var byOrganisation = organisations.All(name => !string.IsNullOrWhiteSpace(name)) &&
                                 organisations.Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Length;
            for (var index = 0; index < members.Length; index++)
            {
                yield return (
                    members[index].Key,
                    byOrganisation
                        ? $"{members[index].Label} · {organisations[index]}"
                        : index == 0 ? members[index].Label : $"{members[index].Label} · Account {index + 1}");
            }
        }
    }
}
