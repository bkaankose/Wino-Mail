using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Shell;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The one header over the content area, in the order of the macOS unified toolbar: back, sidebar toggle, modes (while
/// the sidebar is hidden), synchronize and the title at the start, the search entry centred, then Daily briefing,
/// What's New, Wino Account and the primary menu at the end. It renders and raises requests; the shell decides.
/// </summary>
internal sealed class ShellHeaderBar : IDisposable
{
    private const int TitleMaxWidthChars = 36;
    private const int SearchMaximumWidth = 420;
    private const int SearchTighteningThreshold = 240;
    private const uint SearchDelay = 300;

    private readonly Action<Exception> _error;
    private readonly Adw.HeaderBar _header = Adw.HeaderBar.New();
    private readonly Gtk.Button _back = Gtk.Button.NewFromIconName("go-previous-symbolic");
    private readonly Gtk.ToggleButton _sidebarToggle;
    private readonly Gtk.MenuButton _modes = Gtk.MenuButton.New();
    private readonly Gtk.Label _modesGlyph;
    private readonly Gtk.Popover _modesPopover = Gtk.Popover.New();
    private readonly Gtk.Box _modesList = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly WinoSynchronizationButton _synchronize;
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _subtitle = Gtk.Label.New(string.Empty);
    private readonly Gtk.SearchEntry _search = Gtk.SearchEntry.New();
    private readonly Gtk.ToggleButton _briefing;
    private readonly Gtk.Button _whatsNew;
    private readonly Gtk.Button _account;
    private readonly Gtk.MenuButton _menu = Gtk.MenuButton.New();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<IDisposable> _modeSubscriptions = [];
    private IShellSearchTarget? _target;
    private WinoApplicationMode? _currentMode;
    private IReadOnlyCollection<WinoApplicationMode>? _visibleModes;
    private bool _modesBuilt;
    private bool _sidebarVisible = true;
    private bool _applyingSidebar;
    private bool _applyingBriefing;
    private string? _applyingSearchText;
    private bool _compact;
    private bool _briefingVisible;
    private bool _whatsNewVisible;
    private bool _accountVisible;
    private string _subtitleText = string.Empty;
    private bool _disposed;

    public ShellHeaderBar(string synchronizeLabel, Action<Exception> error)
    {
        _error = error;

        WinoA11y.LabelWithTooltip(_back, Translator.Buttons_Back);
        _back.SetValign(Gtk.Align.Center);
        _back.SetVisible(false);
        _sidebarToggle = WinoIcons.ToggleButton(WinoIconGlyph.PanelLeft, Translator.MacOSMenu_ToggleSidebar);
        _sidebarToggle.SetActive(true);

        _modesGlyph = WinoIcons.Label(WinoIconGlyph.Mail);
        _modes.SetChild(_modesGlyph);
        _modes.AddCssClass("flat");
        _modes.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(_modes, Translator.KeyboardShortcuts_Mode);
        _modesList.AddCssClass("menu");
        _modesPopover.SetChild(_modesList);
        _modes.SetPopover(_modesPopover);
        _modes.SetVisible(false);

        _synchronize = new WinoSynchronizationButton(synchronizeLabel);
        _synchronize.SetState(false, false, false, null);

        var titleBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        titleBox.SetValign(Gtk.Align.Center);
        titleBox.SetMarginStart(6);
        foreach (var label in new[] { _title, _subtitle })
        {
            label.SetXalign(0);
            label.SetEllipsize(Pango.EllipsizeMode.End);
            label.SetMaxWidthChars(TitleMaxWidthChars);
            label.SetSingleLineMode(true);
            titleBox.Append(label);
        }
        _title.AddCssClass("heading");
        _subtitle.AddCssClass("caption");
        _subtitle.AddCssClass("dim-label");
        _subtitle.SetVisible(false);

        _search.SetHexpand(true);
        _search.SetPlaceholderText(Translator.SearchBarPlaceholder);
        _search.SetSearchDelay(SearchDelay);
        _search.SetWidthChars(18);
        _search.SetSensitive(false);
        WinoA11y.Label(_search, Translator.SearchBarPlaceholder);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(SearchMaximumWidth);
        clamp.SetTighteningThreshold(SearchTighteningThreshold);
        clamp.SetHexpand(true);
        clamp.SetChild(_search);

        _menu.SetIconName("open-menu-symbolic");
        _menu.SetPrimary(true);
        _menu.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(_menu, Translator.More);
        _account = WinoIcons.Button(WinoIconGlyph.Person, Translator.WinoAccount_Titlebar_SignedOutTitle);
        _whatsNew = WinoIcons.Button(WinoIconGlyph.Announcement, Translator.WhatsNew_ShellButtonTooltip);
        _briefing = WinoIcons.ToggleButton(WinoIconGlyph.DailyBriefing, Translator.DailyBriefing_Menu);
        _account.SetVisible(false);
        _whatsNew.SetVisible(false);
        _briefing.SetVisible(false);

        _header.SetCenteringPolicy(Adw.CenteringPolicy.Loose);
        _header.SetShowTitle(true);
        _header.PackStart(_back);
        _header.PackStart(_sidebarToggle);
        _header.PackStart(_modes);
        _header.PackStart(_synchronize.Root);
        _header.PackStart(titleBox);
        _header.SetTitleWidget(clamp);
        // PackEnd places the first widget next to the window buttons.
        _header.PackEnd(_menu);
        _header.PackEnd(_account);
        _header.PackEnd(_whatsNew);
        _header.PackEnd(_briefing);

        _subscriptions.Add(GtkSignals.OnClicked(_back, () => BackRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnToggled(_sidebarToggle, () =>
        {
            if (!_applyingSidebar && _sidebarToggle.GetActive() != _sidebarVisible) SidebarToggleRequested?.Invoke();
        }));
        _synchronize.Clicked += OnSynchronizeClicked;
        _subscriptions.Add(GtkSignals.OnToggled(_briefing, () =>
        {
            if (!_applyingBriefing) BriefingRequested?.Invoke(_briefing);
        }));
        _subscriptions.Add(GtkSignals.OnClicked(_whatsNew, () => WhatsNewRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_account, () => AccountRequested?.Invoke(_account)));
        var search = _search;
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnSearchChanged += h, h => search.OnSearchChanged -= h, (_, _) => SearchChanged()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnActivate += h, h => search.OnActivate -= h, (_, _) => SearchActivated()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnStopSearch += h, h => search.OnStopSearch -= h, (_, _) => SearchStopped()));

        RebuildMenu();
    }

    public Gtk.Widget Root => _header;

    public event Action? BackRequested;
    public event Action? SidebarToggleRequested;
    public event Action? SynchronizeRequested;
    public event Action<WinoApplicationMode>? ModeRequested;
    public event Action<Gtk.Widget>? BriefingRequested;
    public event Action? WhatsNewRequested;
    public event Action<Gtk.Widget>? AccountRequested;

    public void SetBackVisible(bool visible)
    {
        if (_back.GetVisible() != visible) _back.SetVisible(visible);
    }

    /// <summary>The toggle follows the sidebar; the modes menu shows while the sidebar (and its mode switcher) is not visible.</summary>
    public void SetSidebarState(bool visible)
    {
        _sidebarVisible = visible;
        if (_sidebarToggle.GetActive() != visible)
        {
            _applyingSidebar = true;
            try { _sidebarToggle.SetActive(visible); }
            finally { _applyingSidebar = false; }
        }
        if (_modes.GetVisible() == visible) _modes.SetVisible(!visible);
    }

    public void SetModes(WinoApplicationMode current, IReadOnlyCollection<WinoApplicationMode>? visible)
    {
        if (_modesBuilt && _currentMode == current && ReferenceEquals(_visibleModes, visible)) return;
        _modesBuilt = true;
        _currentMode = current;
        _visibleModes = visible;
        ClearModes();

        foreach (var segment in WinoModeSegments.All)
        {
            var isSettings = segment.Mode == WinoApplicationMode.Settings;
            if (!isSettings && visible is not null && !visible.Contains(segment.Mode)) continue;
            if (segment.Mode == current) WinoIcons.SetGlyph(_modesGlyph, WinoIcons.Glyph(segment.Regular));
            if (isSettings) _modesList.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

            var button = Gtk.Button.New();
            var title = WinoModeSwitcher.Title(segment.Mode);
            button.SetChild(WinoIcons.ButtonContent(segment.Regular, title));
            button.AddCssClass("flat");
            if (segment.Mode == current) button.AddCssClass("accent");
            WinoA11y.Label(button, title);
            var mode = segment.Mode;
            _modeSubscriptions.Add(GtkSignals.OnClicked(button, () =>
            {
                _modesPopover.Popdown();
                ModeRequested?.Invoke(mode);
            }));
            _modesList.Append(button);
        }
    }

    /// <summary>Re-reads every label, tooltip and menu text after a language change.</summary>
    public void ApplyLanguage(string synchronizeLabel)
    {
        WinoA11y.LabelWithTooltip(_back, Translator.Buttons_Back);
        WinoA11y.LabelWithTooltip(_sidebarToggle, Translator.MacOSMenu_ToggleSidebar);
        WinoA11y.LabelWithTooltip(_modes, Translator.KeyboardShortcuts_Mode);
        _search.SetPlaceholderText(_target?.SearchPlaceholder ?? Translator.SearchBarPlaceholder);
        WinoA11y.Label(_search, Translator.SearchBarPlaceholder);
        WinoA11y.LabelWithTooltip(_menu, Translator.More);
        WinoA11y.LabelWithTooltip(_account, Translator.WinoAccount_Titlebar_SignedOutTitle);
        WinoA11y.LabelWithTooltip(_whatsNew, Translator.WhatsNew_ShellButtonTooltip);
        WinoA11y.LabelWithTooltip(_briefing, Translator.DailyBriefing_Menu);
        _synchronize.SetDefaultLabel(synchronizeLabel);
        _modesBuilt = false;
        if (_currentMode is { } current) SetModes(current, _visibleModes);
        RebuildMenu();
    }

    public void SetSynchronization(bool supported, bool enabled, bool isSynchronizing, string? toolTip)
        => _synchronize.SetState(supported, enabled, isSynchronizing, toolTip);

    public void SetSynchronizationAttention(string? toolTip) => _synchronize.SetAttention(toolTip);

    public void SetTitle(string title, string? subtitle)
    {
        if (_title.GetText() != title) _title.SetText(title);
        _subtitleText = subtitle ?? string.Empty;
        if (_subtitle.GetText() != _subtitleText) _subtitle.SetText(_subtitleText);
        ApplySubtitleVisibility();
    }

    /// <summary>Compact hides the subtitle and moves the trailing buttons into the primary menu; below Wide the search entry narrows so the title keeps room.</summary>
    public void SetWidthClass(ShellWidthClass widthClass)
    {
        var searchChars = widthClass switch { ShellWidthClass.Compact => 8, ShellWidthClass.Medium => 12, _ => 18 };
        if (_search.GetWidthChars() != searchChars) _search.SetWidthChars(searchChars);
        var compact = widthClass == ShellWidthClass.Compact;
        if (_compact == compact) return;
        _compact = compact;
        ApplySubtitleVisibility();
        ApplyTrailingVisibility();
        RebuildMenu();
    }

    public void SetSearchTarget(IShellSearchTarget? target)
    {
        _target = target;
        _search.SetSensitive(target is not null);
        _search.SetPlaceholderText(target?.SearchPlaceholder ?? Translator.SearchBarPlaceholder);
    }

    public void FocusSearch()
    {
        if (_search.GetSensitive()) _search.GrabFocus();
    }

    /// <summary>Sets the entry's text without telling the target.</summary>
    public void SetSearchText(string text)
    {
        if (_search.GetText() == text) return;
        // GTK raises search-changed after the delay, so the flag is cleared by the handler that sees this text.
        _applyingSearchText = text;
        _search.SetText(text);
    }

    public void ResetSearch() => SetSearchText(string.Empty);

    public void SetBriefing(bool visible, bool hasUnseen, bool isOpen)
    {
        var changed = _briefingVisible != visible;
        _briefingVisible = visible;
        if (_briefing.GetActive() != isOpen)
        {
            _applyingBriefing = true;
            try { _briefing.SetActive(isOpen); }
            finally { _applyingBriefing = false; }
        }
        if (hasUnseen) _briefing.AddCssClass("accent");
        else _briefing.RemoveCssClass("accent");
        if (!changed) return;
        ApplyTrailingVisibility();
        RebuildMenu();
    }

    public void SetWhatsNewVisible(bool visible)
    {
        if (_whatsNewVisible == visible) return;
        _whatsNewVisible = visible;
        ApplyTrailingVisibility();
        RebuildMenu();
    }

    public void SetAccountVisible(bool visible)
    {
        if (_accountVisible == visible) return;
        _accountVisible = visible;
        ApplyTrailingVisibility();
        RebuildMenu();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BackRequested = null;
        SidebarToggleRequested = null;
        SynchronizeRequested = null;
        ModeRequested = null;
        BriefingRequested = null;
        WhatsNewRequested = null;
        AccountRequested = null;
        _target = null;
        _synchronize.Clicked -= OnSynchronizeClicked;
        _synchronize.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var subscription in _modeSubscriptions) subscription.Dispose();
        _modeSubscriptions.Clear();
    }

#if DEBUG
    internal string DescribeState() => string.Join('\n', new[]
    {
        $"title={_title.GetText()}",
        $"subtitle={_subtitleText}",
        $"subtitleVisible={_subtitle.GetVisible()}",
        $"back={_back.GetVisible()}",
        $"sidebarToggle={_sidebarToggle.GetActive()}",
        $"modesVisible={_modes.GetVisible()}",
        $"modesCurrent={_currentMode}",
        $"syncVisible={_synchronize.Root.GetVisible()}",
        $"syncSensitive={_synchronize.Root.GetSensitive()}",
        $"syncBusy={_synchronize.IsSynchronizing}",
        $"syncToolTip={_synchronize.ToolTip}",
        $"syncAttention={_synchronize.Attention ?? string.Empty}",
        $"searchSensitive={_search.GetSensitive()}",
        $"searchText={_search.GetText()}",
        $"briefingVisible={_briefing.GetVisible()}",
        $"whatsNewVisible={_whatsNew.GetVisible()}",
        $"accountVisible={_account.GetVisible()}",
        $"compact={_compact}",
    });
#endif

    private void OnSynchronizeClicked() => SynchronizeRequested?.Invoke();

    private void ClearModes()
    {
        foreach (var subscription in _modeSubscriptions) subscription.Dispose();
        _modeSubscriptions.Clear();
        while (_modesList.GetFirstChild() is { } child) _modesList.Remove(child);
    }

    private void ApplySubtitleVisibility()
    {
        var visible = !_compact && _subtitleText.Length > 0;
        if (_subtitle.GetVisible() != visible) _subtitle.SetVisible(visible);
    }

    private void ApplyTrailingVisibility()
    {
        _briefing.SetVisible(_briefingVisible && !_compact);
        _whatsNew.SetVisible(_whatsNewVisible && !_compact);
        _account.SetVisible(_accountVisible && !_compact);
    }

    private void RebuildMenu()
    {
        var menu = Gio.Menu.New();
        var whatsNewInOverflow = false;
        if (_compact && (_briefingVisible || _whatsNewVisible || _accountVisible))
        {
            var overflow = Gio.Menu.New();
            if (_briefingVisible) overflow.Append(Translator.DailyBriefing_Menu, AppActions.Detailed(AppActions.Briefing));
            if (_whatsNewVisible) overflow.Append(Translator.WhatsNew_WindowTitle, AppActions.Detailed(AppActions.WhatsNew));
            if (_accountVisible) overflow.Append(Translator.WinoAccount_Titlebar_SignedOutTitle, AppActions.Detailed(AppActions.Account));
            whatsNewInOverflow = _whatsNewVisible;
            menu.AppendSection(null, overflow);
        }

        var main = Gio.Menu.New();
        main.Append(Translator.MenuSettings, AppActions.Detailed(AppActions.Preferences));
        main.Append(Translator.Settings_KeyboardShortcuts_Title, AppActions.Detailed(AppActions.Shortcuts));
        if (!whatsNewInOverflow) main.Append(Translator.WhatsNew_WindowTitle, AppActions.Detailed(AppActions.WhatsNew));
        main.Append(string.Format(Translator.MacOSMenu_About, AppActions.ApplicationName), AppActions.Detailed(AppActions.About));
        main.Append(string.Format(Translator.MacOSMenu_Quit, AppActions.ApplicationName), AppActions.Detailed(AppActions.Quit));
        menu.AppendSection(null, main);
        _menu.SetMenuModel(menu);
    }

    private void SearchChanged()
    {
        var text = _search.GetText();
        if (_applyingSearchText is not null)
        {
            if (_applyingSearchText == text) _applyingSearchText = null;
            return;
        }
        // An emptied field is a text change, not a clear: the target keeps a filter-only search (macOS ShellToolbar).
        if (_target is { } target) Observe(target.SearchTextChangedAsync(text));
    }

    private void SearchActivated()
    {
        if (_target is not { } target) return;
        var text = _search.GetText();
        Observe(text.Length == 0 ? target.SearchClearedAsync() : target.SearchSubmittedAsync(text));
    }

    private void SearchStopped()
    {
        SetSearchText(string.Empty);
        if (_target is { } target) Observe(target.SearchClearedAsync());
        if (_header.GetRoot() is Gtk.Window window) window.SetFocus(null);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _error(exception); }
    }
}
