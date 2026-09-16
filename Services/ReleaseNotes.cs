using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageAI.Services;

internal sealed record ReleaseNoteSection(string Heading, IReadOnlyList<string> Bullets);

internal sealed record ReleaseNotesVersion(
    Version Version,
    string DisplayVersion,
    DateOnly? Date,
    IReadOnlyList<ReleaseNoteSection> Sections);

/// <summary>How a span of bullet text was marked up in the changelog Markdown.</summary>
internal enum ReleaseNoteRunStyle
{
    Normal,
    Strong,
    Code,
}

/// <summary>One contiguous span of a bullet that shares a single visual treatment.</summary>
internal readonly record struct ReleaseNoteRun(string Text, ReleaseNoteRunStyle Style);

internal sealed record WhatsNewSummary(
    IReadOnlyList<ReleaseNotesVersion> Releases,
    string InstalledVersion,
    int SkippedCount)
{
    /// <summary>True when releases were dropped to keep the window bounded.</summary>
    public bool HasOlderSkipped => SkippedCount > 0;
}

/// <summary>Reads a bounded, intentionally small subset of the bundled changelog Markdown.</summary>
internal static partial class ReleaseNotes
{
    internal const string ResourceName = "UsageAI.Changelog.md";
    internal const int MaximumInputCharacters = 131_072;
    private const int MaximumVersions = 128;
    private const int MaximumShownReleases = 3;
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

                current = new ReleaseBuilder(
                    parsed,
                    label.Trim().TrimStart('v', 'V'),
                    ParseDate(headingMatch.Groups[2].Value));
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
            return new WhatsNewSummary(Array.Empty<ReleaseNotesVersion>(), currentVersion, 0);
        }

        var relevant = releases
            .Where(release => release.Version <= current &&
                (previousVersion is null ? release.Version == current : release.Version > previousVersion))
            .OrderByDescending(release => release.Version)
            .ToArray();
        var shown = relevant.Take(MaximumShownReleases).ToArray();
        return new WhatsNewSummary(shown, currentVersion, relevant.Length - shown.Length);
    }

    /// <summary>
    /// Splits one bullet into the inline spans the window renders differently. Only the two
    /// constructs the changelog actually uses are recognised — <c>`code`</c> and <c>**strong**</c> —
    /// and an unpaired or empty delimiter stays literal text, so this can never consume more of the
    /// bullet than the markup it matched.
    /// </summary>
    internal static IReadOnlyList<ReleaseNoteRun> SplitInline(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<ReleaseNoteRun>();
        }

        var runs = new List<ReleaseNoteRun>();
        var literal = new StringBuilder();
        var index = 0;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                runs.Add(new ReleaseNoteRun(literal.ToString(), ReleaseNoteRunStyle.Normal));
                literal.Clear();
            }
        }

        while (index < text.Length)
        {
            var isCode = text[index] == '`';
            var isStrong = !isCode && text[index] == '*' &&
                index + 1 < text.Length && text[index + 1] == '*';
            if (!isCode && !isStrong)
            {
                literal.Append(text[index++]);
                continue;
            }

            var delimiter = isCode ? "`" : "**";
            var contentStart = index + delimiter.Length;
            var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
            if (close <= contentStart)
            {
                literal.Append(text[index++]);
                continue;
            }

            FlushLiteral();
            runs.Add(new ReleaseNoteRun(
                text[contentStart..close],
                isCode ? ReleaseNoteRunStyle.Code : ReleaseNoteRunStyle.Strong));
            index = close + delimiter.Length;
        }

        FlushLiteral();
        return runs;
    }

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    [GeneratedRegex(@"^##\s+\[([^\]]+)\](?:\s+-\s+(\d{4}-\d{2}-\d{2}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionHeading();

    private sealed class ReleaseBuilder
    {
        private readonly Dictionary<string, List<string>> _sections = new(StringComparer.OrdinalIgnoreCase);

        public ReleaseBuilder(Version version, string displayVersion, DateOnly? date)
        {
            Version = version;
            DisplayVersion = displayVersion;
            Date = date;
        }

        public Version Version { get; }

        public string DisplayVersion { get; }

        public DateOnly? Date { get; }

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
            Date,
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
