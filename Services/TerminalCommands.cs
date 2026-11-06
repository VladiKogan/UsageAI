using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UsageAI.Services;

/// <summary>What writing or removing a terminal command came to.</summary>
internal sealed record TerminalCommandResult(
    string? Command,
    string? Problem,
    string? FilePath = null,
    bool PathChanged = false,
    bool PathUpdateFailed = false)
{
    public bool Succeeded => Problem is null;
}

/// <summary>
/// Terminal commands such as <c>claude-team</c>: a small <c>.cmd</c> file that sets
/// <c>CLAUDE_CONFIG_DIR</c> / <c>CODEX_HOME</c> to one login folder and starts the provider's CLI,
/// so an account's folder never has to be typed. The files live in UsageAI's own <c>bin</c> folder,
/// which is added to the user's PATH once. Only files that carry <see cref="Marker"/> are ever
/// overwritten or deleted, and nothing here reads or writes a credential.
/// </summary>
internal sealed partial class TerminalCommands
{
    /// <summary>The line every file UsageAI writes starts with; a file without it is not UsageAI's.</summary>
    public const string Marker = "@rem UsageAI terminal command";

    private const int MaxNameLength = 40;

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".com", ".exe", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msc", ".ps1",
    };

    private readonly string _folder;
    private readonly Func<string?> _readUserPath;
    private readonly Action<string> _writeUserPath;
    private readonly Func<string, string?> _findCommand;
    private readonly Func<string, string?> _findCli;
    private readonly string _userProfile;

    internal TerminalCommands(
        string folder,
        Func<string?> readUserPath,
        Action<string> writeUserPath,
        Func<string, string?> findCommand,
        Func<string, string?> findCli,
        string? userProfile = null)
    {
        _folder = Path.GetFullPath(folder);
        _readUserPath = readUserPath;
        _writeUserPath = writeUserPath;
        _findCommand = findCommand;
        _findCli = findCli;
        _userProfile = userProfile ?? AccountDiscovery.UserProfile();
    }

    /// <summary>The tray app's: <c>%LOCALAPPDATA%\UsageAI\bin</c>, the user's PATH and the installed CLIs.</summary>
    public static TerminalCommands CreateDefault()
    {
        var folder = Path.Combine(AppPaths.DataDirectory, "bin");
        return new TerminalCommands(
            folder,
            UserEnvironmentPath.Read,
            UserEnvironmentPath.Write,
            name => FindCommandOnPath(name, folder),
            FindCli);
    }

    /// <summary>The folder the command files are written to.</summary>
    public string Folder => _folder;

    public string FilePath(string name) => Path.Combine(_folder, name + ".cmd");

    /// <summary>
    /// The name a command is offered under: the provider's CLI name and the account's label, such as
    /// <c>claude-team</c>, else the folder's own suffix (<c>.claude-work</c> gives <c>claude-work</c>).
    /// </summary>
    public static string Suggest(string providerId, string? label, string? folder)
    {
        var cli = CliName(providerId);
        var slug = AccountDiscovery.Slug(label);
        if (slug.Length == 0 && folder is not null)
        {
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            foreach (var prefix in new[] { $".{cli}-", $".{cli}_", "." })
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[prefix.Length..];
                    break;
                }
            }

            slug = AccountDiscovery.Slug(name);
        }

        if (slug.Length == 0 || slug.Equals(cli, StringComparison.OrdinalIgnoreCase))
        {
            slug = "account-2";
        }

        var suggestion = $"{cli}-{slug}";
        return suggestion.Length <= MaxNameLength ? suggestion : suggestion[..MaxNameLength].TrimEnd('-');
    }

    /// <summary>Why a name cannot be a command, or null when it can.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the command.";
        }

        var trimmed = name.Trim();
        if (!CommandName().IsMatch(trimmed))
        {
            return $"Use letters, digits, '-', '_' or '.', starting with a letter or digit, up to {MaxNameLength} characters.";
        }

        // The file is the name plus .cmd, so "x.exe" would be x.exe.cmd and typing x.exe would not
        // find it; a trailing dot is dropped by Windows the same way.
        if (trimmed.EndsWith('.') || ProgramExtensions.Contains(Path.GetExtension(trimmed)))
        {
            return "Leave the name without a final dot or a program extension such as .exe; .cmd is added for you.";
        }

        if (trimmed.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed} already starts the default login; choose a name such as {trimmed}-work.";
        }

        return ReservedDeviceName().IsMatch(trimmed)
            ? "Windows reserves that name; choose another."
            : null;
    }

    /// <summary>
    /// Writes the command that starts <paramref name="providerId"/>'s CLI on <paramref name="loginFolder"/>,
    /// replacing <paramref name="previous"/> (the account's earlier command) when the name changed,
    /// and puts <see cref="Folder"/> on the user's PATH if it is not there yet.
    /// </summary>
    public TerminalCommandResult Create(string providerId, string loginFolder, string name, string? previous)
    {
        if (NameProblem(name) is { } problem)
        {
            return new TerminalCommandResult(null, problem);
        }

        name = name.Trim();
        var file = FilePath(name);
        if (_findCommand(name) is { } existing)
        {
            return new TerminalCommandResult(null, $"A {name} command already exists at {existing}. Choose another name.");
        }

        var renaming = previous is not null && !previous.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(file) && (!IsOurs(file) || renaming))
        {
            return new TerminalCommandResult(null, $"{file} already exists. Choose another name.");
        }

        if (Contents(providerId, loginFolder) is not { } contents)
        {
            return new TerminalCommandResult(null, _findCli(providerId) is null
                ? $"The {AccountDiscovery.ProviderName(providerId)} CLI was not found, so the command would have nothing to start. Install it, then try again."
                : "A .cmd file cannot hold that folder's path reliably: it has characters outside plain ASCII beyond your user folder.");
        }

        try
        {
            Directory.CreateDirectory(_folder);
            AppPaths.WriteAllTextAtomic(file, contents);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TerminalCommandResult(null, $"UsageAI could not write {file}.");
        }

        if (renaming)
        {
            Remove(previous!);
        }

        bool pathChanged;
        try
        {
            pathChanged = EnsureOnUserPath();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new TerminalCommandResult(name, null, file, PathUpdateFailed: true);
        }

        return new TerminalCommandResult(name, null, file, pathChanged);
    }

    /// <summary>
    /// Writes an existing command again when what it should hold has changed, such as an editor
    /// extension's bundled CLI that moved to a new version folder. Returns true when it rewrote the
    /// file; a missing file, one UsageAI did not write, or a CLI that cannot be found is left alone.
    /// </summary>
    public bool Refresh(string providerId, string loginFolder, string name)
    {
        if (NameProblem(name) is not null)
        {
            return false;
        }

        var file = FilePath(name.Trim());
        if (!File.Exists(file) || !IsOurs(file) || Contents(providerId, loginFolder) is not { } contents)
        {
            return false;
        }

        try
        {
            if (string.Equals(File.ReadAllText(file), contents, StringComparison.Ordinal))
            {
                return false;
            }

            AppPaths.WriteAllTextAtomic(file, contents);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The file's text: the login folder in the provider's variable, then the CLI. Setting
    /// <c>NoDefaultCurrentDirectoryInExePath</c> (inside <c>setlocal</c>, so it never leaks) keeps
    /// cmd from running a <c>claude.bat</c> or <c>codex.cmd</c> that sits in the current folder,
    /// such as a cloned repository, instead of the CLI on the PATH. Null when no CLI is found or a
    /// path cannot be written.
    /// </summary>
    private string? Contents(string providerId, string loginFolder)
    {
        if (_findCli(providerId) is not { } cli || CmdText(loginFolder) is not { } folderText || CmdText(cli) is not { } cliText)
        {
            return null;
        }

        return new StringBuilder()
            .Append(Marker).Append("\r\n")
            .Append("@rem Starts ").Append(AccountDiscovery.ProviderName(providerId)).Append(" with the login in the folder below.\r\n")
            .Append("@rem Forget the account in UsageAI to delete this file.\r\n")
            .Append("@echo off\r\n")
            .Append("setlocal\r\n")
            .Append("set \"NoDefaultCurrentDirectoryInExePath=1\"\r\n")
            .Append("set \"").Append(AccountDiscovery.FolderVariable(providerId)).Append('=').Append(folderText).Append("\"\r\n")
            .Append(cliText).Append(" %*\r\n")
            .ToString();
    }

    /// <summary>Deletes a command UsageAI wrote; a file without the marker is left alone.</summary>
    public bool Remove(string name)
    {
        if (NameProblem(name) is not null)
        {
            return false;
        }

        var file = FilePath(name.Trim());
        if (!File.Exists(file) || !IsOurs(file))
        {
            return false;
        }

        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the folder is already one of the user's PATH entries.</summary>
    public bool IsOnUserPath() => Contains(_readUserPath());

    private bool Contains(string? userPath) =>
        (userPath ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => SameFolder(entry, _folder));

    private bool EnsureOnUserPath()
    {
        var saved = _readUserPath();
        if (Contains(saved))
        {
            return false;
        }

        var current = (saved ?? string.Empty).TrimEnd(';');
        _writeUserPath(current.Length == 0 ? _folder : $"{current};{_folder}");
        return true;
    }

    private static bool IsOurs(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            return string.Equals(reader.ReadLine(), Marker, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A path as a <c>.cmd</c> file can hold it: the user folder becomes <c>%USERPROFILE%</c>, and
    /// <c>%</c> is doubled. Null when anything else is outside printable ASCII, since cmd reads the
    /// file in the console's code page.
    /// </summary>
    internal string? CmdText(string value)
    {
        var quoted = value.StartsWith('"');
        var path = quoted ? value.Trim('"') : value;
        var profile = _userProfile.TrimEnd('\\');
        string text;
        if (Path.IsPathFullyQualified(path) &&
            path.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
        {
            text = "%USERPROFILE%" + path[profile.Length..].Replace("%", "%%", StringComparison.Ordinal);
        }
        else
        {
            text = path.Replace("%", "%%", StringComparison.Ordinal);
        }

        if (text.Any(character => character is < ' ' or > '~'))
        {
            return null;
        }

        return quoted ? $"\"{text}\"" : text;
    }

    private static bool SameFolder(string entry, string folder)
    {
        var expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(expanded).TrimEnd('\\'),
                Path.GetFullPath(folder).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string CliName(string providerId) =>
        providerId.Equals(AccountDiscovery.CodexProviderId, StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";

    /// <summary>
    /// A command of that name elsewhere on the PATH (this process's and the user's saved one), which a
    /// new file would shadow or be shadowed by. Files in <paramref name="ownFolder"/> are UsageAI's.
    /// </summary>
    internal static string? FindCommandOnPath(string name, string ownFolder)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } pathExt ? pathExt : ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat((UserEnvironmentPath.Read() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(entry => Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')))
            .Where(Path.IsPathFullyQualified)
            .Where(directory => !SameFolder(directory, ownFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension.Trim());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How the command starts the CLI: its bare name when it is on the PATH, so an update that moves
    /// it is still found, else the absolute path of the copy UsageAI itself would run.
    /// </summary>
    internal static string? FindCli(string providerId)
    {
        var cli = CliName(providerId);
        var configured = Environment.GetEnvironmentVariable(cli == "claude" ? "CLAUDE_PATH" : "CODEX_PATH")?.Trim().Trim('"');
        // Only an .exe or .cmd, as the usage clients accept: a .ps1 started from a .cmd file opens
        // in an editor instead of running.
        if (!string.IsNullOrEmpty(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured) &&
            (configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || configured.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            return $"\"{Path.GetFullPath(configured)}\"";
        }

        if (ProcessSecurity.FindAbsoluteExecutableOnPath(cli + ".exe") is not null ||
            ProcessSecurity.FindAbsoluteExecutableOnPath(cli + ".cmd") is not null)
        {
            return cli;
        }

        var profile = AccountDiscovery.UserProfile();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = cli == "claude"
            ? new[]
            {
                Path.Combine(profile, ".local", "bin", "claude.exe"),
                Path.Combine(appData, "npm", "claude.cmd"),
                Path.Combine(localAppData, "Programs", "claude", "claude.exe"),
            }
            : new[] { Path.Combine(appData, "npm", "codex.cmd") };
        var found = candidates.FirstOrDefault(File.Exists)
            ?? (cli == "claude"
                ? EditorExtensionBinaries.FindClaude(profile, RuntimeInformation.OSArchitecture)
                : EditorExtensionBinaries.FindCodex(profile, RuntimeInformation.OSArchitecture));
        return found is null ? null : $"\"{found}\"";
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDeviceName();
}

/// <summary>The user's own saved PATH (<c>HKCU\Environment</c>), which new terminals start with.</summary>
internal static class UserEnvironmentPath
{
    private const string KeyName = "Environment";
    private const string ValueName = "Path";

    public static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyName);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>Saves the PATH, keeping its value kind, and tells running programs it changed.</summary>
    public static void Write(string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyName))
        {
            var kind = key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase)
                ? key.GetValueKind(ValueName)
                : RegistryValueKind.ExpandString;
            key.SetValue(ValueName, value, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        }

        // Explorer re-reads the environment on this broadcast, so a terminal opened from it next
        // finds the command; one already open keeps the PATH it started with.
        _ = SendMessageTimeout(
            new IntPtr(0xFFFF),
            0x001A,
            IntPtr.Zero,
            KeyName,
            0x0002,
            1000,
            out _);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        int message,
        IntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out IntPtr result);
}
