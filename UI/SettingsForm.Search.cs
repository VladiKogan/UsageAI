namespace UsageAI.UI;

/// <summary>
/// Settings' search: a box above the page list whose results drop down under it, grouped by
/// page. Choosing one opens its page, scrolls the setting to the top, focuses it, and marks its
/// row with an accent band that fades, so the eye lands on it.
/// </summary>
internal sealed partial class SettingsForm
{
    /// <summary>The pages' icons in the Windows icon font: settings, bell, palette, list, person, info.</summary>
    internal static readonly string[] PageGlyphs = { "", "", "", "", "", "" };

    /// <summary>How long a found setting's band takes to fade.</summary>
    internal const int FlashMilliseconds = 1500;

    private readonly List<SettingsSearchEntry> _searchEntries = new();
    private readonly HashSet<int> _expandedPages = new();
    private SettingsSearchBox _search = null!;
    private SettingsSearchResults _results = null!;
    private System.Windows.Forms.Timer _flashTimer = null!;
    private FlashState? _flash;

    /// <summary>A found row being marked: its page, its table row, the controls in it, and when it began.</summary>
    private sealed record FlashState(BufferedTableLayoutPanel Layout, int Row, Control[] Controls, long Started);

    /// <summary>The search box, for checks and the preview.</summary>
    internal TextBox SearchBox => _search.Box;

    /// <summary>The results under it.</summary>
    internal SettingsSearchResults SearchResults => _results;

    private void CreateSearch(LayoutScale scale)
    {
        _search = new SettingsSearchBox
        {
            DpiOverride = _dpiOverride,
            Dock = DockStyle.Fill,
            Margin = scale.Pad(8, 12, 4, 4),
        };
        _search.Box.TextChanged += (_, _) =>
        {
            _expandedPages.Clear();
            UpdateResults();
        };
        _search.Box.KeyDown += OnSearchKeyDown;
        _search.Box.Leave += (_, _) =>
        {
            // Choosing a result with the mouse leaves the focus here, so only a real move away
            // closes the results.
            if (IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && !_search.Box.Focused)
                    {
                        HideResults();
                    }
                });
            }
        };

        _results = new SettingsSearchResults
        {
            DpiOverride = _dpiOverride,
            Glyphs = PageGlyphs,
        };
        _results.Chosen += (_, eventArgs) => ChooseResult(eventArgs.Row);
        Controls.Add(_results);

        _flashTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _flashTimer.Tick += (_, _) => StepFlash();
    }

    /// <summary>Records something the search can find on the page being built.</summary>
    private void Index(string text, Control target)
    {
        if (_building is not null && !string.IsNullOrWhiteSpace(text))
        {
            _searchEntries.Add(new SettingsSearchEntry(_pages.Count - 1, text, target, SettingsSearch.KeywordsFor(text)));
        }
    }

    /// <summary>
    /// Everything the search can find: the labels recorded while the pages were built, plus the
    /// entries of the provider and account lists as they are now.
    /// </summary>
    internal IReadOnlyList<SettingsSearchEntry> SearchEntries()
    {
        var entries = new List<SettingsSearchEntry>(_searchEntries);
        var providersPage = Array.IndexOf(PageNames, "Providers");
        for (var index = 0; index < _providers.Items.Count; index++)
        {
            entries.Add(new SettingsSearchEntry(providersPage, _providers.Items[index]!.ToString()!, _providers, Array.Empty<string>(), index));
        }

        var accountsPage = Array.IndexOf(PageNames, AccountsSection);
        for (var index = 0; index < _accountList.Items.Count; index++)
        {
            if (_accountList.Items[index] is AccountEntry account)
            {
                entries.Add(new SettingsSearchEntry(accountsPage, account.Text, _accountList, Array.Empty<string>(), index));
            }
        }

        return entries;
    }

    /// <summary>Types <paramref name="text"/> into the search box, as the preview's --search does.</summary>
    internal void Search(string text)
    {
        _search.Box.Focus();
        _search.Box.Text = text;
        _search.Box.SelectionStart = text.Length;
    }

    /// <summary>Focuses the search box, from Ctrl+F or Ctrl+E anywhere in the window.</summary>
    private void FocusSearch()
    {
        _search.Box.Focus();
        _search.Box.SelectAll();
        if (_search.Box.TextLength > 0)
        {
            UpdateResults();
        }
    }

    private void UpdateResults(int select = 0)
    {
        if (_search.Box.TextLength == 0)
        {
            HideResults();
            return;
        }

        _results.SetResults(SettingsSearch.Find(SearchEntries(), PageNames, _search.Box.Text, _expandedPages), select);
        PlaceResults();
        _results.Visible = true;
        _results.BringToFront();
    }

    private void HideResults() => _results.Visible = false;

    /// <summary>
    /// Places the results under the search box, wider than the page list as the Claude desktop
    /// app's are, and no taller than the window leaves room for.
    /// </summary>
    private void PlaceResults()
    {
        var scale = Scale();
        var below = PointToClient(_search.Parent!.PointToScreen(new Point(_search.Left, _search.Bottom)));
        var width = Math.Min(Math.Max(scale[340], _search.Width), ClientSize.Width - below.X - scale[12]);
        var height = _results.PreferredHeight(ClientSize.Height - below.Y - scale[4] - scale[12]);
        _results.Bounds = new Rectangle(below.X, below.Y + scale[4], Math.Max(scale[160], width), height);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        switch (eventArgs.KeyCode)
        {
            case Keys.Down:
            case Keys.Up:
                if (!_results.Visible)
                {
                    UpdateResults();
                }
                else
                {
                    _results.MoveSelection(eventArgs.KeyCode == Keys.Down ? 1 : -1);
                }

                eventArgs.Handled = eventArgs.SuppressKeyPress = true;
                break;
            case Keys.Enter:
                if (_results.Visible && _results.SelectedRow is { } row)
                {
                    ChooseResult(row);
                }

                eventArgs.Handled = eventArgs.SuppressKeyPress = true;
                break;
        }
    }

    /// <summary>
    /// Escape in the search box steps back one thing at a time — closes the results, then
    /// clears the text, then returns to the page list — and never closes the window from there.
    /// </summary>
    private bool EscapeSearch()
    {
        if (!_search.Box.Focused)
        {
            return false;
        }

        if (_results.Visible)
        {
            HideResults();
        }
        else if (_search.Box.TextLength > 0)
        {
            _search.Box.Clear();
        }
        else
        {
            _nav.Focus();
        }

        return true;
    }

    private void ChooseResult(SettingsSearchRow row)
    {
        switch (row.Kind)
        {
            case SettingsSearchRowKind.Page:
                _search.Box.Clear();
                EnterPage(row.Group!.Page);
                break;
            case SettingsSearchRowKind.More:
                // The rest of the page's results open in place; the first of them is selected.
                var first = _results.Rows.ToList().IndexOf(row);
                _expandedPages.Add(row.Group!.Page);
                UpdateResults(first);
                break;
            case SettingsSearchRowKind.Hit:
                _search.Box.Clear();
                GoTo(row.Hit!.Entry);
                break;
        }
    }

    /// <summary>
    /// Opens <paramref name="entry"/>'s page, selects it if it is a list entry, scrolls its row to
    /// the top, focuses it, and marks the row.
    /// </summary>
    internal void GoTo(SettingsSearchEntry entry)
    {
        ShowPage(entry.Page);
        if (entry.ListIndex >= 0 && entry.Target is ListBox list && entry.ListIndex < list.Items.Count)
        {
            list.SelectedIndex = entry.ListIndex;
        }

        var layout = _pages[entry.Page].Layout;
        var cell = entry.Target;
        while (cell.Parent is { } parent && !ReferenceEquals(parent, layout))
        {
            cell = parent;
        }

        if (!ReferenceEquals(cell.Parent, layout))
        {
            return;
        }

        PerformLayout();
        var row = layout.GetRow(cell);
        var heights = layout.GetRowHeights();
        var top = layout.Padding.Top + heights.Take(row).Sum();
        _scrollFrame.ScrollTo(top - Scale()[12]);
        if (entry.Target.CanSelect)
        {
            entry.Target.Focus();
        }

        Flash(layout, row);
    }

    /// <summary>Marks a row of a page with the accent band, which fades over <see cref="FlashMilliseconds"/>.</summary>
    private void Flash(BufferedTableLayoutPanel layout, int row)
    {
        EndFlash();
        var controls = layout.Controls.Cast<Control>().Where(control => layout.GetRow(control) == row).ToArray();
        _flash = new FlashState(layout, row, controls, Environment.TickCount64);
        _flashTimer.Start();
        StepFlash();
    }

    /// <summary>
    /// The band's strength now: full at first, fading to nothing. With Windows' animations off it
    /// stays full, then goes at once.
    /// </summary>
    internal double FlashStrength()
    {
        if (_flash is not { } flash)
        {
            return 0;
        }

        var progress = Math.Clamp((Environment.TickCount64 - flash.Started) / (double)FlashMilliseconds, 0, 1);
        return progress >= 1 ? 0 : Motion.Enabled ? 1 - Motion.Ease(progress) : 1;
    }

    /// <summary>Whether a found row is being marked.</summary>
    internal bool IsFlashing => _flash is not null;

    private void StepFlash()
    {
        if (_flash is not { } flash)
        {
            _flashTimer.Stop();
            return;
        }

        var strength = FlashStrength();
        if (strength <= 0 || flash.Layout.IsDisposed)
        {
            EndFlash();
            return;
        }

        // The row's own controls are opaque, so they take the band's colour too; High Contrast
        // draws a frame around the row instead and leaves them as they are.
        if (!Theme.IsHighContrast)
        {
            var band = BandColor(strength);
            foreach (var control in flash.Controls.SelectMany(control => new[] { control }.Concat(Descendants(control))).Where(TakesBand))
            {
                control.BackColor = band;
            }
        }

        flash.Layout.Invalidate();
    }

    private static Color BandColor(double strength) => Theme.Blend(Theme.Accent, Theme.Surface, 0.28 * strength);

    /// <summary>Controls that sit on the card's colour, as opposed to raised fields that keep theirs.</summary>
    private static bool TakesBand(Control control) =>
        control is Label or ToggleSwitch or SegmentedControl or SettingPreview or FlowLayoutPanel ||
        control is Button { FlatAppearance.BorderSize: 0 };

    private void EndFlash()
    {
        _flashTimer.Stop();
        if (_flash is not { } flash)
        {
            return;
        }

        _flash = null;
        foreach (var control in flash.Controls)
        {
            ApplyControlTheme(control);
        }

        flash.Layout.Invalidate();
    }

    /// <summary>Paints the band, or High Contrast's frame, behind a found row.</summary>
    private void PaintFlash(Graphics graphics, BufferedTableLayoutPanel layout, int[] tops)
    {
        if (_flash is not { } flash || !ReferenceEquals(flash.Layout, layout) || flash.Row + 1 >= tops.Length)
        {
            return;
        }

        var scale = Scale();
        var inset = scale[CardInset];
        var band = Rectangle.FromLTRB(
            layout.Padding.Left - inset + scale[2],
            tops[flash.Row],
            layout.Width - layout.Padding.Right + inset - scale[3],
            tops[flash.Row + 1]);
        using var shape = DrawingHelpers.RoundedRectangle(band, scale.Exact(6));
        if (Theme.IsHighContrast)
        {
            using var frame = new Pen(SystemColors.Highlight, Math.Max(2F, scale.Exact(2)));
            graphics.DrawPath(frame, shape);
            return;
        }

        using var fill = new SolidBrush(BandColor(FlashStrength()));
        graphics.FillPath(fill, shape);
    }
}
