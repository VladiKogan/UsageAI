using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

internal sealed class UsageApplicationContext : ApplicationContext
{
    private readonly IReadOnlyDictionary<string, IUsageClient> _clients;
    private readonly Dictionary<string, ToolStripMenuItem> _providerItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly NotifyIcon _trayIcon;
    private readonly UsagePopupForm _popup;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly ToolStripMenuItem _startupItem;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Icon? _currentIcon;
    private string _selectedProviderId;
    private bool _isExiting;

    public UsageApplicationContext(IEnumerable<IUsageClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Id, StringComparer.OrdinalIgnoreCase);
        if (_clients.Count == 0)
        {
            throw new ArgumentException("At least one usage provider is required.", nameof(clients));
        }

        var savedProviderId = UsageProviderSettings.SelectedProviderId;
        _selectedProviderId = _clients.ContainsKey(savedProviderId)
            ? savedProviderId
            : _clients.Keys.First();
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

        var providerMenu = new ToolStripMenuItem("Account");
        foreach (var client in _clients.Values)
        {
            var providerItem = new ToolStripMenuItem(client.DisplayName)
            {
                Checked = client.Id.Equals(_selectedProviderId, StringComparison.OrdinalIgnoreCase),
            };
            providerItem.Click += async (_, _) => await SelectProviderAsync(client.Id);
            _providerItems.Add(client.Id, providerItem);
            providerMenu.DropDownItems.Add(providerItem);
        }

        menu.Items.Add(providerMenu);
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
            Text = TrimToolTip($"UsageAI - reading {CurrentClient.DisplayName} usage"),
            Visible = true,
        };
        _trayIcon.MouseUp += TrayIconOnMouseUp;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(showPopup: false);
        _refreshTimer.Start();

        _ = RefreshAsync(showPopup: false);
    }

    private IUsageClient CurrentClient => _clients[_selectedProviderId];

    private async Task SelectProviderAsync(string providerId)
    {
        if (!_clients.ContainsKey(providerId))
        {
            return;
        }

        _selectedProviderId = providerId;
        foreach (var (id, item) in _providerItems)
        {
            item.Checked = id.Equals(providerId, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            UsageProviderSettings.SelectedProviderId = providerId;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The account selection could not be saved: {exception.Message}",
                "UsageAI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        await RefreshAsync(showPopup: true, waitForCurrentRefresh: true);
    }

    private async Task RefreshAsync(bool showPopup, bool waitForCurrentRefresh = false)
    {
        var entered = waitForCurrentRefresh
            ? await _refreshLock.WaitAsync(TimeSpan.FromSeconds(30))
            : await _refreshLock.WaitAsync(0);
        if (!entered || _isExiting)
        {
            return;
        }

        var client = CurrentClient;
        try
        {
            _popup.SetLoading(client.DisplayName);
            if (showPopup && !_popup.Visible)
            {
                _popup.ShowNearTray();
            }

            var snapshot = await client.GetUsageAsync();
            if (!client.Id.Equals(_selectedProviderId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _popup.SetSnapshot(snapshot);
            SetTraySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            if (client.Id.Equals(_selectedProviderId, StringComparison.OrdinalIgnoreCase))
            {
                _popup.SetError(client.DisplayName, exception.Message);
                SetTrayError(client.DisplayName, exception.Message);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void SetTraySnapshot(UsageSnapshot snapshot)
    {
        ReplaceIcon(TrayIconFactory.Create(snapshot.HighestUsedPercent, ProviderGlyph(snapshot.ProviderId)));
        var windows = new[] { snapshot.Session, snapshot.Weekly }
            .Where(window => window is not null)
            .Cast<UsageWindow>()
            .Select(window => $"{window.Name} {CompactRemaining(window)}");
        _trayIcon.Text = TrimToolTip($"UsageAI - {snapshot.ProviderName} - {string.Join(" - ", windows)}");
    }

    private void SetTrayError(string providerName, string message)
    {
        ReplaceIcon(TrayIconFactory.Create(100, "!", hasError: true));
        _trayIcon.Text = TrimToolTip($"UsageAI - {providerName} - {message}");
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

    private static string CompactRemaining(UsageWindow window) =>
        window.RemainingText == "UNLIMITED" ? "unlimited" : $"{window.RemainingPercent}% left";

    private static string ProviderGlyph(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => "A",
        "copilot" => "G",
        _ => "C",
    };

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
