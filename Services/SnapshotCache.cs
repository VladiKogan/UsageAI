using System.Text.Json;
using System.Text.Json.Serialization;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>
/// Remembers the last reading for each provider so the popup shows real numbers immediately
/// at start-up instead of an empty shell. Cached values are always presented as stale until
/// the first live refresh completes.
/// </summary>
internal static class SnapshotCache
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(2);

    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<UsageSnapshot> Load()
    {
        try
        {
            var path = AppPaths.SnapshotCacheFile;
            if (!File.Exists(path))
            {
                return Array.Empty<UsageSnapshot>();
            }

            var json = SecureLocalFile.ReadAllText(path, maxCharacters: 262_144);
            var snapshots = JsonSerializer.Deserialize<UsageSnapshot[]>(json, CacheOptions);
            if (snapshots is null)
            {
                return Array.Empty<UsageSnapshot>();
            }

            var cutoff = DateTimeOffset.Now - MaximumAge;
            return snapshots
                .Where(snapshot => snapshot is not null && snapshot.FetchedAt >= cutoff)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or JsonException
                                              or NotSupportedException)
        {
            // A stale-data convenience is never worth an error path.
            return Array.Empty<UsageSnapshot>();
        }
    }

    public static void Save(IReadOnlyList<UsageSnapshot> snapshots)
    {
        try
        {
            AppPaths.WriteAllTextAtomic(
                AppPaths.SnapshotCacheFile,
                JsonSerializer.Serialize(snapshots, CacheOptions));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            // The next refresh will try again.
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(AppPaths.SnapshotCacheFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing further to do.
        }
    }
}
