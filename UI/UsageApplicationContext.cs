using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal sealed class UsageApplicationContext : ApplicationContext
{
    private readonly CodexUsageClient _client;
    private readonly NotifyIcon _trayIcon;
    private readonly UsagePopupForm _popup;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly ToolStripMenuItem _startupItem;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Icon? _currentIcon;
    private bool _isExiting;

    public UsageApplicationContext(CodexUsageClient client)
    {
        _client = client;
        _popup = new UsagePopupForm();
        _popup.RefreshRequested += async (_, _) => await RefreshAsync(showPopup: true);

        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
        };
        menu.Items.Add("Open", null, (_, _) => TogglePopup());
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshAsync(showPopup: false));
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = true,
        };
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _currentIcon = TrayIconFactory.Create(0);
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _currentIcon,
            Text = "UsageAI — reading Codex usage",
            Visible = true,
        };
        _trayIcon.MouseUp += TrayIconOnMouseUp;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(showPopup: false);
        _refreshTimer.Start();

        _ = RefreshAsync(showPopup: false);
    }

    private async Task RefreshAsync(bool showPopup)
    {
        if (!await _refreshLock.WaitAsync(0) || _isExiting)
        {
            return;
        }

        try
        {
            _popup.SetLoading();
            if (showPopup && !_popup.Visible)
            {
                _popup.ShowNearTray();
            }

            var snapshot = await _client.GetUsageAsync();
            _popup.SetSnapshot(snapshot);
            SetTraySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            _popup.SetError(exception.Message);
            SetTrayError(exception.Message);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void SetTraySnapshot(UsageSnapshot snapshot)
    {
        ReplaceIcon(TrayIconFactory.Create(snapshot.HighestUsedPercent));
        var session = snapshot.Session is null ? "—" : $"{snapshot.Session.RemainingPercent}%";
        var weekly = snapshot.Weekly is null ? "—" : $"{snapshot.Weekly.RemainingPercent}%";
        _trayIcon.Text = TrimToolTip($"UsageAI — 5h {session} left · week {weekly} left");
    }

    private void SetTrayError(string message)
    {
        ReplaceIcon(TrayIconFactory.Create(100, hasError: true));
        _trayIcon.Text = TrimToolTip($"UsageAI — {message}");
    }

    private void ReplaceIcon(Icon icon)
    {
        var previous = _currentIcon;
        _currentIcon = icon;
        _trayIcon.Icon = icon;
        previous?.Dispose();
    }

    private void TrayIconOnMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            TogglePopup();
        }
    }

    private void TogglePopup()
    {
        if (_popup.Visible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.ShowNearTray();
        }
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            StartupManager.SetEnabled(_startupItem.Checked);
        }
        catch (Exception exception)
        {
            _startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
            _startupItem.Checked = StartupManager.IsEnabled;
            _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
            MessageBox.Show(exception.Message, "UsageAI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Exit()
    {
        _isExiting = true;
        _refreshTimer.Stop();
        _trayIcon.Visible = false;
        _popup.CloseForExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _trayIcon.Dispose();
            _popup.Dispose();
            _currentIcon?.Dispose();
            _refreshLock.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string TrimToolTip(string value) => value.Length <= 63 ? value : value[..60] + "…";

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.SurfaceRaised;
        public override Color MenuItemBorder => Theme.Hairline;
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Hairline;
        public override Color SeparatorLight => Theme.Hairline;
    }
}
