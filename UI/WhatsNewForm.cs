using System.Diagnostics;
using System.Globalization;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// A native, bundled release-notes window. It never fetches anything and never runs a Markdown
/// engine: the changelog is parsed into versions, sections and bullets before it gets here, and
/// this form only chooses a font and colour per already-identified span.
/// </summary>
internal sealed class WhatsNewForm : Form
{
    private static readonly Uri CompleteChangelogUrl =
        new("https://github.com/VladiKogan/UsageAI/blob/main/Changelog.md");

    private readonly WhatsNewSummary _summary;
    private readonly TableLayoutPanel _shell;
    private readonly Label _heading;
    private readonly Label _subheading;
    private readonly RichTextBox _content;
    private readonly FlowLayoutPanel _buttons;
    private readonly Button _viewComplete;
    private readonly Button _close;
    private readonly int? _dpiOverride;
    private readonly Font _bodyFont = Typography.Text(9F);
    private readonly Font _strongFont = Typography.Text(9F, FontStyle.Bold);
    private readonly Font _codeFont = Typography.Mono(8.5F, FontStyle.Regular);
    private readonly Font _versionFont = Typography.Display(13F);
    private readonly Font _dateFont = Typography.Text(8.5F);
    private readonly Font _sectionFont = Typography.Text(8.5F, FontStyle.Bold);
    private readonly Font _gapFont = Typography.Text(5F);

    public WhatsNewForm(WhatsNewSummary summary, int? dpiOverride = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _summary = summary;
        _dpiOverride = dpiOverride;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        MinimumSize = new Size(420, 360);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "What's new in UsageAI";
        var initialScale = Scale();
        ClientSize = ConstrainClientSize(new Size(initialScale[560], initialScale[520]));

        _shell = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 4,
        };
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(_shell);

        _heading = new Label
        {
            AccessibleName = "What's new heading",
            AccessibleRole = AccessibleRole.StaticText,
            Dock = DockStyle.Fill,
            Font = Typography.Display(15F),
            Text = "What's new",
            TextAlign = ContentAlignment.BottomLeft,
        };
        _subheading = new Label
        {
            Dock = DockStyle.Fill,
            Font = Typography.Text(9F),
            Text = BuildSubheading(summary),
            TextAlign = ContentAlignment.TopLeft,
        };
        _content = new RichTextBox
        {
            AccessibleName = "Release notes",
            BackColor = Theme.Night,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            Font = _bodyFont,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            TabStop = true,
        };
        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
        };
        _close = CreateButton("Got it", primary: true);
        _close.DialogResult = DialogResult.OK;
        _viewComplete = CreateButton("View full changelog", primary: false);
        _buttons.Paint += OnButtonsPaint;
        _viewComplete.Click += (_, _) => OpenCompleteChangelog();
        _buttons.Controls.Add(_close);
        _buttons.Controls.Add(_viewComplete);
        _shell.Controls.Add(_heading, 0, 0);
        _shell.Controls.Add(_subheading, 0, 1);
        _shell.Controls.Add(_content, 0, 2);
        _shell.Controls.Add(_buttons, 0, 3);
        AcceptButton = _close;
        CancelButton = _close;
        Theme.Changed += OnThemeChanged;
        // Character formatting lives in the native control, so it is written once the handle
        // exists and written again if Windows ever recreates it.
        _content.HandleCreated += OnContentHandleCreated;
        ApplyScaledLayout();
        ApplyTheme();
        RenderContent();
    }

    public static void ShowCurrent(IWin32Window? owner = null)
    {
        var summary = ReleaseNotes.ForUpgrade(
            ReleaseNotes.LoadBundled(),
            AppIdentity.Version,
            previousVersion: null);
        using var form = new WhatsNewForm(summary);
        if (owner is null) form.ShowDialog();
        else form.ShowDialog(owner);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        var selectionStart = _content.SelectionStart;
        base.OnDpiChanged(eventArgs);
        ApplyScaledLayout();
        // Bullet indents are pixel geometry, so the whole document has to be laid out again.
        RenderContent();
        Bounds = WindowBounds.FitToWorkingArea(Bounds, Screen.FromRectangle(eventArgs.SuggestedRectangle).WorkingArea);
        _content.SelectionStart = Math.Min(selectionStart, _content.TextLength);
        _content.ScrollToCaret();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);
    }

    // The notes box is created after the window, so the window's own handle creation is too early
    // to theme its scrollbar, which then stayed light on a dark window.
    private void OnContentHandleCreated(object? sender, EventArgs eventArgs)
    {
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);
        RenderContent();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _content.HandleCreated -= OnContentHandleCreated;
            _buttons.Paint -= OnButtonsPaint;
            _close.Font.Dispose();
            _viewComplete.Font.Dispose();
            _heading.Font.Dispose();
            _subheading.Font.Dispose();
            _bodyFont.Dispose();
            _strongFont.Dispose();
            _codeFont.Dispose();
            _versionFont.Dispose();
            _dateFont.Dispose();
            _sectionFont.Dispose();
            _gapFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string BuildSubheading(WhatsNewSummary summary) =>
        summary.Releases.Count > 1
            ? $"Updated to UsageAI {summary.InstalledVersion} — {summary.Releases.Count} releases since you last ran it"
            : $"Updated to UsageAI {summary.InstalledVersion}";

    /// <summary>
    /// The colour of the dot before a section's name. It is never the only cue: the name spells
    /// the category out, and Windows High Contrast flattens all four to the system foreground.
    /// </summary>
    private static Color SectionColor(string heading) => heading.ToUpperInvariant() switch
    {
        "ADDED" => Theme.Success,
        "FIXED" => Theme.Warning,
        "SECURITY" => Theme.Critical,
        _ => Theme.Signal,
    };

    /// <summary>
    /// Lays the parsed release notes out as a typed document: a version line per release, a
    /// coloured category label per section, and hanging-indented bullets whose inline code and
    /// strong spans keep their own faces.
    /// </summary>
    private void RenderContent()
    {
        if (!_content.IsHandleCreated)
        {
            // Nothing to format yet; OnContentHandleCreated renders as soon as there is.
            return;
        }

        var scale = Scale();
        _content.Clear();
        if (_summary.Releases.Count == 0)
        {
            AppendParagraph(
                new[] { (Text: $"Updated to UsageAI {_summary.InstalledVersion}.", Font: _bodyFont, Color: Theme.Text) },
                indent: 0,
                hangingIndent: 0);
            _content.SelectionStart = 0;
            _content.SelectionLength = 0;
            return;
        }

        var first = true;
        foreach (var release in _summary.Releases)
        {
            if (!first)
            {
                AppendGap(_versionFont);
            }
            first = false;

            var versionRuns = new List<(string Text, Font Font, Color Color)>
            {
                ($"UsageAI {release.DisplayVersion}", _versionFont, Theme.Text),
            };
            if (release.Date is { } date)
            {
                // Every other string in this window is English, and a right-to-left system long
                // date reverses itself against the version it sits beside, so the date is spelled
                // out the same way the changelog itself is.
                versionRuns.Add((
                    $"    {date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}",
                    _dateFont,
                    Theme.Muted));
            }
            AppendParagraph(versionRuns, indent: 0, hangingIndent: 0);

            foreach (var section in release.Sections)
            {
                AppendGap(_bodyFont);
                AppendParagraph(
                    new[]
                    {
                        (Text: "\u25CF  ", Font: _sectionFont, Color: SectionColor(section.Heading)),
                        (Text: SentenceCase(section.Heading), Font: _sectionFont, Color: Theme.Text),
                    },
                    indent: 0,
                    hangingIndent: 0);
                var firstBullet = true;
                foreach (var bullet in section.Bullets)
                {
                    if (!firstBullet)
                    {
                        AppendGap(_gapFont);
                    }
                    firstBullet = false;

                    var runs = new List<(string Text, Font Font, Color Color)>
                    {
                        ("•  ", _bodyFont, Theme.Muted),
                    };
                    foreach (var run in ReleaseNotes.SplitInline(bullet))
                    {
                        runs.Add(run.Style switch
                        {
                            ReleaseNoteRunStyle.Code => (run.Text, _codeFont, Theme.Signal),
                            ReleaseNoteRunStyle.Strong => (run.Text, _strongFont, Theme.Text),
                            _ => (run.Text, _bodyFont, Theme.Text),
                        });
                    }
                    AppendParagraph(runs, scale[6], scale[14]);
                }
            }
        }

        if (_summary.HasOlderSkipped)
        {
            AppendGap(_bodyFont);
            var releaseWord = _summary.SkippedCount == 1 ? "release is" : "releases are";
            AppendParagraph(
                new[]
                {
                    (Text: $"{_summary.SkippedCount} older {releaseWord} not shown here. " +
                        "Use View full changelog to read them.",
                        Font: _bodyFont,
                        Color: Theme.Muted),
                },
                indent: 0,
                hangingIndent: 0);
        }

        _content.SelectionStart = 0;
        _content.SelectionLength = 0;
    }

    /// <summary>
    /// A blank line of a chosen height. A RichTextBox has no paragraph spacing, so the size of the
    /// font on an empty line is the only way to separate a release from a section from a bullet.
    /// </summary>
    private void AppendGap(Font font) => AppendParagraph(
        new[] { (Text: string.Empty, Font: font, Color: Theme.Muted) },
        indent: 0,
        hangingIndent: 0);

    private void AppendParagraph(
        IReadOnlyList<(string Text, Font Font, Color Color)> runs,
        int indent,
        int hangingIndent)
    {
        var paragraphStart = _content.TextLength;
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }

            var start = _content.TextLength;
            _content.AppendText(run.Text);
            _content.Select(start, _content.TextLength - start);
            _content.SelectionFont = run.Font;
            _content.SelectionColor = run.Color;
        }

        var lineBreakStart = _content.TextLength;
        _content.AppendText("\n");
        if (lineBreakStart == paragraphStart && runs.Count > 0)
        {
            // An empty spacer paragraph: the newline itself has to carry the height.
            _content.Select(lineBreakStart, _content.TextLength - lineBreakStart);
            _content.SelectionFont = runs[0].Font;
        }

        _content.Select(paragraphStart, _content.TextLength - paragraphStart);
        _content.SelectionIndent = indent;
        _content.SelectionHangingIndent = hangingIndent;
        _content.Select(_content.TextLength, 0);
    }

    /// <summary>"ADDED" or "added" reads as "Added", the way the rest of the window is written.</summary>
    internal static string SentenceCase(string heading) =>
        heading.Length == 0
            ? heading
            : char.ToUpperInvariant(heading[0]) + heading[1..].ToLowerInvariant();

    private static Button CreateButton(string text, bool primary) => new()
    {
        AutoSize = false,
        FlatStyle = FlatStyle.Flat,
        Font = Typography.Text(9F, primary ? FontStyle.Bold : FontStyle.Regular),
        Text = text,
        UseVisualStyleBackColor = false,
    };

    /// <summary>A hairline between the notes and the buttons, now that the notes have no border.</summary>
    private void OnButtonsPaint(object? sender, PaintEventArgs eventArgs)
    {
        using var pen = new Pen(Theme.IsHighContrast ? SystemColors.WindowText : Theme.Divider);
        eventArgs.Graphics.DrawLine(pen, 0, 0, _buttons.Width, 0);
    }

    private void ApplyScaledLayout()
    {
        var scale = Scale();
        var workingArea = Screen.FromPoint(Location).WorkingArea;
        MinimumSize = new Size(
            Math.Min(scale[420], workingArea.Width),
            Math.Min(scale[360], workingArea.Height));
        _shell.Padding = scale.Pad(20, 16, 20, 12);
        _shell.RowStyles[0] = new RowStyle(SizeType.Absolute, scale[42]);
        _shell.RowStyles[1] = new RowStyle(SizeType.Absolute, scale[30]);
        _shell.RowStyles[3] = new RowStyle(SizeType.Absolute, scale[54]);
        _heading.Margin = Padding.Empty;
        _subheading.Margin = scale.Pad(0, 0, 0, 8);
        _content.Margin = Padding.Empty;
        _buttons.Padding = scale.Pad(0, 12, 0, 0);
        _close.Margin = scale.Pad(8, 0, 0, 0);
        _close.Size = new Size(Math.Max(scale[92], TextRenderer.MeasureText(_close.Text, _close.Font).Width + scale[28]), scale[32]);
        _viewComplete.Margin = Padding.Empty;
        _viewComplete.Size = new Size(TextRenderer.MeasureText(_viewComplete.Text, _viewComplete.Font).Width + scale[20], scale[32]);
    }

    private Size ConstrainClientSize(Size desired)
    {
        var area = Screen.FromPoint(Location).WorkingArea;
        var scale = Scale();
        return new Size(Math.Min(desired.Width, Math.Max(320, area.Width - scale[24])),
            Math.Min(desired.Height, Math.Max(280, area.Height - scale[24])));
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        if (!IsDisposed && !Disposing)
        {
            ApplyTheme();
            // Every run colour is baked into the document, so a palette change has to redraw it.
            var selectionStart = _content.SelectionStart;
            RenderContent();
            _content.SelectionStart = Math.Min(selectionStart, _content.TextLength);
            _content.ScrollToCaret();
        }
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    private void ApplyTheme()
    {
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        _heading.ForeColor = Theme.Text;
        _subheading.ForeColor = Theme.Muted;
        _content.BackColor = Theme.Night;
        _content.ForeColor = Theme.Text;
        _close.BackColor = Theme.Accent;
        _close.ForeColor = Theme.OnAccent;
        _close.FlatAppearance.BorderColor = Theme.Accent;
        _close.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Accent, Theme.Text, 0.88);
        // A link rather than a second button: the changelog is a way out, not a choice.
        _viewComplete.BackColor = Theme.Night;
        _viewComplete.ForeColor = Theme.IsHighContrast ? SystemColors.HotTrack : Theme.Accent;
        _viewComplete.FlatAppearance.BorderSize = Theme.IsHighContrast ? 1 : 0;
        _viewComplete.FlatAppearance.BorderColor = Theme.IsHighContrast ? SystemColors.WindowText : Theme.Night;
        // High Contrast keeps the window colour under the link: a blend there would be the system
        // text colour and hide the link text. Its border is the cue instead.
        _viewComplete.FlatAppearance.MouseOverBackColor = Theme.IsHighContrast ? SystemColors.Window : Theme.Blend(Theme.Text, Theme.Night, 0.06);
        _viewComplete.FlatAppearance.MouseDownBackColor = Theme.IsHighContrast ? SystemColors.Window : Theme.Blend(Theme.Text, Theme.Night, 0.1);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);
        Invalidate(invalidateChildren: true);
    }

    private static void OpenCompleteChangelog()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(CompleteChangelogUrl.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            PromptDialog.Ask(
                "UsageAI",
                "The changelog did not open",
                "Windows could not open the UsageAI changelog in a browser.",
                "OK",
                declineText: null,
                PromptKind.Warning);
        }
    }
}
