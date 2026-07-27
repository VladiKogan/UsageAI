using System.Globalization;
using System.Text;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>One recorded observation of a single metric.</summary>
internal sealed record UsageSample(DateTimeOffset At, string ProviderId, string MetricKey, int UsedPercent);

/// <summary>
/// Appends metered usage to a local JSON-lines file so the dashboard can draw trends and
/// project exhaustion. The file never leaves the machine and holds no account identifiers
/// beyond the provider id.
/// </summary>
internal static class UsageHistoryStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private const long PruneAboveBytes = 512 * 1024;
    private const long MaxReadBytes = 4 * 1024 * 1024;
    private const int MaxSamplesPerAppend = 64;

    public static void Append(IReadOnlyList<UsageSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        try
        {
            AppPaths.EnsureDirectory();
            var builder = new StringBuilder();
            foreach (var sample in samples.Take(MaxSamplesPerAppend))
            {
                builder.Append("{\"t\":")
                    .Append(JsonSerializer.Serialize(sample.At.ToString("O", CultureInfo.InvariantCulture)))
                    .Append(",\"p\":")
                    .Append(JsonSerializer.Serialize(sample.ProviderId))
                    .Append(",\"m\":")
                    .Append(JsonSerializer.Serialize(sample.MetricKey))
                    .Append(",\"u\":")
                    .Append(sample.UsedPercent.ToString(CultureInfo.InvariantCulture))
                    .Append('}')
                    .Append('\n');
            }

            File.AppendAllText(AppPaths.HistoryFile, builder.ToString());
            PruneIfLarge();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // History is optional; losing a sample must not affect the live reading.
        }
    }

    /// <summary>Samples newer than <paramref name="window"/>, oldest first.</summary>
    public static IReadOnlyList<UsageSample> Load(TimeSpan window)
    {
        try
        {
            var path = AppPaths.HistoryFile;
            if (!File.Exists(path) || new FileInfo(path).Length > MaxReadBytes)
            {
                return Array.Empty<UsageSample>();
            }

            var cutoff = DateTimeOffset.Now - window;
            var samples = new List<UsageSample>();
            foreach (var line in File.ReadLines(path))
            {
                if (TryParse(line) is { } sample && sample.At >= cutoff)
                {
                    samples.Add(sample);
                }
            }

            samples.Sort((left, right) => left.At.CompareTo(right.At));
            return samples;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<UsageSample>();
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(AppPaths.HistoryFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the next prune will trim the file instead.
        }
    }

    /// <summary>Extracts the metered samples worth recording from a snapshot.</summary>
    public static IReadOnlyList<UsageSample> SamplesFrom(UsageSnapshot snapshot) => snapshot.Metrics
        .Where(metric => metric.HasQuota)
        .Select(metric => new UsageSample(
            snapshot.FetchedAt,
            snapshot.ProviderId,
            metric.Key,
            metric.UsedPercent!.Value))
        .ToArray();

    private static void PruneIfLarge()
    {
        var path = AppPaths.HistoryFile;
        if (!File.Exists(path) || new FileInfo(path).Length <= PruneAboveBytes)
        {
            return;
        }

        var cutoff = DateTimeOffset.Now - Retention;
        var kept = new StringBuilder();
        foreach (var line in File.ReadLines(path))
        {
            if (TryParse(line) is { } sample && sample.At >= cutoff)
            {
                kept.Append(line).Append('\n');
            }
        }

        AppPaths.WriteAllTextAtomic(path, kept.ToString());
    }

    private static UsageSample? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("t", out var at) ||
                !root.TryGetProperty("p", out var provider) ||
                !root.TryGetProperty("m", out var metric) ||
                !root.TryGetProperty("u", out var used) ||
                !at.TryGetDateTimeOffset(out var timestamp) ||
                !used.TryGetInt32(out var usedPercent))
            {
                return null;
            }

            var providerId = provider.GetString();
            var metricKey = metric.GetString();
            if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(metricKey))
            {
                return null;
            }

            return new UsageSample(timestamp, providerId, metricKey, Math.Clamp(usedPercent, 0, 100));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
