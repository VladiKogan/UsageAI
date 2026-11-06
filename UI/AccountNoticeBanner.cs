using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>What the user chose on an account banner.</summary>
internal sealed class AccountNoticeEventArgs : EventArgs
{
    public AccountNoticeEventArgs(AccountNotice notice, bool primary)
    {
        Notice = notice;
        Primary = primary;
    }

    public AccountNotice Notice { get; }

    /// <summary>True for the first button (Keep all, Show, Sign in again…), false for the second.</summary>
    public bool Primary { get; }
}

/// <summary>
/// The popup's account banner: one notice at a time — several logins found, a new login found, or
/// a folder now signed in to someone else — with its two answers. It is a banner rather than a
/// toast, so an account never appears in the tiles before the user has seen why.
/// </summary>
internal sealed class AccountNoticeBanner : TableLayoutPanel
{
    public const int HeightBaseline = 70;

    private readonly Label _message;
    private readonly Button _primary;
    private readonly Button _secondary;
    private readonly int? _dpiOverride;
    private AccountNotice? _notice;

    public AccountNoticeBanner(int? dpiOverride)
    {
        _dpiOverride = dpiOverride;
        var scale = Scale();
        ColumnCount = 3;
        RowCount = 2;
        Margin = Padding.Empty;
        Padding = scale.Pad(10, 6, 8, 6);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AccessibleRole = AccessibleRole.Alert;

        _message = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Typography.Text(8.6F),
            Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };
        Controls.Add(_message, 0, 0);
        SetColumnSpan(_message, 3);

        _primary = CreateButton(scale);
        _primary.Click += (_, _) => Raise(primary: true);
        _secondary = CreateButton(scale);
        _secondary.Click += (_, _) => Raise(primary: false);
        Controls.Add(_secondary, 1, 1);
        Controls.Add(_primary, 2, 1);
        ApplyTheme();
    }

    public event EventHandler<AccountNoticeEventArgs>? Answered;

    public AccountNotice? Notice => _notice;

    /// <summary>Shows the first of <paramref name="notices"/>, or nothing.</summary>
    public void SetNotices(IReadOnlyList<AccountNotice> notices)
    {
        _notice = notices.Count > 0 ? notices[0] : null;
        if (_notice is not { } notice)
        {
            return;
        }

        var more = notices.Count > 1 ? $"  (+{notices.Count - 1} more)" : string.Empty;
        _message.Text = notice.Message + more;
        AccessibleName = notice.Message;
        (_primary.Text, _secondary.Text) = notice.Kind switch
        {
            AccountNoticeKind.FoundSeveral => ("Keep all", "Choose…"),
            AccountNoticeKind.NewAccount => ("Show", "Ignore"),
            _ => ("Sign the old account in again…", "Dismiss"),
        };
    }

    public void ApplyTheme()
    {
        BackColor = Theme.IsHighContrast ? SystemColors.Window : Theme.SurfaceRaised;
        _message.ForeColor = Theme.IsHighContrast ? SystemColors.WindowText : Theme.Text;
        StyleButton(_primary, primary: true);
        StyleButton(_secondary, primary: false);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var scale = Scale();
        using var pen = new Pen(Theme.IsHighContrast ? SystemColors.WindowText : Theme.Accent, Math.Max(1, scale[1]));
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void Raise(bool primary)
    {
        if (_notice is { } notice)
        {
            Answered?.Invoke(this, new AccountNoticeEventArgs(notice, primary));
        }
    }

    private static Button CreateButton(LayoutScale scale) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlatStyle = FlatStyle.Flat,
        Font = Typography.Text(8.4F),
        Margin = scale.Pad(6, 2, 0, 0),
        MinimumSize = new Size(scale[64], scale[26]),
        Padding = scale.Pad(6, 0, 6, 0),
        UseMnemonic = false,
    };

    private static void StyleButton(Button button, bool primary)
    {
        if (Theme.IsHighContrast)
        {
            button.FlatStyle = FlatStyle.System;
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Theme.Accent : Theme.Surface;
        button.ForeColor = primary ? Theme.OnAccent : Theme.Text;
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Blend(Theme.Text, Theme.Surface, 0.25);
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
}
