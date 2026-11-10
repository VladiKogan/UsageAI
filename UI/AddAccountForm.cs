using System.Diagnostics;
using UsageAI.Models;
using UsageAI.Services;

namespace UsageAI.UI;

/// <summary>
/// Add account…: either signs another account in through the provider's official CLI login into a
/// new folder (by default <c>%USERPROFILE%\.claude-&lt;name&gt;</c>), or adds a login folder the user
/// already has. Opened for an account UsageAI created, it signs that folder in again.
/// </summary>
internal sealed class AddAccountForm : Form
{
    private static readonly string[] ProviderOptions = { "Claude Code", "Codex" };
    private static readonly string[] WayOptions = { "Sign in to another account", "Use a login folder I already have" };

    private readonly AccountManager _manager;
    private readonly ProviderAccount? _reauthenticate;
    private readonly int? _dpiOverride;
    private readonly TableLayoutPanel _layout;
    private readonly SegmentedControl _provider;
    private readonly SegmentedControl _way;
    private readonly TextBox _name;
    private readonly TextBox _folder;
    private readonly Button _browse;
    private readonly Label _command;
    private readonly Button _copyCommand;
    private readonly Label _status;
    private readonly LinkLabel _link;
    private readonly Button _copyLink;
    private readonly Button _primary;
    private readonly Button _cancel;
    private readonly Label _nameCaption;
    private bool _folderEdited;
    private bool _settingFolder;
    private CancellationTokenSource? _signIn;
    private Uri? _signInLink;

    public AddAccountForm(AccountManager manager, string? providerId = null, ProviderAccount? reauthenticate = null, int? dpiOverride = null)
    {
        _manager = manager;
        _reauthenticate = reauthenticate;
        _dpiOverride = dpiOverride;
        var scale = Scale();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        Font = Typography.Text(9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        WindowThemeHelpers.RevealWhenPainted(this);
        StartPosition = FormStartPosition.CenterScreen;
        Text = reauthenticate is null ? "Add account" : "Re-authenticate account";
        AppIcon.Apply(this);
        ClientSize = new Size(scale[560], scale[430]);

        _layout = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Padding = scale.Pad(20, 16, 20, 14),
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scale[110]));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(_layout);

        var title = new Label
        {
            AutoSize = true,
            Font = Typography.Display(12F),
            Margin = scale.Pad(0, 0, 0, 10),
            Text = reauthenticate is null ? "Add an account" : $"Sign {reauthenticate.DisplayLabel} in again",
            UseMnemonic = false,
        };
        AddRow(title, span: true);

        _provider = new SegmentedControl(ProviderOptions, string.Equals(providerId ?? reauthenticate?.ProviderId, AccountDiscovery.CodexProviderId, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        {
            AccessibleName = "Provider",
            DpiOverride = _dpiOverride,
            Dock = DockStyle.Fill,
            Margin = scale.Pad(0, 2, 0, 6),
            MinimumSize = new Size(0, scale[30]),
        };
        AddCaptioned("Provider", _provider);

        _way = new SegmentedControl(WayOptions, 0)
        {
            AccessibleName = "How to add it",
            DpiOverride = _dpiOverride,
            Dock = DockStyle.Fill,
            Margin = scale.Pad(0, 2, 0, 6),
            MinimumSize = new Size(0, scale[30]),
        };
        AddCaptioned("Way", _way);

        _name = new TextBox { Dock = DockStyle.Fill, Margin = scale.Pad(0, 4, 0, 4), MaxLength = 64, PlaceholderText = "team (optional)" };
        _nameCaption = AddCaptioned("Name", _name);

        _folder = new TextBox { Dock = DockStyle.Fill, Margin = scale.Pad(0, 4, 6, 4) };
        _browse = CreateButton("Browse…", scale, primary: false);
        _browse.Click += (_, _) => Browse();
        var folderCaption = Caption("Folder");
        var row = _layout.RowCount;
        _layout.Controls.Add(folderCaption, 0, row);
        _layout.Controls.Add(_folder, 1, row);
        _layout.Controls.Add(_browse, 2, row);
        _layout.RowCount = row + 1;

        _command = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = scale.Pad(0, 2, 6, 2),
            MaximumSize = new Size(scale[360], 0),
            UseMnemonic = false,
        };
        _copyCommand = CreateButton("Copy", scale, primary: false);
        _copyCommand.Click += (_, _) => TryCopy(CommandText());
        row = _layout.RowCount;
        _layout.Controls.Add(_command, 1, row);
        _layout.Controls.Add(_copyCommand, 2, row);
        _layout.RowCount = row + 1;

        _status = new Label
        {
            AutoSize = true,
            Margin = scale.Pad(0, 10, 0, 4),
            MaximumSize = new Size(scale[500], 0),
            UseMnemonic = false,
        };
        AddRow(_status, span: true);

        _link = new LinkLabel
        {
            AutoSize = true,
            Margin = scale.Pad(0, 2, 6, 2),
            MaximumSize = new Size(scale[400], 0),
            UseMnemonic = false,
            Visible = false,
        };
        _link.LinkClicked += (_, _) => OpenLink();
        _copyLink = CreateButton("Copy link", scale, primary: false);
        _copyLink.Visible = false;
        _copyLink.Click += (_, _) =>
        {
            if (_signInLink is { } link)
            {
                TryCopy(link.AbsoluteUri);
            }
        };
        row = _layout.RowCount;
        _layout.Controls.Add(_link, 0, row);
        _layout.SetColumnSpan(_link, 2);
        _layout.Controls.Add(_copyLink, 2, row);
        _layout.RowCount = row + 1;

        _layout.RowStyles.Clear();
        for (var index = 0; index < _layout.RowCount; index++)
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = scale.Pad(0, 10, 0, 0),
        };
        _primary = CreateButton("Sign in", scale, primary: true);
        _primary.Click += async (_, _) => await RunPrimaryAsync();
        _cancel = CreateButton("Cancel", scale, primary: false);
        _cancel.Click += (_, _) => CancelOrClose();
        buttons.Controls.Add(_primary);
        buttons.Controls.Add(_cancel);
        row = _layout.RowCount;
        _layout.Controls.Add(buttons, 0, row + 1);
        _layout.SetColumnSpan(buttons, 3);
        _layout.RowCount = row + 2;
        AcceptButton = _primary;

        _provider.SelectedIndexChanged += (_, _) => UpdateForm(resetFolder: true);
        _way.SelectedIndexChanged += (_, _) => UpdateForm(resetFolder: true);
        _name.TextChanged += (_, _) => UpdateForm(resetFolder: false);
        _folder.TextChanged += (_, _) =>
        {
            if (!_settingFolder)
            {
                _folderEdited = true;
            }

            UpdateForm(resetFolder: false);
        };

        if (reauthenticate is not null)
        {
            _provider.Enabled = false;
            _way.Enabled = false;
            _name.Visible = false;
            _nameCaption.Visible = false;
            _folder.ReadOnly = true;
            _browse.Visible = false;
            SetFolder(reauthenticate.Path ?? string.Empty);
            _folderEdited = true;
        }

        ApplyTheme();
        UpdateForm(resetFolder: reauthenticate is null);
    }

    /// <summary>
    /// Measures the dialog for the DPI of the monitor it is on. The constructor measured before any
    /// handle existed, at the start-up DPI, so this runs again once the window exists and whenever
    /// it moves to a monitor at another scale.
    /// </summary>
    private void ApplyScale()
    {
        var scale = Scale();
        _layout.Padding = scale.Pad(20, 16, 20, 14);
        _layout.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, scale[110]);
        foreach (var segmented in new[] { _provider, _way })
        {
            segmented.Margin = scale.Pad(0, 2, 0, 6);
            segmented.MinimumSize = new Size(0, scale[30]);
        }

        foreach (var button in new[] { _browse, _copyCommand, _copyLink, _primary, _cancel })
        {
            button.Margin = scale.Pad(8, 2, 0, 2);
            button.MinimumSize = new Size(scale[88], scale[30]);
        }

        _command.MaximumSize = new Size(scale[360], 0);
        _status.MaximumSize = new Size(scale[500], 0);
        _link.MaximumSize = new Size(scale[400], 0);
        var workingArea = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(
            Math.Min(scale[560], workingArea.Width - (Width - ClientSize.Width)),
            Math.Min(scale[430], workingArea.Height - (Height - ClientSize.Height)));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyScale();
        CenterToScreen();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale();
    }

    /// <summary>The provider key of the account added or signed in again, once the dialog succeeded.</summary>
    public string? AddedKey { get; private set; }

    private string ProviderId => _provider.SelectedIndex == 1 ? AccountDiscovery.CodexProviderId : AccountDiscovery.ClaudeProviderId;

    private bool SignsIn => _reauthenticate is not null || _way.SelectedIndex == 0;

    private void UpdateForm(bool resetFolder)
    {
        if (_signIn is not null)
        {
            return;
        }

        if (resetFolder && _reauthenticate is null)
        {
            _folderEdited = false;
        }

        if (SignsIn && !_folderEdited && _reauthenticate is null)
        {
            SetFolder(AccountDiscovery.SuggestFolder(ProviderId, _name.Text, _manager.UserProfile));
        }
        else if (!SignsIn && !_folderEdited)
        {
            SetFolder(string.Empty);
        }

        _command.Text = _folder.Text.Length > 0
            ? $"To use it in a terminal: {AccountCommands.UseFolder(ProviderId, _folder.Text.Trim())}"
            : "Choose the folder that holds the login.";
        _copyCommand.Enabled = _folder.Text.Length > 0;

        string? problem;
        if (SignsIn && _reauthenticate is null)
        {
            var (state, message) = AccountDiscovery.CheckNewLoginFolder(ProviderId, _folder.Text);
            problem = state is AccountDiscovery.NewFolderState.Usable ? null : message;
            _primary.Text = state == AccountDiscovery.NewFolderState.HoldsLogin ? "Use this login instead" : "Sign in";
            _primary.Enabled = state is AccountDiscovery.NewFolderState.Usable or AccountDiscovery.NewFolderState.HoldsLogin;
        }
        else if (SignsIn)
        {
            problem = null;
            _primary.Text = "Sign in again";
            _primary.Enabled = true;
        }
        else
        {
            problem = _folder.Text.Length == 0 ? null : AccountDiscovery.ExistingLoginProblem(ProviderId, _folder.Text);
            _primary.Text = "Add";
            _primary.Enabled = _folder.Text.Length > 0 && problem is null;
        }

        SetStatus(problem ?? (SignsIn
            ? $"Signs in with the official {AccountDiscovery.ProviderName(ProviderId)} CLI. Your browser opens; UsageAI never sees your password."
            : "UsageAI reads the login where it is and never changes that folder."), problem is not null);
    }

    private async Task RunPrimaryAsync()
    {
        if (!SignsIn)
        {
            Finish(_manager.AddFolder(ProviderId, _folder.Text, AccountSource.Added, _name.Text));
            return;
        }

        var folder = _folder.Text.Trim().Trim('"');
        if (_reauthenticate is null &&
            AccountDiscovery.CheckNewLoginFolder(ProviderId, folder).State == AccountDiscovery.NewFolderState.HoldsLogin)
        {
            // The folder already holds a login: use it rather than sign in over it.
            _way.SelectedIndex = 1;
            SetFolder(folder);
            _folderEdited = true;
            Finish(_manager.AddFolder(ProviderId, folder, AccountSource.Added, _name.Text));
            return;
        }

        folder = ProviderKey.NormalizeFolder(folder);
        var createdByAttempt = !Directory.Exists(folder);
        _signIn = new CancellationTokenSource();
        SetBusy(true);
        SetStatus("Waiting for you to finish signing in in the browser…", isProblem: false);
        AccountLoginResult result;
        try
        {
            result = await AccountLogin.RunAsync(
                ProviderId,
                folder,
                link => BeginInvoke(() => ShowLink(link)),
                _signIn.Token);
        }
        finally
        {
            _signIn.Dispose();
            _signIn = null;
        }

        if (IsDisposed)
        {
            return;
        }

        SetBusy(false);
        if (result.Outcome != AccountLoginOutcome.SignedIn)
        {
            SetStatus(result.Outcome == AccountLoginOutcome.Cancelled ? "Sign-in cancelled." : result.Message ?? "The sign-in did not finish.", isProblem: true);
            return;
        }

        if (_reauthenticate is not null)
        {
            AddedKey = _reauthenticate.UnitKey;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        var added = _manager.AddFolder(ProviderId, folder, AccountSource.Created, _name.Text);
        if (!added.Added && createdByAttempt)
        {
            // A login of someone already shown: the folder this attempt made holds nothing else.
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left in place; it is not added as an account.
            }
        }

        Finish(added);
    }

    private void Finish(AccountAddResult result)
    {
        if (result.Problem is { } problem)
        {
            SetStatus(problem, isProblem: true);
            return;
        }

        AddedKey = result.UnitKey;
        _manager.Scan(DateTimeOffset.UtcNow);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelOrClose()
    {
        if (_signIn is { } signIn)
        {
            signIn.Cancel();
            return;
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing mid sign-in stops the CLI; the attempt then removes the folder it created.
        _signIn?.Cancel();
        base.OnFormClosing(e);
    }

    private void SetBusy(bool busy)
    {
        _provider.Enabled = !busy && _reauthenticate is null;
        _way.Enabled = !busy && _reauthenticate is null;
        _name.Enabled = !busy;
        _folder.Enabled = !busy;
        _browse.Enabled = !busy;
        _primary.Enabled = !busy;
        _cancel.Text = busy ? "Cancel sign-in" : "Cancel";
        if (!busy)
        {
            _link.Visible = false;
            _copyLink.Visible = false;
            _signInLink = null;
        }
    }

    private void ShowLink(Uri link)
    {
        if (IsDisposed || _signIn is null)
        {
            return;
        }

        _signInLink = link;
        _link.Text = $"If the browser did not open: {link.AbsoluteUri}";
        _link.LinkArea = new LinkArea(_link.Text.Length - link.AbsoluteUri.Length, link.AbsoluteUri.Length);
        _link.Visible = true;
        _copyLink.Visible = true;
    }

    private void OpenLink()
    {
        if (_signInLink is not { Scheme: "https" } link)
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus("Windows could not open the browser. Copy the link instead.", isProblem: true);
        }
    }

    private void Browse()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = SignsIn ? "Choose an empty or new folder for the new login" : "Choose the folder that holds the login",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = SignsIn,
            InitialDirectory = _manager.UserProfile,
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _folderEdited = true;
            SetFolder(picker.SelectedPath);
            UpdateForm(resetFolder: false);
        }
    }

    private void SetFolder(string folder)
    {
        _settingFolder = true;
        _folder.Text = folder;
        _settingFolder = false;
    }

    private string CommandText() => AccountCommands.UseFolder(ProviderId, _folder.Text.Trim());

    private void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("The clipboard was busy; try again.", isProblem: true);
        }
    }

    private void SetStatus(string text, bool isProblem)
    {
        _status.Text = text;
        _status.ForeColor = isProblem ? Theme.CriticalText : Theme.TextSecondary;
    }

    private Label AddCaptioned(string caption, Control control)
    {
        var label = Caption(caption);
        var row = _layout.RowCount;
        _layout.Controls.Add(label, 0, row);
        _layout.Controls.Add(control, 1, row);
        _layout.SetColumnSpan(control, 2);
        _layout.RowCount = row + 1;
        return label;
    }

    private Label Caption(string text) => new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Margin = Scale().Pad(0, 0, 8, 0),
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private void AddRow(Control control, bool span)
    {
        var row = _layout.RowCount;
        _layout.Controls.Add(control, 0, row);
        if (span)
        {
            _layout.SetColumnSpan(control, 3);
        }

        _layout.RowCount = row + 1;
    }

    private static Button CreateButton(string text, LayoutScale scale, bool primary)
    {
        var button = new Button
        {
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Margin = scale.Pad(8, 2, 0, 2),
            MinimumSize = new Size(scale[88], scale[30]),
            Text = text,
            Tag = primary,
            UseVisualStyleBackColor = false,
        };
        return button;
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        foreach (var control in _layout.Controls.Cast<Control>().Concat(_layout.Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.Cast<Control>())))
        {
            switch (control)
            {
                case TextBox box:
                    box.BackColor = Theme.SurfaceRaised;
                    box.ForeColor = Theme.Text;
                    box.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button button when Theme.IsHighContrast:
                    button.FlatStyle = FlatStyle.System;
                    break;
                case Button button:
                    var primary = button.Tag is true;
                    button.BackColor = primary ? Theme.Accent : Theme.SurfaceRaised;
                    button.ForeColor = primary ? Theme.OnAccent : Theme.Text;
                    button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Hairline;
                    break;
                case LinkLabel link:
                    link.LinkColor = Theme.Signal;
                    link.ActiveLinkColor = Theme.Signal;
                    link.ForeColor = Theme.TextSecondary;
                    break;
                case SegmentedControl:
                    control.BackColor = Theme.Night;
                    control.ForeColor = Theme.Text;
                    break;
                case Label label when ReferenceEquals(label, _command):
                    label.ForeColor = Theme.Muted;
                    break;
                case Label label when !ReferenceEquals(label, _status):
                    label.ForeColor = Theme.Text;
                    break;
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowThemeHelpers.ApplyDarkTitleBar(this, Theme.IsDark && !Theme.IsHighContrast);
    }

    private LayoutScale Scale() => _dpiOverride is { } dpi ? new LayoutScale(dpi) : new LayoutScale(this);
}

/// <summary>A one-line text question in the app's palette, such as a new name for an account.</summary>
internal sealed class TextPrompt : Form
{
    private readonly TextBox _text;

    private TextPrompt(string caption, string prompt, string value)
    {
        var scale = new LayoutScale(this);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Night;
        ForeColor = Theme.Text;
        Font = Typography.Text(9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        WindowThemeHelpers.RevealWhenPainted(this);
        StartPosition = FormStartPosition.CenterParent;
        Text = caption;
        ClientSize = new Size(scale[380], scale[132]);
        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Padding = scale.Pad(16, 14, 16, 12) };
        var label = new Label { AutoSize = true, Text = prompt, Margin = scale.Pad(0, 0, 0, 6), UseMnemonic = false };
        _text = new TextBox
        {
            BackColor = Theme.SurfaceRaised,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Top,
            ForeColor = Theme.Text,
            MaxLength = 64,
            Text = value,
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = scale.Pad(0, 12, 0, 0) };
        var ok = new Button { AutoSize = true, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, MinimumSize = new Size(scale[80], scale[28]), Text = "OK", BackColor = Theme.Accent, ForeColor = Theme.OnAccent };
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, MinimumSize = new Size(scale[80], scale[28]), Text = "Cancel", BackColor = Theme.SurfaceRaised, ForeColor = Theme.Text };
        if (Theme.IsHighContrast)
        {
            ok.FlatStyle = FlatStyle.System;
            cancel.FlatStyle = FlatStyle.System;
        }

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(label);
        layout.Controls.Add(_text);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The text entered, or null when the user cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string caption, string prompt, string value)
    {
        using var dialog = new TextPrompt(caption, prompt, value);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._text.Text : null;
    }
}
