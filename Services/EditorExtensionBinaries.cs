using System.Runtime.InteropServices;
using System.Text.Json;

namespace UsageAI.Services;

/// <summary>
/// Finds a provider CLI bundled in an installed editor extension, the last resort after a provider's
/// standalone install. Each editor's <c>extensions.json</c> names the folder of the version it
/// actually loads, so stale folders of older versions are never picked and no directory is searched;
/// inside that folder only the fixed paths the extension itself uses are tried. The editor extension
/// gets the same binaries from VS Code's own API.
/// </summary>
internal static class EditorExtensionBinaries
{
    private const int MaxManifestCharacters = 4_194_304;

    /// <summary>VS Code and the VS Code-based editors that keep extensions in the same layout.</summary>
    private static readonly string[] EditorDataFolders =
        [".vscode", ".vscode-insiders", ".vscode-oss", ".cursor", ".windsurf", ".antigravity"];

    /// <summary>
    /// Mirrors the Claude Code extension's own lookup: a multi-platform package keeps
    /// <c>resources/native-binaries/&lt;platform&gt;/claude.exe</c>, where Windows on ARM falls back to
    /// the x64 build, and a platform-specific package keeps a single
    /// <c>resources/native-binary/claude.exe</c>.
    /// </summary>
    private static readonly string[] ClaudeX64Paths =
    [
        Path.Combine("resources", "native-binaries", "win32-x64", "claude.exe"),
        Path.Combine("resources", "native-binary", "claude.exe"),
    ];

    private static readonly string[] ClaudeArm64Paths =
        [Path.Combine("resources", "native-binaries", "win32-arm64", "claude.exe"), .. ClaudeX64Paths];

    /// <summary>
    /// Mirrors the Codex extension's own lookup, <c>bin/windows-&lt;arch&gt;/codex.exe</c>, with x64 as
    /// the fallback Windows on ARM can still run.
    /// </summary>
    private static readonly string[] CodexX64Paths = [Path.Combine("bin", "windows-x86_64", "codex.exe")];

    private static readonly string[] CodexArm64Paths =
        [Path.Combine("bin", "windows-aarch64", "codex.exe"), .. CodexX64Paths];

    internal static string? FindClaude(string userProfile, Architecture architecture) =>
        Find(userProfile, "anthropic.claude-code", architecture == Architecture.Arm64 ? ClaudeArm64Paths : ClaudeX64Paths);

    internal static string? FindCodex(string userProfile, Architecture architecture) =>
        Find(userProfile, "openai.chatgpt", architecture == Architecture.Arm64 ? CodexArm64Paths : CodexX64Paths);

    private static string? Find(string userProfile, string extensionId, IReadOnlyList<string> relativePaths)
    {
        if (!Path.IsPathFullyQualified(userProfile))
        {
            return null;
        }

        foreach (var editor in EditorDataFolders)
        {
            var folder = FindInstalledExtensionFolder(Path.Combine(userProfile, editor, "extensions"), extensionId);
            if (folder is not null &&
                relativePaths.Select(relative => Path.Combine(folder, relative)).FirstOrDefault(File.Exists) is { } executable)
            {
                return executable;
            }
        }

        return null;
    }

    private static string? FindInstalledExtensionFolder(string extensionsDirectory, string extensionId)
    {
        var manifest = Path.Combine(extensionsDirectory, "extensions.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(SecureLocalFile.ReadAllText(manifest, MaxManifestCharacters));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("identifier", out var identifier) ||
                    identifier.ValueKind != JsonValueKind.Object ||
                    !identifier.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    !string.Equals(id.GetString(), extensionId, StringComparison.OrdinalIgnoreCase) ||
                    !entry.TryGetProperty("relativeLocation", out var relative) ||
                    relative.ValueKind != JsonValueKind.String ||
                    relative.GetString() is not { Length: > 0 } folderName)
                {
                    continue;
                }

                // A bare folder name only: nothing in the manifest may point outside the extensions
                // folder. An entry that does, or whose folder an interrupted update removed, is skipped
                // so a later entry for the same extension can still serve.
                if (folderName is "." or ".." || Path.GetFileName(folderName) != folderName)
                {
                    continue;
                }

                var folder = Path.Combine(extensionsDirectory, folderName);
                if (Directory.Exists(folder))
                {
                    return folder;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            // An unreadable manifest only means this editor lends no binary.
        }

        return null;
    }
}
