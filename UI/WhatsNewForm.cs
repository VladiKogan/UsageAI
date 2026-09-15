using System.Diagnostics;
using System.Globalization;
using System.Text;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>A native, bundled release-notes window with no network or Markdown rendering.</summary>
internal sealed class WhatsNewForm : Form
{
    private static readonly Uri CompleteChangelogUrl =
        new("https://github.com/VladiKogan/UsageAI/blob/main/Changelog.md");

    private readonly TableLayoutPanel _shell;
    private readonly Label _heading;
    private readonly Label _subheading;
    private readonly RichTextBox _content;
    private readonly FlowLayoutPanel _buttons;
    private readonly Button _viewComplete;
    private readonly Button _close;
    private readonly int? _dpiOverride;

    public WhatsNewForm(WhatsNewSummary summary, int? dpiOverride = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
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
            Text = "What changed",
            TextAlign = ContentAlignment.BottomLeft,
        };
        _subheading = new Label
        {
            Dock = DockStyle.Fill,
            Font = Typography.Text(9F),
            Text = $"Updated to UsageAI {summary.InstalledVersion}",
            TextAlign = ContentAlignment.TopLeft,
        };
        _content = new RichTextBox
        {
            AccessibleName = "Release notes",
            BackColor = Theme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            Font = Typography.Text(9F),
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            TabStop = true,
            Text = BuildText(summary),
        };
        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
        };
        _close = CreateButton("Close", primary: true);
        _close.DialogResult = DialogResult.OK;
        _viewComplete = CreateButton("View complete changelog", primary: false);
        _viewComplete.AutoSize = true;
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
        ApplyScaledLayout();
        ApplyTheme();
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
        Bounds = FitToWorkingArea(Bounds, Screen.FromRectangle(eventArgs.SuggestedRectangle).WorkingArea);
        _content.SelectionStart = Math.Min(selectionStart, _content.TextLength);
        _content.ScrollToCaret();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        WindowThemeHelpers.ApplyDarkScrollbar(_content, Theme.IsDark && !Theme.IsHighContrast);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _heading.Font.Dispose();
            _subheading.Font.Dispose();
            _content.Font.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string BuildText(WhatsNewSummary summary)
    {
        if (summary.Releases.Count == 0)
        {
            return $"Updated to UsageAI {summary.InstalledVersion}.";
        }

        var text = new StringBuilder();
        foreach (var release in summary.Releases)
        {
            if (text.Length > 0) text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"VERSION {release.DisplayVersion}");
            foreach (var section in release.Sections)
            {
                text.AppendLine();
                text.AppendLine(section.Heading.ToUpperInvariant());
                foreach (var bullet in section.Bullets)
                {
                    text.Append("• ");
                    text.AppendLine(bullet);
                }
            }
        }

        if (summary.HasOlderSkipped)
        {
            text.AppendLine();
            text.Append("Older skipped releases are available in the complete changelog.");
        }
        return text.ToString().TrimEnd();
    }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            AutoSize = false,
            BackColor = primary ? Theme.Accent : Theme.SurfaceRaised,
            FlatStyle = FlatStyle.Flat,
            Font = Typography.Text(8.5F, FontStyle.Bold),
            ForeColor = primary ? Theme.OnAccent : Theme.Text,
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Hairline;
        return button;
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
        _close.Size = new Size(scale[92], scale[32]);
        _viewComplete.Margin = scale.Pad(8, 0, 0, 0);
        _viewComplete.Size = new Size(scale[168], scale[32]);
    }

    private Size ConstrainClientSize(Size desired)
    {
        var area = Screen.FromPoint(Location).WorkingArea;
        var scale = Scale();
        return new Size(Math.Min(desired.Width, Math.Max(320, area.Width - scale[24])),
            Math.Min(desired.Height, Math.Max(280, area.Height - scale[24])));
    }

    private static Rectangle FitToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Min(bounds.Width, workingArea.Width);
        var height = Math.Min(bounds.Height, workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        var y = Math.Clamp(bounds.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        if (!IsDisposed && !Disposing) ApplyTheme();
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);

    private void ApplyTheme()
    {
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        _heading.ForeColor = Theme.Text;
        _subheading.ForeColor = Theme.Muted;
        _content.BackColor = Theme.Surface;
        _content.ForeColor = Theme.Text;
        _close.BackColor = Theme.Accent;
        _close.ForeColor = Theme.OnAccent;
        _close.FlatAppearance.BorderColor = Theme.Accent;
        _viewComplete.BackColor = Theme.SurfaceRaised;
        _viewComplete.ForeColor = Theme.Text;
        _viewComplete.FlatAppearance.BorderColor = Theme.Hairline;
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
            MessageBox.Show("Windows could not open the UsageAI changelog.", "UsageAI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
