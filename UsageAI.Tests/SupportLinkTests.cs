using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

/// <summary>
/// The quiet ways to give back: About's Support the project card and What's New's last line.
/// Each only opens a page; nothing is fetched, counted or asked unprompted.
/// </summary>
internal static class SupportLinkTests
{
    public static Task TestSupportLinksAsync()
    {
        var opened = new List<Uri>();
        var original = Browser.Opener;
        Browser.Opener = link =>
        {
            opened.Add(link);
            return true;
        };
        try
        {
            // A new issue starts with the versions and a warning about diagnostics, and nothing else.
            var issue = ProjectLinks.NewIssue("9.9.9", "Microsoft Windows 10.0.26100 (X64)");
            True(issue.AbsoluteUri.StartsWith("https://github.com/VladiKogan/UsageAI/issues/new?body=", StringComparison.Ordinal));
            var body = Uri.UnescapeDataString(issue.Query["?body=".Length..]);
            True(body.Contains("**UsageAI version:** 9.9.9", StringComparison.Ordinal));
            True(body.Contains("**Windows:** Microsoft Windows 10.0.26100 (X64)", StringComparison.Ordinal));
            True(body.Contains("### What happened", StringComparison.Ordinal));
            True(body.Contains("### What you expected", StringComparison.Ordinal));
            True(body.Contains("check it first: it can include your account email", StringComparison.Ordinal));
            False(body.Contains('@', StringComparison.Ordinal));
            True(ProjectLinks.WindowsVersion.Length > 0);
            Equal("https://github.com/VladiKogan/UsageAI", ProjectLinks.Repository.AbsoluteUri.TrimEnd('/'));

            // About: a card of its own, its two links, and both found by the words people use.
            using (var dialog = new SettingsForm(
                       new AppSettings { IsEphemeral = true },
                       new[] { ("codex", "Codex") },
                       _ => Task.FromResult(new UpdateCheckResult(true, null)),
                       _ => Task.CompletedTask,
                       96)
                   {
                       StartPosition = FormStartPosition.Manual,
                       Location = new Point(-10_000, -10_000),
                   })
            {
                dialog.Show();
                dialog.ShowSection("About");
                Application.DoEvents();
                var links = Descendants(dialog).OfType<Button>().ToList();
                var star = links.Single(button => button.Text == "★ Star on GitHub");
                var report = links.Single(button => button.Text == "Report a problem");
                Equal("Star UsageAI on GitHub", star.AccessibleName);
                True(star.Visible && report.Visible);
                star.PerformClick();
                report.PerformClick();
                Equal(2, opened.Count);
                Equal(ProjectLinks.Repository, opened[0]);
                True(opened[1].AbsoluteUri.Contains("/issues/new?body=", StringComparison.Ordinal));
                True(Uri.UnescapeDataString(opened[1].Query).Contains(AppIdentity.Version, StringComparison.Ordinal));

                var about = Array.IndexOf(SettingsForm.PageNames, "About");
                var found = SettingsSearch.Find(dialog.SearchEntries(), SettingsForm.PageNames, "star").Single(group => group.Page == about);
                True(found.Hits.Any(hit => hit.Entry.Text == "Support the project"));
                True(found.Hits.Any(hit => hit.Entry.Text == "★ Star on GitHub"));
                True(SettingsSearch.Find(dialog.SearchEntries(), SettingsForm.PageNames, "bug")
                    .Single().Hits.Any(hit => hit.Entry.Text == "Report a problem"));

                // The window opens with the page list focused, not the search box.
                dialog.Close();
            }

            // What's New: the line sits on the left of the footer, left of the buttons and never
            // under them, in the quiet colour; it opens the repository page.
            foreach (var width in new[] { 560, 420 })
            {
                using var notes = new WhatsNewForm(new WhatsNewSummary(Array.Empty<ReleaseNotesVersion>(), "9.9.9", 0), 96)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-10_000, -10_000),
                };
                notes.Show();
                notes.ClientSize = new Size(width, notes.ClientSize.Height);
                Application.DoEvents();
                var star = Field<Button>(notes, "_star");
                var viewComplete = Field<Button>(notes, "_viewComplete");
                Equal("Enjoying UsageAI? Star it on GitHub", star.AccessibleName);
                True(star.Text.StartsWith('★'));
                Equal(Theme.Muted, star.ForeColor);
                var starRight = notes.PointToClient(star.Parent!.PointToScreen(new Point(star.Right, 0))).X;
                var buttonsLeft = notes.PointToClient(viewComplete.Parent!.PointToScreen(new Point(viewComplete.Left, 0))).X;
                True(starRight <= buttonsLeft);
                True(star.Left < 40);
                star.PerformClick();
                Equal(ProjectLinks.Repository, opened[^1]);

                // The changelog link beside it opens through the same opener.
                viewComplete.PerformClick();
                Equal(ProjectLinks.Changelog, opened[^1]);

                // Laid out again under a smaller limit left from another scale, the line takes its
                // full size rather than being held to the old limit.
                star.MaximumSize = new Size(10, 10);
                typeof(WhatsNewForm).GetMethod("ApplyScaledLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(notes, null);
                True(star.MaximumSize.Width > 10 && star.MaximumSize.Height == 32);
                using var bitmap = new Bitmap(notes.ClientSize.Width, notes.ClientSize.Height);
                notes.DrawToBitmap(bitmap, notes.ClientRectangle);
                notes.Close();
            }

            // High Contrast: the system's link colour, with a border as the cue.
            Theme.SetHighContrastProbe(static () => true);
            try
            {
                Theme.Apply(ThemeMode.Dark, 72, 90);
                using var notes = new WhatsNewForm(new WhatsNewSummary(Array.Empty<ReleaseNotesVersion>(), "9.9.9", 0), 96);
                var star = Field<Button>(notes, "_star");
                Equal(SystemColors.HotTrack, star.ForeColor);
                Equal(1, star.FlatAppearance.BorderSize);
            }
            finally
            {
                Theme.SetHighContrastProbe(null);
                Theme.Apply(ThemeMode.Dark, 72, 90);
            }
        }
        finally
        {
            Browser.Opener = original;
        }

        // Settings opens with the page list focused.
        using (var focused = new SettingsForm(new AppSettings { IsEphemeral = true }, new[] { ("codex", "Codex") }))
        {
            True(focused.ActiveControl is SectionNav);
        }

        return Task.CompletedTask;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return (T)field.GetValue(target)!;
    }

    private static void True(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Expected true: {expression}");
        }
    }

    private static void False(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        True(!condition, $"not ({expression})");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
