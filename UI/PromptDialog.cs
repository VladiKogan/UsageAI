using System.Drawing.Drawing2D;

namespace UsageAI.UI;

internal enum PromptKind
{
    /// <summary>A choice to make, such as installing an update: marked with the app's logo.</summary>
    Question,

    /// <summary>Something went wrong: marked with a warning sign, as the old message box was.</summary>
    Warning,
}

/// <summary>
/// A question or notice — install an update, open the release page when that fails — in the
/// app's own palette rather than a system message box, which ignored the theme and read as a
/// different program. Escape and the close box answer no.
/// </summary>
internal sealed class PromptDialog : Form
{
    private readonly Label _title;
    private readonly Label _body;
    private readonly Button _accept;
    private readonly Button? _decline;
    private readonly int? _dpiOverride;
    private readonly PromptKind _kind;
    private readonly Font _bodyFont = Typography.Text(9F);
    private readonly Font _titleFont = Typography.Display(12F);
    private readonly Font _primaryFont = Typography.Text(9F, FontStyle.Bold);

    /// <param name="declineText">The second button's label, or null for a notice with one button.</param>
    public PromptDialog(
        string caption,
        string title,
        string body,
        string acceptText,
        string? declineText,
        PromptKind kind = PromptKind.Question,
        int? dpiOverride = null)
    {
        _dpiOverride = dpiOverride;
        _kind = kind;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        Font = _bodyFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = caption;
        AppIcon.Apply(this);

        _title = new Label
        {
            AutoSize = false,
            Font = _titleFont,
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _body = new Label
        {
            AutoSize = false,
            Text = body,
        };
        _accept = CreateButton(acceptText, primary: true);
        _accept.Font = _primaryFont;
        _accept.DialogResult = DialogResult.Yes;
        Controls.AddRange(new Control[] { _title, _body, _accept });
        if (declineText is not null)
        {
            _decline = CreateButton(declineText, primary: false);
            _decline.DialogResult = DialogResult.No;
            Controls.Add(_decline);
        }

        AcceptButton = _accept;
        CancelButton = (IButtonControl?)_decline ?? _accept;
        Theme.Changed += OnThemeChanged;
        ApplyLayout();
        ApplyTheme();
    }

    internal Button AcceptControl => _accept;

    internal Button? DeclineControl => _decline;

    internal PromptKind Kind => _kind;

    /// <summary>Asks the question and returns whether the user chose the accept button.</summary>
    public static bool Ask(
        string caption,
        string title,
        string body,
        string acceptText,
        string? declineText,
        PromptKind kind = PromptKind.Question)
    {
        using var dialog = new PromptDialog(caption, title, body, acceptText, declineText, kind);
        return dialog.ShowDialog() == DialogResult.Yes;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // The constructor measured at the start-up DPI. By now every handle exists on the monitor
        // the dialog opens on and WinForms has rescaled the fonts, so it is measured again and
        // centred at its real size.
        ApplyLayout();
        CenterToScreen();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyLayout();
        Bounds = WindowBounds.FitToWorkingArea(Bounds, Screen.FromRectangle(e.SuggestedRectangle).WorkingArea);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = Scale();
        var mark = new RectangleF(scale[24], scale[24], scale.Exact(36), scale.Exact(36));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_kind == PromptKind.Warning)
        {
            PaintWarningMark(e.Graphics, mark, scale);
        }
        else
        {
            PaintLogoMark(e.Graphics, mark, scale);
        }
    }

    /// <summary>
    /// The UsageAI mark — a ring holding a level — so a question is recognisably the app's. High
    /// Contrast draws it in the system text colour.
    /// </summary>
    private static void PaintLogoMark(Graphics graphics, RectangleF bounds, LayoutScale scale)
    {
        var stroke = scale.Exact(4);
        var ringColor = Theme.IsHighContrast ? SystemColors.WindowText : Theme.Text;
        var levelColor = Theme.IsHighContrast ? SystemColors.WindowText : Theme.Accent;
        var inner = RectangleF.Inflate(bounds, -stroke * 1.75F, -stroke * 1.75F);
        using (var clip = new GraphicsPath())
        {
            clip.AddEllipse(inner);
            var state = graphics.Save();
            graphics.SetClip(clip);
            using var level = new SolidBrush(levelColor);
            graphics.FillRectangle(level, inner.Left, inner.Top + inner.Height * 0.42F, inner.Width, inner.Height);
            graphics.Restore(state);
        }

        using var ring = new Pen(ringColor, stroke);
        graphics.DrawEllipse(ring, RectangleF.Inflate(bounds, -stroke / 2F, -stroke / 2F));
    }

    /// <summary>A warning sign: a ring with an exclamation mark, a symbol rather than colour alone.</summary>
    private static void PaintWarningMark(Graphics graphics, RectangleF bounds, LayoutScale scale)
    {
        var color = Theme.IsHighContrast ? SystemColors.WindowText : Theme.WarningText;
        var stroke = scale.Exact(3.5F);
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawEllipse(pen, RectangleF.Inflate(bounds, -stroke / 2F, -stroke / 2F));
        var centerX = bounds.Left + bounds.Width / 2F;
        graphics.DrawLine(pen, centerX, bounds.Top + bounds.Height * 0.28F, centerX, bounds.Top + bounds.Height * 0.56F);
        using var dot = new SolidBrush(color);
        var dotSize = stroke * 1.2F;
        graphics.FillEllipse(dot, centerX - dotSize / 2F, bounds.Top + bounds.Height * 0.70F - dotSize / 2F, dotSize, dotSize);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _bodyFont.Dispose();
            _titleFont.Dispose();
            _primaryFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Sizes everything for the dialog's DPI, measuring text with the controls' own fonts, which
    /// WinForms rescales for the monitor the dialog is on.
    /// </summary>
    private void ApplyLayout()
    {
        var scale = Scale();
        var width = scale[440];
        var textLeft = scale[76];
        var textWidth = width - textLeft - scale[24];
        var titleSize = TextRenderer.MeasureText(_title.Text, _title.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak);
        _title.Bounds = new Rectangle(textLeft, scale[22], textWidth, Math.Max(scale[36], titleSize.Height));
        var bodySize = TextRenderer.MeasureText(_body.Text, _body.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak);
        _body.Bounds = new Rectangle(textLeft, _title.Bottom + scale[4], textWidth, bodySize.Height + scale[4]);
        var buttonTop = _body.Bottom + scale[24];
        var buttonHeight = scale[32];
        _accept.Size = new Size(Math.Max(scale[120], TextRenderer.MeasureText(_accept.Text, _accept.Font).Width + scale[28]), buttonHeight);
        _accept.Location = new Point(width - scale[24] - _accept.Width, buttonTop);
        if (_decline is not null)
        {
            _decline.Size = new Size(Math.Max(scale[96], TextRenderer.MeasureText(_decline.Text, _decline.Font).Width + scale[28]), buttonHeight);
            _decline.Location = new Point(_accept.Left - scale[8] - _decline.Width, buttonTop);
        }

        ClientSize = new Size(width, buttonTop + buttonHeight + scale[20]);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && !Disposing)
        {
            ApplyLayout();
            ApplyTheme();
        }
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        _title.ForeColor = Theme.Text;
        _body.ForeColor = Theme.TextSecondary;
        StyleButton(_accept, primary: true);
        if (_decline is not null)
        {
            StyleButton(_decline, primary: false);
        }

        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
        Invalidate(invalidateChildren: true);
    }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button { Text = text };
        StyleButton(button, primary);
        return button;
    }

    private static void StyleButton(Button button, bool primary)
    {
        if (Theme.IsHighContrast)
        {
            // System buttons draw the user's own High Contrast colours and focus cues.
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Theme.Accent : Theme.SurfaceRaised;
        button.ForeColor = primary ? Theme.OnAccent : Theme.Text;
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Hairline;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Theme.Blend(Theme.Accent, Theme.Text, 0.88)
            : Theme.Blend(Theme.Text, Theme.SurfaceRaised, 0.06);
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
}
