using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>Who is signed in to a login folder, read from the provider's own local files.</summary>
internal sealed record AccountIdentity(string? Email, string? OrganizationId, string? OrganizationName)
{
    /// <summary>A hash of email and organisation, so the identity can be compared without storing either.</summary>
    public string Hash
    {
        get
        {
            var source = $"{Email?.Trim().ToLowerInvariant()}|{OrganizationId?.Trim().ToLowerInvariant()}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)), 0, 8).ToLowerInvariant();
        }
    }

    public bool IsKnown => !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(OrganizationId);
}

/// <summary>A login folder the scan found in the user's profile.</summary>
internal sealed record FoundLogin(string ProviderId, string Folder);

/// <summary>
/// Finds and checks provider login folders. Discovery is one non-recursive listing of the user's
/// profile; every other folder is one the user chose. Nothing here writes to a login folder.
/// </summary>
internal static class AccountDiscovery
{
    public const string ClaudeProviderId = "claude";
    public const string CodexProviderId = "codex";

    /// <summary>The providers whose logins can be kept in more than one folder.</summary>
    public static readonly IReadOnlyList<string> MultiAccountProviders = [ClaudeProviderId, CodexProviderId];

    /// <summary>Bounds the identity file read: <c>.claude.json</c> keeps per-project state and grows.</summary>
    private const int MaxIdentityFileCharacters = 8_000_000;

    private const int MaxFoundLogins = 24;

    public static bool SupportsAccounts(string providerId) =>
        MultiAccountProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase);

    public static string CredentialFileName(string providerId) =>
        providerId.Equals(ClaudeProviderId, StringComparison.OrdinalIgnoreCase) ? ".credentials.json" : "auth.json";

    /// <summary>The variable that points the provider's CLI at a login folder.</summary>
    public static string FolderVariable(string providerId) =>
        providerId.Equals(ClaudeProviderId, StringComparison.OrdinalIgnoreCase) ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME";

    public static string ProviderName(string providerId) => ProviderNames.Short(providerId);

    public static string UserProfile()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrWhiteSpace(profile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : profile;
    }

    /// <summary>
    /// The main login's folder, the one keyed by the bare provider id: always the provider's standard
    /// <c>~\.claude</c> / <c>~\.codex</c>, the folder its CLI uses when no variable is set. It never
    /// follows an inherited <c>CLAUDE_CONFIG_DIR</c> / <c>CODEX_HOME</c>, so the main tile reads the
    /// same login however UsageAI was started; a folder such a variable names is found as an
    /// account of its own instead (<see cref="InheritedFolder"/>).
    /// </summary>
    public static string DefaultFolder(string providerId, string? userProfile = null)
    {
        var name = providerId.Equals(ClaudeProviderId, StringComparison.OrdinalIgnoreCase) ? ".claude" : ".codex";
        return ProviderKey.NormalizeFolder(Path.Combine(userProfile ?? UserProfile(), name));
    }

    /// <summary>A folder as the UI shows it: under the user profile as <c>%USERPROFILE%\…</c>.</summary>
    public static string DisplayFolder(string folder, string? userProfile = null)
    {
        var profile = (userProfile ?? UserProfile()).TrimEnd('\\', '/');
        return profile.Length > 0 && folder.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + folder[profile.Length..]
            : folder;
    }

    /// <summary>
    /// The folder an inherited <c>CLAUDE_CONFIG_DIR</c> / <c>CODEX_HOME</c> names, when it is an
    /// absolute local path other than the standard folder; null otherwise.
    /// </summary>
    public static string? InheritedFolder(string providerId, string? userProfile = null)
    {
        var configured = Environment.GetEnvironmentVariable(FolderVariable(providerId))?.Trim().Trim('"');
        if (string.IsNullOrEmpty(configured) || PathProblem(configured) is not null)
        {
            return null;
        }

        var folder = ProviderKey.NormalizeFolder(configured);
        return folder.Equals(DefaultFolder(providerId, userProfile), StringComparison.OrdinalIgnoreCase) ? null : folder;
    }

    /// <summary>The login an inherited variable points at, when its folder holds the provider's credential file.</summary>
    public static IReadOnlyList<FoundLogin> InheritedLogins(string? userProfile = null) =>
        MultiAccountProviders
            .Select(providerId => (providerId, folder: InheritedFolder(providerId, userProfile)))
            .Where(entry => entry.folder is not null && File.Exists(Path.Combine(entry.folder, CredentialFileName(entry.providerId))))
            .Select(entry => new FoundLogin(entry.providerId, entry.folder!))
            .ToArray();

    /// <summary>
    /// Immediate children of <paramref name="userProfile"/> whose name starts with <c>.claude</c> or
    /// <c>.codex</c> (see <see cref="ProviderForFolderName"/>) and that hold their provider's
    /// credential file. One listing, no recursion; the default login folders are left out.
    /// </summary>
    public static IReadOnlyList<FoundLogin> Scan(string userProfile, IEnumerable<string> excludedFolders)
    {
        var excluded = new HashSet<string>(
            excludedFolders.Select(ProviderKey.NormalizeFolder),
            StringComparer.OrdinalIgnoreCase);
        var found = new List<FoundLogin>();
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(userProfile, ".*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return found;
        }

        try
        {
            foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
            {
                var providerId = ProviderForFolderName(Path.GetFileName(child));
                if (providerId is null || excluded.Contains(ProviderKey.NormalizeFolder(child)))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(child, CredentialFileName(providerId))))
                {
                    found.Add(new FoundLogin(providerId, ProviderKey.NormalizeFolder(child)));
                    if (found.Count >= MaxFoundLogins)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A profile that cannot be listed in full keeps what was found so far.
        }

        return found;
    }

    /// <summary>
    /// The provider a folder name belongs to: anything that starts with <c>.claude</c> or
    /// <c>.codex</c> and goes on, so <c>.claude-work</c>, <c>.claude_work</c>, <c>.claude2</c> and
    /// <c>.codex.work</c> all count. The bare <c>.claude</c> / <c>.codex</c> is the default login,
    /// never a found one. A wide rule is safe because only a folder holding the provider's own
    /// credential file is taken, and a copy of a login already shown is marked a duplicate.
    /// </summary>
    internal static string? ProviderForFolderName(string name)
    {
        foreach (var (prefix, providerId) in new[] { (".claude", ClaudeProviderId), (".codex", CodexProviderId) })
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return providerId;
            }
        }

        return null;
    }

    /// <summary>Why a path cannot be used as a login folder, or null when it can.</summary>
    internal static string? PathProblem(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "Choose a folder.";
        }

        var trimmed = folder.Trim().Trim('"');
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "Choose a folder on this computer, not a network path.";
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return "Enter the folder's full path, such as C:\\Users\\you\\.claude-work.";
        }

        try
        {
            _ = Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That is not a valid folder path.";
        }

        return null;
    }

    /// <summary>Why an existing folder cannot be read as a login, or null when it holds one.</summary>
    public static string? ExistingLoginProblem(string providerId, string? folder)
    {
        if (PathProblem(folder) is { } problem)
        {
            return problem;
        }

        var full = ProviderKey.NormalizeFolder(folder!);
        if (!Directory.Exists(full))
        {
            return "That folder does not exist.";
        }

        return File.Exists(Path.Combine(full, CredentialFileName(providerId)))
            ? null
            : $"That folder holds no {ProviderName(providerId)} login ({CredentialFileName(providerId)} is missing).";
    }

    /// <summary>What a folder chosen for a new sign-in holds.</summary>
    internal enum NewFolderState
    {
        Usable,
        Invalid,
        HoldsLogin,
        NotEmpty,
    }

    /// <summary>A new sign-in needs a folder that is missing or empty; one already signed in is used instead.</summary>
    public static (NewFolderState State, string? Problem) CheckNewLoginFolder(string providerId, string? folder)
    {
        if (PathProblem(folder) is { } problem)
        {
            return (NewFolderState.Invalid, problem);
        }

        var full = ProviderKey.NormalizeFolder(folder!);
        if (File.Exists(full))
        {
            return (NewFolderState.Invalid, "That path is a file, not a folder.");
        }

        if (!Directory.Exists(full))
        {
            return (NewFolderState.Usable, null);
        }

        if (File.Exists(Path.Combine(full, CredentialFileName(providerId))))
        {
            return (NewFolderState.HoldsLogin, "That folder already holds a login.");
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(full).Any()
                ? (NewFolderState.NotEmpty, "Choose an empty or new folder: this one already has files in it.")
                : (NewFolderState.Usable, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (NewFolderState.Invalid, "UsageAI cannot read that folder.");
        }
    }

    /// <summary>
    /// The folder a new sign-in proposes: <c>%USERPROFILE%\.claude-&lt;name&gt;</c>, or
    /// <c>.claude-account-2</c> without a name, with <c>-2</c>, <c>-3</c> … while the name is taken.
    /// </summary>
    public static string SuggestFolder(string providerId, string? name, string? userProfile = null)
    {
        var profile = userProfile ?? UserProfile();
        var prefix = providerId.Equals(ClaudeProviderId, StringComparison.OrdinalIgnoreCase) ? ".claude-" : ".codex-";
        var slug = Slug(name);
        var stem = slug.Length > 0 ? slug : "account";
        var start = slug.Length > 0 ? 1 : 2;
        for (var attempt = start; attempt < 100; attempt++)
        {
            var candidate = Path.Combine(profile, attempt == 1 ? $"{prefix}{stem}" : $"{prefix}{stem}-{attempt}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(profile, $"{prefix}{stem}-{Guid.NewGuid().ToString("N")[..8]}");
    }

    /// <summary>A name reduced to letters, digits and dashes, for a folder name.</summary>
    internal static string Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    /// <summary>
    /// Who is signed in to a login folder, from local files only: Claude's <c>.claude.json</c>
    /// <c>oauthAccount</c>, or the email and workspace in Codex's <c>id_token</c>. Null when unknown.
    /// </summary>
    public static AccountIdentity? ReadIdentity(string providerId, string folder, string? userProfile = null)
    {
        try
        {
            return providerId.Equals(ClaudeProviderId, StringComparison.OrdinalIgnoreCase)
                ? ReadClaudeIdentity(ClaudeIdentityFile(folder, userProfile))
                : ReadCodexIdentity(Path.Combine(folder, "auth.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Claude keeps its account profile in <c>.claude.json</c>: inside the config folder when one is
    /// set, but next to <c>~\.claude</c> (as <c>~\.claude.json</c>) for the plain default login.
    /// </summary>
    internal static string ClaudeIdentityFile(string folder, string? userProfile = null)
    {
        var inFolder = Path.Combine(folder, ".claude.json");
        var inProfile = Path.Combine(userProfile ?? UserProfile(), ".claude.json");
        var isProfileFolder = string.Equals(
            ProviderKey.NormalizeFolder(folder),
            ProviderKey.NormalizeFolder(Path.Combine(userProfile ?? UserProfile(), ".claude")),
            StringComparison.OrdinalIgnoreCase);
        if (!isProfileFolder)
        {
            return inFolder;
        }

        // ~\.claude itself: the CLI keeps the profile beside it (~\.claude.json) when no variable is
        // set, which is how UsageAI reads this folder; one kept inside it only answers when that is
        // the only one there.
        return File.Exists(inProfile) || !File.Exists(inFolder) ? inProfile : inFolder;
    }

    private static readonly Dictionary<string, (DateTime WrittenAt, long Length, AccountIdentity? Identity)> IdentityCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static AccountIdentity? ReadClaudeIdentity(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        // The file is large and rewritten often, but its account rarely changes; it is parsed again
        // only when it was written since the last read.
        lock (IdentityCache)
        {
            if (IdentityCache.TryGetValue(path, out var cached) &&
                cached.WrittenAt == info.LastWriteTimeUtc &&
                cached.Length == info.Length)
            {
                return cached.Identity;
            }
        }

        using var document = JsonDocument.Parse(
            SecureLocalFile.ReadAllText(path, MaxIdentityFileCharacters),
            new JsonDocumentOptions { MaxDepth = 256 });
        AccountIdentity? identity = null;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("oauthAccount", out var account) &&
            account.ValueKind == JsonValueKind.Object)
        {
            identity = new AccountIdentity(
                Bounded(GetString(account, "emailAddress"), 320),
                Bounded(GetString(account, "organizationUuid"), 128),
                Bounded(GetString(account, "organizationName"), 128));
        }

        identity = identity is { IsKnown: true } ? identity : null;
        lock (IdentityCache)
        {
            IdentityCache[path] = (info.LastWriteTimeUtc, info.Length, identity);
        }

        return identity;
    }

    private static AccountIdentity? ReadCodexIdentity(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(SecureLocalFile.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("tokens", out var tokens) ||
            tokens.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? email = null;
        string? workspace = null;
        if (GetString(tokens, "id_token") is { } idToken && JwtPayload(idToken) is { } payload)
        {
            using (payload)
            {
                email = GetString(payload.RootElement, "email");
                if (payload.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) &&
                    auth.ValueKind == JsonValueKind.Object)
                {
                    workspace = GetString(auth, "chatgpt_account_id");
                }
            }
        }

        workspace ??= GetString(tokens, "account_id");
        var identity = new AccountIdentity(Bounded(email, 320), Bounded(workspace, 128), null);
        return identity.IsKnown ? identity : null;
    }

    /// <summary>A JWT's payload, read without checking its signature: it only names who is signed in.</summary>
    internal static JsonDocument? JwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length > 16_384)
        {
            return null;
        }

        try
        {
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
            var document = JsonDocument.Parse(Convert.FromBase64String(segment), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Bounded(string? value, int maxLength) =>
        value is { Length: > 0 } text && text.Length <= maxLength && !text.Any(char.IsControl) ? text.Trim() : null;
}

/// <summary>The commands that point a provider's CLI at one login folder, shown for the user to run.</summary>
internal static class AccountCommands
{
    /// <summary>PowerShell, the Windows Terminal default: sets the folder variable, then signs in.</summary>
    public static string SignIn(string providerId, string folder) =>
        $"{UseFolder(providerId, folder)}; {(providerId.Equals(AccountDiscovery.CodexProviderId, StringComparison.OrdinalIgnoreCase) ? "codex login" : "claude")}";

    /// <summary>The line that makes the CLI in a terminal use <paramref name="folder"/>.</summary>
    public static string UseFolder(string providerId, string folder) =>
        $"$env:{AccountDiscovery.FolderVariable(providerId)}='{folder.Replace("'", "''", StringComparison.Ordinal)}'";
}
