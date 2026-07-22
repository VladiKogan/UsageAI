using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal sealed class UsageApplicationContext : ApplicationContext
{
    private readonly IUsageClient[] _clients;
    private readonly Dictionary<string, ProviderViewState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _trayIcon;
    private readonly UsagePopupForm _popup;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly ToolStripMenuItem _startupItem;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Icon? _currentIcon;
    private DateTimeOffset? _lastRefreshed;
    private bool _isExiting;

    public UsageApplicationContext(IEnumerable<IUsageClient> clients)
    {
        _clients = clients.ToArray();
        if (_clients.Length == 0)
        {
            throw new ArgumentException("At least one usage provider is required.", nameof(clients));
        }

        foreach (var client in _clients)
        {
            _states[client.Id] = new ProviderViewState(client.Id, client.DisplayName, null, null, true);
        }

        _popup = new UsagePopupForm();
        _popup.SetStates(OrderedStates(), isRefreshing: true, lastRefreshed: null);
        _popup.RefreshRequested += async (_, _) => await RefreshAllAsync(showPopup: true);

        _menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            Padding = new Padding(4),
        };
        var openItem = new ToolStripMenuItem("Open")
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        openItem.Click += (_, _) => OpenDashboard();
        _menu.Items.Add(openItem);
        _menu.Items.Add("Refresh", null, async (_, _) => await RefreshAllAsync(showPopup: false));
        _menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = true,
        };
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        _currentIcon = TrayIconFactory.Create(0, "U");
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _currentIcon,
            Text = "UsageAI - checking connected providers",
            Visible = true,
        };
        _trayIcon.MouseUp += TrayIconOnMouseUp;

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds,
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync(showPopup: false);
        _refreshTimer.Start();

        Application.Idle += RefreshOnFirstIdle;
    }

    private void RefreshOnFirstIdle(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= RefreshOnFirstIdle;
        _ = RefreshAllAsync(showPopup: false);
    }

    private async Task RefreshAllAsync(bool showPopup)
    {
        var entered = await _refreshLock.WaitAsync(0);
        if (!entered || _isExiting)
        {
            return;
        }

        try
        {
            foreach (var client in _clients)
            {
                var current = _states[client.Id];
                _states[client.Id] = current with { IsLoading = true };
            }

            UpdatePopup(isRefreshing: true);
            if (showPopup && !_popup.Visible)
            {
                _popup.ShowNearTray(_popup.Mode);
            }

            var results = await Task.WhenAll(_clients.Select(FetchProviderAsync));
            if (_isExiting)
            {
                return;
            }

            foreach (var result in results)
            {
                _states[result.ProviderId] = result;
            }

            _lastRefreshed = DateTimeOffset.Now;
            UpdatePopup(isRefreshing: false);
            UpdateTray();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ProviderViewState> FetchProviderAsync(IUsageClient client)
    {
        try
        {
            var snapshot = await client.GetUsageAsync(_shutdown.Token);
            return new ProviderViewState(client.Id, client.DisplayName, snapshot, null, false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return new ProviderViewState(client.Id, client.DisplayName, null, "Refresh cancelled.", false);
        }
        catch (Exception exception)
        {
            var message = exception is CodexUsageException or
                ClaudeCodeUsageException or
                ClaudeWebUsageException or
                GitHubCopilotUsageException
                ? exception.Message
                : $"{client.DisplayName} usage is temporarily unavailable.";
            return new ProviderViewState(client.Id, client.DisplayName, null, message, false);
        }
    }

    private void UpdatePopup(bool isRefreshing) =>
        _popup.SetStates(OrderedStates(), isRefreshing, _lastRefreshed);

    private ProviderViewState[] OrderedStates() =>
        _clients.Select(client => _states[client.Id]).ToArray();

    private void UpdateTray()
    {
        var connected = OrderedStates().Where(state => state.Snapshot is not null).ToArray();
        if (connected.Length == 0)
        {
            ReplaceIcon(TrayIconFactory.Create(100, "!", hasError: true));
            _trayIcon.Text = "UsageAI - no connected providers";
            return;
        }

        var highestUsed = connected.Max(state => state.Snapshot!.HighestUsedPercent);
        var glyph = connected.Length == 1 ? ProviderGlyph(connected[0].ProviderId) : "U";
        ReplaceIcon(TrayIconFactory.Create(highestUsed, glyph));

        var summaries = connected
            .Select(state => $"{ShortProviderName(state.ProviderName)} {CompactMetric(state.Snapshot!)}")
            .ToArray();
        _trayIcon.Text = TrimToolTip($"UsageAI - {string.Join(" - ", summaries)}");
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
            ToggleCompactPopup();
        }
    }

    private void ToggleCompactPopup()
    {
        if (_popup.Visible && _popup.Mode == DashboardMode.Compact)
        {
            _popup.Hide();
            return;
        }

        _popup.ShowNearTray(DashboardMode.Compact);
    }

    private void OpenDashboard() => _popup.ShowNearTray(DashboardMode.Full);

    private void StartupItemOnCheckedChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            StartupManager.SetEnabled(_startupItem.Checked);
        }
        catch (Exception)
        {
            _startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
            _startupItem.Checked = StartupManager.IsEnabled;
            _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
            MessageBox.Show(
                "Windows startup settings could not be updated.",
                "UsageAI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void Exit()
    {
        _isExiting = true;
        Application.Idle -= RefreshOnFirstIdle;
        _shutdown.Cancel();
        _refreshTimer.Stop();
        _trayIcon.Visible = false;
        _popup.CloseForExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= RefreshOnFirstIdle;
            _shutdown.Cancel();
            _refreshTimer.Dispose();
            _trayIcon.Dispose();
            _menu.Dispose();
            _popup.Dispose();
            _currentIcon?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string CompactMetric(UsageSnapshot snapshot)
    {
        var window = snapshot.Session ?? snapshot.Weekly;
        if (window is not null)
        {
            return window.RemainingText == "UNLIMITED" ? "unlimited" : $"{window.RemainingPercent}% left";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CreditBalance))
        {
            return $"{snapshot.CreditBalance} credits";
        }

        return snapshot.AvailableResetCredits > 0
            ? $"{snapshot.AvailableResetCredits} resets"
            : "connected";
    }

    private static string ProviderGlyph(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => "A",
        "copilot" => "G",
        _ => "C",
    };

    private static string ShortProviderName(string providerName) =>
        providerName.Equals("GitHub Copilot", StringComparison.OrdinalIgnoreCase) ? "Copilot" : providerName;

    private static string TrimToolTip(string value) => value.Length <= 63 ? value : value[..60] + "...";

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
