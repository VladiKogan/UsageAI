using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageAI.Services;

internal sealed record ReleaseNoteSection(string Heading, IReadOnlyList<string> Bullets);

internal sealed record ReleaseNotesVersion(
    Version Version,
    string DisplayVersion,
    IReadOnlyList<ReleaseNoteSection> Sections);

internal sealed record WhatsNewSummary(
    IReadOnlyList<ReleaseNotesVersion> Releases,
    string InstalledVersion,
    bool HasOlderSkipped);

/// <summary>Reads a bounded, intentionally small subset of the bundled changelog Markdown.</summary>
internal static partial class ReleaseNotes
{
    internal const string ResourceName = "UsageAI.Changelog.md";
    internal const int MaximumInputCharacters = 131_072;
    private const int MaximumVersions = 128;
    private const int MaximumBulletsPerVersion = 32;
    private const int MaximumBulletCharacters = 1_000;
    private const int MaximumOutputCharacters = 24_000;
    private static readonly HashSet<string> AllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Added", "Changed", "Fixed", "Security",
    };

    public static IReadOnlyList<ReleaseNotesVersion> LoadBundled() =>
        LoadBundled(Assembly.GetExecutingAssembly());

    internal static IReadOnlyList<ReleaseNotesVersion> LoadBundled(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        try
        {
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null || stream.Length > MaximumInputCharacters * 4L)
            {
                return Array.Empty<ReleaseNotesVersion>();
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var markdown = reader.ReadToEnd();
            return Parse(markdown);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return Array.Empty<ReleaseNotesVersion>();
        }
    }

    internal static IReadOnlyList<ReleaseNotesVersion> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Length > MaximumInputCharacters)
        {
            return Array.Empty<ReleaseNotesVersion>();
        }

        var builders = new List<ReleaseBuilder>();
        ReleaseBuilder? current = null;
        string? section = null;
        StringBuilder? bullet = null;
        var outputCharacters = 0;

        void FinishBullet()
        {
            if (current is null || section is null || bullet is null)
            {
                bullet = null;
                return;
            }

            var text = bullet.ToString().Trim();
            if (text.Length > 0 && current.BulletCount < MaximumBulletsPerVersion &&
                outputCharacters + text.Length <= MaximumOutputCharacters)
            {
                current.Add(section, text);
                outputCharacters += text.Length;
            }
            bullet = null;
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var headingMatch = VersionHeading().Match(line);
            if (headingMatch.Success)
            {
                FinishBullet();
                section = null;
                var label = headingMatch.Groups[1].Value;
                var parsed = UpdateChecker.ParseVersion(label);
                if (parsed is null || label.Equals("Unreleased", StringComparison.OrdinalIgnoreCase) ||
                    builders.Count >= MaximumVersions)
                {
                    current = null;
                    continue;
                }

                current = new ReleaseBuilder(parsed, label.Trim().TrimStart('v', 'V'));
                builders.Add(current);
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FinishBullet();
                current = null;
                section = null;
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FinishBullet();
                var candidate = line[4..].Trim();
                section = current is not null && AllowedSections.Contains(candidate) ? candidate : null;
                continue;
            }

            if (current is null || section is null)
            {
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FinishBullet();
                var initial = line[2..].Trim();
                bullet = new StringBuilder(initial[..Math.Min(initial.Length, MaximumBulletCharacters)]);
            }
            else if (bullet is not null && rawLine.Length > 0 && char.IsWhiteSpace(rawLine[0]))
            {
                var continuation = line.Trim();
                if (continuation.Length > 0 && bullet.Length < MaximumBulletCharacters)
                {
                    bullet.Append(' ');
                    bullet.Append(continuation.AsSpan(0, Math.Min(
                        continuation.Length,
                        MaximumBulletCharacters - bullet.Length)));
                }
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                FinishBullet();
            }
        }

        FinishBullet();
        return builders
            .Where(builder => builder.Sections.Count > 0)
            .Select(builder => builder.Build())
            .OrderByDescending(release => release.Version)
            .ToArray();
    }

    internal static WhatsNewSummary ForUpgrade(
        IReadOnlyList<ReleaseNotesVersion> releases,
        string currentVersion,
        Version? previousVersion)
    {
        var current = UpdateChecker.ParseVersion(currentVersion);
        if (current is null)
        {
            return new WhatsNewSummary(Array.Empty<ReleaseNotesVersion>(), currentVersion, false);
        }

        var relevant = releases
            .Where(release => release.Version <= current &&
                (previousVersion is null ? release.Version == current : release.Version > previousVersion))
            .OrderByDescending(release => release.Version)
            .ToArray();
        return new WhatsNewSummary(relevant.Take(3).ToArray(), currentVersion, relevant.Length > 3);
    }

    [GeneratedRegex(@"^##\s+\[([^\]]+)\](?:\s+-\s+\d{4}-\d{2}-\d{2})?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionHeading();

    private sealed class ReleaseBuilder
    {
        private readonly Dictionary<string, List<string>> _sections = new(StringComparer.OrdinalIgnoreCase);

        public ReleaseBuilder(Version version, string displayVersion)
        {
            Version = version;
            DisplayVersion = displayVersion;
        }

        public Version Version { get; }

        public string DisplayVersion { get; }

        public int BulletCount { get; private set; }

        public Dictionary<string, List<string>> Sections => _sections;

        public void Add(string section, string bullet)
        {
            if (!_sections.TryGetValue(section, out var bullets))
            {
                bullets = new List<string>();
                _sections.Add(section, bullets);
            }
            bullets.Add(bullet);
            BulletCount++;
        }

        public ReleaseNotesVersion Build() => new(
            Version,
            DisplayVersion,
            _sections.Select(pair => new ReleaseNoteSection(pair.Key, pair.Value.ToArray())).ToArray());
    }
}

/// <summary>Determines once-per-upgrade state without opening any UI during background startup.</summary>
internal sealed class UpgradeNotice
{
    private readonly AppSettings _settings;

    private UpgradeNotice(AppSettings settings, WhatsNewSummary? pending)
    {
        _settings = settings;
        Pending = pending;
    }

    public WhatsNewSummary? Pending { get; private set; }

    public static UpgradeNotice Create(
        AppSettings settings,
        bool profileExisted,
        string currentVersion,
        IReadOnlyList<ReleaseNotesVersion> releases)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = UpdateChecker.ParseVersion(currentVersion);
        if (current is null)
        {
            return new UpgradeNotice(settings, null);
        }

        if (settings.LastRunVersion is null)
        {
            if (!profileExisted)
            {
                settings.LastRunVersion = currentVersion;
                settings.Save();
                return new UpgradeNotice(settings, null);
            }

            return new UpgradeNotice(settings, ReleaseNotes.ForUpgrade(releases, currentVersion, null));
        }

        var previous = UpdateChecker.ParseVersion(settings.LastRunVersion);
        if (previous is null || previous >= current)
        {
            settings.LastRunVersion = currentVersion;
            settings.Save();
            return new UpgradeNotice(settings, null);
        }

        return new UpgradeNotice(settings, ReleaseNotes.ForUpgrade(releases, currentVersion, previous));
    }

    public void MarkDisplayed(string currentVersion)
    {
        if (Pending is null)
        {
            return;
        }

        _settings.LastRunVersion = currentVersion;
        _settings.Save();
        Pending = null;
    }
}
