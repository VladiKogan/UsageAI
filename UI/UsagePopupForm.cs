using UsageAI.Models;

namespace UsageAI.UI;

internal sealed class UsagePopupForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _planLabel;
    private readonly Label _statusLabel;
    private readonly Label _creditLabel;
    private readonly QuotaMeterControl _sessionMeter;
    private readonly QuotaMeterControl _weeklyMeter;
    private readonly Button _refreshButton;
    private bool _allowClose;

    public event EventHandler? RefreshRequested;

    public UsagePopupForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Night;
        ClientSize = new Size(378, 302);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "UsagePopup";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "UsageAI";
        TopMost = true;

        var shell = new TableLayoutPanel
        {
            BackColor = Theme.Night,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 14),
            RowCount = 5,
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        var header = new Panel { Dock = DockStyle.Fill };
        _titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Cascadia Mono", 11F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.Text,
            Location = new Point(0, 0),
            Text = "CODEX // LIMITS",
        };
        _planLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.Signal,
            Location = new Point(0, 23),
            Text = "CONNECTING",
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Right,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Theme.Muted,
            Size = new Size(144, 20),
            Text = "Reading Codex…",
            TextAlign = ContentAlignment.TopRight,
        };
        header.Controls.Add(_titleLabel);
        header.Controls.Add(_planLabel);
        header.Controls.Add(_statusLabel);
        shell.Controls.Add(header, 0, 0);

        _sessionMeter = new QuotaMeterControl { Dock = DockStyle.Fill, EmptyName = "5-hour" };
        _weeklyMeter = new QuotaMeterControl { Dock = DockStyle.Fill, EmptyName = "Weekly" };
        shell.Controls.Add(_sessionMeter, 0, 1);
        shell.Controls.Add(_weeklyMeter, 0, 2);

        var divider = new Panel { BackColor = Theme.Hairline, Dock = DockStyle.Fill };
        shell.Controls.Add(divider, 0, 3);

        var footer = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _creditLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Theme.Muted,
            Margin = new Padding(6, 8, 8, 0),
            Text = "Uses your existing Codex login",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _refreshButton = new Button
        {
            BackColor = Theme.SurfaceRaised,
            Cursor = Cursors.Hand,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.Text,
            Margin = new Padding(0, 9, 0, 6),
            TabIndex = 0,
            Text = "Refresh",
            UseVisualStyleBackColor = false,
        };
        _refreshButton.FlatAppearance.BorderColor = Theme.Hairline;
        _refreshButton.FlatAppearance.MouseOverBackColor = Theme.Surface;
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        footer.Controls.Add(_creditLabel, 0, 0);
        footer.Controls.Add(_refreshButton, 1, 0);
        shell.Controls.Add(footer, 0, 4);

        Deactivate += (_, _) => Hide();
        KeyPreview = true;
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
    }

    public void SetLoading(string providerName)
    {
        SetProvider(providerName);
        _refreshButton.Enabled = false;
        _refreshButton.Text = "Reading…";
        _statusLabel.ForeColor = Theme.Muted;
        _statusLabel.Text = $"Reading {ShortProviderName(providerName)}…";
    }

    public void SetSnapshot(UsageSnapshot snapshot)
    {
        SetProvider(snapshot.ProviderName);
        _planLabel.Text = snapshot.Plan.ToUpperInvariant();
        _planLabel.ForeColor = Theme.Signal;
        _statusLabel.ForeColor = Theme.Muted;
        _statusLabel.Text = $"Updated {snapshot.FetchedAt:t}";
        _creditLabel.Text = snapshot.ProviderId.Equals("copilot", StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrWhiteSpace(snapshot.AccountName)
                ? "Uses your existing Copilot login"
                : $"{snapshot.AccountName} - existing Copilot login"
            : snapshot.AvailableResetCredits > 0
            ? $"{snapshot.AvailableResetCredits} full reset{(snapshot.AvailableResetCredits == 1 ? string.Empty : "s")} available"
            : string.IsNullOrWhiteSpace(snapshot.CreditBalance)
                ? "Uses your existing Codex login"
                : $"Credits  {snapshot.CreditBalance}";
        _sessionMeter.SetWindow(snapshot.Session);
        _weeklyMeter.SetWindow(snapshot.Weekly);
        ResetRefreshButton();
    }

    public void SetError(string providerName, string message)
    {
        SetProvider(providerName);
        _planLabel.Text = "NEEDS ATTENTION";
        _planLabel.ForeColor = Theme.Critical;
        _statusLabel.ForeColor = Theme.Critical;
        _statusLabel.Text = "Not connected";
        _creditLabel.Text = message;
        _sessionMeter.SetWindow(null);
        _weeklyMeter.SetWindow(null);
        ResetRefreshButton();
    }

    private void SetProvider(string providerName)
    {
        _titleLabel.Text = $"{providerName.ToUpperInvariant()} // LIMITS";
        _sessionMeter.SetEmptySource(providerName);
        _weeklyMeter.SetEmptySource(providerName);
    }

    private static string ShortProviderName(string providerName) =>
        providerName.Equals("GitHub Copilot", StringComparison.OrdinalIgnoreCase) ? "Copilot" : providerName;

    public void ShowNearTray()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Clamp(cursor.X - Width + 24, screen.Left + 8, screen.Right - Width - 8);
        var y = screen.Bottom - Height - 8;

        if (cursor.Y < screen.Top + Height)
        {
            y = screen.Top + 8;
        }

        Location = new Point(x, y);
        Show();
        Activate();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!_allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Hairline);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void ResetRefreshButton()
    {
        _refreshButton.Enabled = true;
        _refreshButton.Text = "Refresh";
    }
}
