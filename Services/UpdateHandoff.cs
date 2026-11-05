using System.Text.Json;

namespace UsageAI.Services;

/// <summary>What the copy that started an installer wrote down before Windows closed it.</summary>
internal sealed record UpdateHandoff(string FromVersion, string ToVersion, DateTimeOffset StartedAt);

internal enum UpdateOutcome
{
    /// <summary>The updated version is the one now running.</summary>
    Installed,

    /// <summary>The version that started the installer is running again, so it never finished.</summary>
    NotInstalled,
}

internal sealed record UpdateHandoffResult(UpdateOutcome Outcome, UpdateHandoff Handoff);

/// <summary>
/// Carries an in-app update across the restart. The installer runs without a window of its own,
/// so the copy it closes records which version it was installing, and the copy Windows reopens
/// reads that note once to say whether the update arrived. The note expires, so an old one can
/// never announce an update the user did not just make.
/// </summary>
internal static class UpdateHandoffStore
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int MaximumCharacters = 4_096;

    public static void Save(UpdateHandoff handoff) => Save(AppPaths.UpdateHandoffFile, handoff);

    internal static void Save(string path, UpdateHandoff handoff)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(handoff));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without the note the update still installs; only the confirmation afterwards is lost.
        }
    }

    public static void Delete() => DeleteBestEffort(AppPaths.UpdateHandoffFile);

    public static UpdateHandoffResult? Take(string runningVersion) =>
        Take(AppPaths.UpdateHandoffFile, runningVersion, DateTimeOffset.UtcNow);

    /// <summary>Reads and removes the note, and says what became of the update it describes.</summary>
    internal static UpdateHandoffResult? Take(string path, string runningVersion, DateTimeOffset now)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        UpdateHandoff? handoff = null;
        try
        {
            handoff = JsonSerializer.Deserialize<UpdateHandoff>(
                SecureLocalFile.ReadAllText(path, maxCharacters: MaximumCharacters));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
        }
        finally
        {
            DeleteBestEffort(path);
        }

        if (handoff is null ||
            !IsVersion(handoff.FromVersion) ||
            !IsVersion(handoff.ToVersion) ||
            handoff.StartedAt > now + TimeSpan.FromMinutes(1) ||
            now - handoff.StartedAt > Lifetime)
        {
            return null;
        }

        if (SameVersion(runningVersion, handoff.ToVersion))
        {
            return new UpdateHandoffResult(UpdateOutcome.Installed, handoff);
        }

        return SameVersion(runningVersion, handoff.FromVersion)
            ? new UpdateHandoffResult(UpdateOutcome.NotInstalled, handoff)
            : null;
    }

    /// <summary>
    /// Deletes installers earlier updates downloaded. Each is about 2.5 MB and was never needed
    /// again once it ran. One still running is locked and stays until the next attempt.
    /// </summary>
    public static int CleanUpInstallers() => CleanUpInstallers(AppPaths.UpdateDirectory);

    internal static int CleanUpInstallers(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "UsageAI-*-Setup-*"))
            {
                var name = Path.GetFileName(file);
                if ((name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".exe.partial", StringComparison.OrdinalIgnoreCase)) &&
                    DeleteBestEffort(file))
                {
                    deleted++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return deleted;
    }

    private static bool IsVersion(string? value) =>
        value is { Length: > 0 and <= 64 } && Version.TryParse(value.Split('-', '+')[0], out _);

    private static bool SameVersion(string left, string right) =>
        string.Equals(left.Split('+')[0], right.Split('+')[0], StringComparison.OrdinalIgnoreCase);

    private static bool DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
