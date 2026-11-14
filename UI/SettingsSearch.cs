namespace UsageAI.UI;

/// <summary>
/// One thing Settings' search can find: a setting's label, a card heading, or an entry in one of
/// its lists (a provider, an account), with the words people use for it that its label does not.
/// </summary>
/// <param name="Page">The page it is on, as an index into the page names.</param>
/// <param name="Text">What the result shows, and what the search matches first.</param>
/// <param name="Target">The control choosing it moves to.</param>
/// <param name="Keywords">Other words that find it: "dark" finds the theme, "login" the accounts.</param>
/// <param name="ListIndex">For an entry in a list, where it was when the results were found; otherwise -1.</param>
/// <param name="ListKey">
/// For an entry in a list, the provider id or account key it stands for, which choosing it looks
/// up again: the list can be rebuilt, and reordered, between finding it and choosing it.
/// </param>
internal sealed record SettingsSearchEntry(
    int Page,
    string Text,
    Control Target,
    IReadOnlyList<string> Keywords,
    int ListIndex = -1,
    string? ListKey = null);

/// <summary>A found entry, with where the search text sits in its label, if it does.</summary>
internal sealed record SettingsSearchHit(SettingsSearchEntry Entry, int MatchStart, int MatchLength);

/// <summary>
/// One page's results: its heading, the hits it shows, and how many more it holds, which
/// "+N more" offers. A page whose own name matches is listed even with no setting matching.
/// </summary>
internal sealed record SettingsSearchGroup(
    int Page,
    string PageName,
    int NameMatchStart,
    int NameMatchLength,
    IReadOnlyList<SettingsSearchHit> Hits,
    int More);

/// <summary>
/// Settings' search, modelled on the Claude desktop app's: results grouped under their page, a
/// few to a page, matched case-insensitively against what each setting is called — never its
/// longer hint, which would match nearly every word — and against a short table of the words
/// people use instead.
/// </summary>
internal static class SettingsSearch
{
    /// <summary>How many results a page shows before "+N more".</summary>
    internal const int PerPage = 4;

    /// <summary>
    /// The words a setting is looked for by that its label does not contain, keyed by that label.
    /// Kept short and literal; a new setting is found by its own label without an entry here.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> KeywordsByLabel =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Theme"] = new[] { "dark", "light", "mode", "appearance" },
            ["Mode"] = new[] { "dark", "light", "theme", "system" },
            ["Colours"] = new[] { "color", "colors", "red", "yellow", "amber", "orange", "warning", "critical", "threshold" },
            ["Accounts"] = new[] { "login", "logins", "profile", "sign in", "sign-in", "email", "folder" },
            ["Notifications"] = new[] { "notify", "notification", "alert", "balloon", "toast" },
            ["Show tray notifications"] = new[] { "notify", "notification", "pause", "mute", "quiet" },
            ["Start with Windows"] = new[] { "startup", "boot", "sign in", "autostart", "launch" },
            ["UsageAI"] = new[] { "version", "update", "updates", "about" },
            ["Check for updates"] = new[] { "update", "upgrade", "version", "release" },
            ["What's new"] = new[] { "changelog", "release notes", "changes" },
            ["Show quota as"] = new[] { "remaining", "left", "used", "percent" },
            ["Shows"] = new[] { "tray", "icon", "tank", "all connected", "automatic" },
            ["Global hotkey (Win+Alt+U)"] = new[] { "shortcut", "keyboard", "key" },
            ["Refresh every (minutes)"] = new[] { "interval", "poll", "polling", "frequency", "how often" },
            ["Shown providers"] = new[] { "order", "reorder", "hide", "show", "provider" },
            ["Popup rings"] = new[] { "model group", "rings", "gemini models" },
            ["Provider status"] = new[] { "incident", "incidents", "outage", "status page" },
            ["Delete recorded history"] = new[] { "clear", "erase", "remove", "privacy" },
            ["Record usage history on this machine"] = new[] { "privacy", "data", "store" },
            ["Show burn-rate forecast"] = new[] { "prediction", "estimate", "run out" },
            ["Dashboard rows"] = new[] { "metered", "important", "metrics", "table" },
            ["Find logins in my user folder automatically"] = new[] { "scan", "discover", "detect" },
            ["Support the project"] = new[] { "star", "github", "feedback", "bug", "issue", "help" },
            ["★ Star on GitHub"] = new[] { "star", "github", "support", "like" },
            ["Report a problem"] = new[] { "bug", "issue", "feedback", "crash", "github", "support" },
        };

    /// <summary>The keywords for a label, or none.</summary>
    internal static IReadOnlyList<string> KeywordsFor(string label) =>
        KeywordsByLabel.TryGetValue(label, out var keywords) ? keywords : Array.Empty<string>();

    /// <summary>
    /// Finds <paramref name="query"/> in <paramref name="entries"/>, grouped by page in page
    /// order. A page in <paramref name="expanded"/> lists every hit instead of
    /// <see cref="PerPage"/>. Within a page, labels that start with the text come first, then
    /// labels that contain it, then keyword hits, each in the order the page shows them.
    /// </summary>
    internal static IReadOnlyList<SettingsSearchGroup> Find(
        IReadOnlyList<SettingsSearchEntry> entries,
        IReadOnlyList<string> pageNames,
        string query,
        IReadOnlySet<int>? expanded = null)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return Array.Empty<SettingsSearchGroup>();
        }

        var groups = new List<SettingsSearchGroup>();
        for (var page = 0; page < pageNames.Count; page++)
        {
            var hits = entries
                .Select((entry, order) => (entry, order))
                .Where(item => item.entry.Page == page)
                .Select(item => (item.order, Hit: Match(item.entry, text)))
                .Where(item => item.Hit is not null)
                .OrderBy(item => Rank(item.Hit!))
                .ThenBy(item => item.order)
                .Select(item => item.Hit!)
                .GroupBy(hit => hit.Entry.Text, StringComparer.OrdinalIgnoreCase)
                .Select(same => same.First())
                .ToList();
            var nameAt = pageNames[page].IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (hits.Count == 0 && nameAt < 0)
            {
                continue;
            }

            var shown = expanded?.Contains(page) == true ? hits.Count : Math.Min(PerPage, hits.Count);
            groups.Add(new SettingsSearchGroup(
                page,
                pageNames[page],
                nameAt,
                nameAt < 0 ? 0 : text.Length,
                hits.Take(shown).ToArray(),
                hits.Count - shown));
        }

        return groups;
    }

    private static SettingsSearchHit? Match(SettingsSearchEntry entry, string text)
    {
        var at = entry.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return new SettingsSearchHit(entry, at, text.Length);
        }

        // A keyword is a whole word people use, so a fragment of one counts only from its start:
        // "not" finds "notify", while "o" finds nothing it was not typed for.
        return entry.Keywords.Any(keyword => keyword.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                (text.Length > keyword.Length && text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)))
            ? new SettingsSearchHit(entry, -1, 0)
            : null;
    }

    private static int Rank(SettingsSearchHit hit) => hit.MatchStart switch
    {
        0 => 0,
        > 0 => 1,
        _ => 2,
    };
}
