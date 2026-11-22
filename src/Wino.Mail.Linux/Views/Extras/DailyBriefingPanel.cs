using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Extras;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Extras;

/// <summary>
/// The Daily briefing panel (Windows DailyBriefingPanel, macOS DailyBriefingPanelViewController): a header with the
/// show-ignored toggle and close, the day strip, and the card list or one of the loading, empty, filtered, unavailable
/// and error states. It loads when the shell shows it and marks the briefing viewed when the shell hides it. Created
/// once by <see cref="Infrastructure.DailyBriefingPresenter"/> and kept, as on macOS.
/// </summary>
internal sealed class DailyBriefingPanel : IDisposable
{
    public const int PanelWidth = 400;

    private readonly DailyBriefingPanelViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly Func<Action?> _close;
    private readonly Action? _openMailClassification;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.ToggleButton _showIgnored;
    private readonly WinoDatePager _pager = new(Translator.DailyBriefing_NextDay, Translator.DailyBriefing_PreviousDay);
    private readonly Gtk.Box _list = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.ScrolledWindow _scroll = Gtk.ScrolledWindow.New();
    private readonly Gtk.Widget _loading;
    private readonly Gtk.Widget _empty;
    private readonly Gtk.Widget _filtered;
    private readonly Gtk.Widget _unavailable;
    private readonly Gtk.Widget _error;
    private readonly Gtk.Label _errorCaption;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<(DailyBriefingItem Item, WinoBriefingCard Card, PropertyChangedEventHandler Handler)> _cards = [];
    private readonly PropertyChangedEventHandler _propertyChanged;
    private readonly NotifyCollectionChangedEventHandler _itemsChanged;
    private readonly NotifyCollectionChangedEventHandler _datesChanged;
    private readonly EventHandler _closeRequested;
    private bool _shown;
    private bool _applying;
    private bool _disposed;

    public DailyBriefingPanel(DailyBriefingPanelViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, Func<Action?> close, Action? openMailClassification)
    {
        WinoLiveObjects.Track(this);
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _logger = logger;
        _close = close;
        _openMailClassification = openMailClassification;
        _root.AddCssClass("wino-briefing-panel");
        _root.SetSizeRequest(PanelWidth, -1);
        WinoA11y.Label(_root, Translator.DailyBriefing_Title);

        var icon = WinoIcons.Label(WinoIconGlyph.DailyBriefing, 18);
        icon.AddCssClass("accent");
        var title = Gtk.Label.New(Translator.DailyBriefing_Title);
        title.AddCssClass("heading");
        title.SetXalign(0);
        title.SetHexpand(true);
        _showIgnored = WinoIcons.ToggleButton(WinoIconGlyph.Eye, Translator.DailyBriefing_ShowIgnored, 15);
        _showIgnored.SetTooltipText(Translator.DailyBriefing_ShowIgnored);
        var closeButton = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.DailyBriefing_ClosePane, 14);
        closeButton.SetTooltipText(Translator.DailyBriefing_ClosePane);
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        header.AddCssClass("wino-briefing-header");
        foreach (var part in (Gtk.Widget[])[icon, title, _showIgnored, closeButton])
        {
            part.SetValign(Gtk.Align.Center);
            header.Append(part);
        }
        icon.SetMarginEnd(4);
        _subscriptions.Add(GtkSignals.OnToggled(_showIgnored, () =>
        {
            if (!_applying) _viewModel.IsShowingIgnored = _showIgnored.GetActive();
        }));
        _subscriptions.Add(GtkSignals.OnClicked(closeButton, () => _close()?.Invoke()));
        _pager.SelectionChanged += PagerChanged;

        _list.SetMarginStart(8);
        _list.SetMarginEnd(8);
        _list.SetMarginTop(8);
        _list.SetMarginBottom(8);
        _scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroll.SetVexpand(true);
        _scroll.SetChild(_list);

        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(24, 24);
        _loading = State(spinner, null, Translator.DailyBriefing_ReadingLocal, out _);
        _empty = State(Glyph(WinoIconGlyph.Info), Translator.DailyBriefing_EmptyTitle, Translator.DailyBriefing_EmptyMessage, out _);
        _filtered = State(Glyph(WinoIconGlyph.EyeOff), Translator.DailyBriefing_FilteredEmptyTitle, Translator.DailyBriefing_FilteredEmptyMessage, out _);
        _unavailable = State(Glyph(WinoIconGlyph.Info), Translator.DailyBriefing_UnavailableTitle, Translator.DailyBriefing_UnavailableMessage, out _);
        if (_openMailClassification is { } open && _unavailable is Gtk.Box unavailable)
        {
            var link = Gtk.Button.NewWithLabel(Translator.DailyBriefing_OpenMailClassification);
            link.AddCssClass("flat");
            link.AddCssClass("wino-intelligence-link");
            link.SetHalign(Gtk.Align.Center);
            WinoA11y.Label(link, Translator.DailyBriefing_OpenMailClassification);
            _subscriptions.Add(GtkSignals.OnClicked(link, () =>
            {
                _close()?.Invoke();
                open();
            }));
            unavailable.Append(link);
        }
        var errorGlyph = Glyph(WinoIconGlyph.AlertCircle);
        errorGlyph.AddCssClass("error");
        _error = State(errorGlyph, Translator.DailyBriefing_LocalErrorTitle, string.Empty, out _errorCaption);
        if (_error is Gtk.Box error)
        {
            var retry = Gtk.Button.NewWithLabel(Translator.DailyBriefing_Retry);
            retry.SetHalign(Gtk.Align.Center);
            WinoA11y.Label(retry, Translator.DailyBriefing_Retry);
            _subscriptions.Add(GtkSignals.OnClicked(retry, () => _viewModel.RetryCommand.Execute(null)));
            error.Append(retry);
        }

        var content = Gtk.Overlay.New();
        content.SetVexpand(true);
        content.SetChild(_scroll);
        foreach (var state in (Gtk.Widget[])[_loading, _empty, _filtered, _unavailable, _error]) content.AddOverlay(state);

        _root.Append(header);
        _root.Append(_pager.Root);
        _root.Append(content);

        _propertyChanged = (_, args) => Ui(() => PropertyChanged(args.PropertyName));
        _itemsChanged = (_, _) => Ui(RefreshItems);
        _datesChanged = (_, _) => Ui(RefreshDates);
        _closeRequested = (_, _) => Ui(() => _close()?.Invoke());
        _viewModel.PropertyChanged += _propertyChanged;
        _viewModel.Items.CollectionChanged += _itemsChanged;
        _viewModel.Dates.CollectionChanged += _datesChanged;
        _viewModel.CloseRequested += _closeRequested;
        RefreshDates();
        RefreshItems();
        RefreshState();
        SetShowIgnored();
    }

    public Gtk.Widget Root => _root;

    public DailyBriefingPanelViewModel ViewModel => _viewModel;

    public bool IsShown => _shown;

    /// <summary>The shell showed (true) or hid (false) the panel: showing loads, hiding marks the briefing viewed.</summary>
    public void SetShown(bool shown)
    {
        if (_disposed || shown == _shown) return;
        _shown = shown;
        Observe(shown ? _viewModel.InitializeAsync() : _viewModel.MarkViewedAsync());
    }

    public string Describe()
    {
        var vm = _viewModel;
        return $"shown={_shown} loading={vm.IsLoading} unavailable={vm.IsUnavailable} empty={vm.IsEmpty} filtered={vm.IsFilteredEmpty} error='{vm.LoadError}' "
            + $"content={vm.ShowContent} dates={vm.Dates.Count} selected={vm.SelectedDateIndex}:'{_pager.SelectedText}' cards={_cards.Count} ignored={vm.IsShowingIgnored} "
            + $"visibleState={string.Join(",", ((Gtk.Widget[])[_loading, _empty, _filtered, _unavailable, _error, _scroll]).Select(static w => w.GetVisible() ? "1" : "0"))}";
    }

    internal WinoDatePager Pager => _pager;

    internal IReadOnlyList<WinoBriefingCard> Cards => [.. _cards.Select(static c => c.Card)];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= _propertyChanged;
        _viewModel.Items.CollectionChanged -= _itemsChanged;
        _viewModel.Dates.CollectionChanged -= _datesChanged;
        _viewModel.CloseRequested -= _closeRequested;
        _pager.SelectionChanged -= PagerChanged;
        ClearCards();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _pager.Dispose();
    }

    private void PagerChanged() => _viewModel.SelectedDateIndex = _pager.SelectedIndex;

    private void PropertyChanged(string? name)
    {
        switch (name)
        {
            case nameof(DailyBriefingPanelViewModel.SelectedDateIndex):
                _pager.Select(_viewModel.SelectedDateIndex);
                break;
            case nameof(DailyBriefingPanelViewModel.IsShowingIgnored):
                SetShowIgnored();
                break;
            case nameof(DailyBriefingPanelViewModel.NewItemCount):
            case nameof(DailyBriefingPanelViewModel.HasIgnoredItems):
                break;
            default:
                RefreshState();
                break;
        }
    }

    private void SetShowIgnored()
    {
        _applying = true;
        try { _showIgnored.SetActive(_viewModel.IsShowingIgnored); }
        finally { _applying = false; }
    }

    private void RefreshDates()
    {
        _pager.SetItems([.. _viewModel.Dates.Select(static date => date.DisplayName)]);
        _pager.Select(_viewModel.SelectedDateIndex);
    }

    private void RefreshState()
    {
        var vm = _viewModel;
        _loading.SetVisible(vm.IsLoading);
        _unavailable.SetVisible(vm.IsUnavailable && !vm.IsLoading);
        _error.SetVisible(vm.HasLoadError && !vm.IsLoading && !vm.IsUnavailable);
        _errorCaption.SetText(vm.LoadError);
        _errorCaption.SetVisible(vm.HasLoadError);
        _empty.SetVisible(vm.IsEmpty && !vm.IsLoading && !vm.IsUnavailable && !vm.HasLoadError);
        _filtered.SetVisible(vm.ShowFilteredEmpty);
        _scroll.SetVisible(vm.ShowContent);
    }

    /// <summary>The list holds a day's cards (a few dozen at most), so it is rebuilt whole when the items change.</summary>
    private void RefreshItems()
    {
        ClearCards();
        foreach (var item in _viewModel.Items.ToList())
        {
            var card = new WinoBriefingCard();
            card.Apply(Describe(item));
            var captured = item;
            card.PrimaryActivated += () => _viewModel.ExecutePrimaryActionCommand.Execute(captured);
            card.IgnoreActivated += () => _viewModel.IgnoreCommand.Execute(captured);
            PropertyChangedEventHandler handler = (_, _) => Ui(() => card.Apply(Describe(captured)));
            item.PropertyChanged += handler;
            _cards.Add((item, card, handler));
            _list.Append(card.Root);
        }
    }

    private void ClearCards()
    {
        foreach (var (item, card, handler) in _cards)
        {
            item.PropertyChanged -= handler;
            card.Dispose();
            _list.Remove(card.Root);
        }
        _cards.Clear();
    }

    private static WinoBriefingCardModel Describe(DailyBriefingItem item)
    {
        var card = DailyBriefingCardContent.From(item);
        return new WinoBriefingCardModel(card.Headline, card.Time, card.IsNew, card.Summary, card.Urgency, [.. card.Labels],
            card.ActionGlyph, card.ActionText, card.IgnoreGlyph, card.IgnoreText, card.CanToggleIgnore);
    }

    private static Gtk.Label Glyph(WinoIconGlyph glyph)
    {
        var label = WinoIcons.Label(glyph, 28);
        label.AddCssClass("dim-label");
        return label;
    }

    private static Gtk.Widget State(Gtk.Widget icon, string? title, string caption, out Gtk.Label captionLabel)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetValign(Gtk.Align.Center);
        box.SetHalign(Gtk.Align.Center);
        box.SetMarginStart(24);
        box.SetMarginEnd(24);
        icon.SetHalign(Gtk.Align.Center);
        box.Append(icon);
        if (title is not null)
        {
            var heading = Gtk.Label.New(title);
            heading.AddCssClass("heading");
            heading.SetWrap(true);
            heading.SetJustify(Gtk.Justification.Center);
            box.Append(heading);
        }
        captionLabel = Gtk.Label.New(caption);
        captionLabel.AddCssClass("dim-label");
        captionLabel.SetWrap(true);
        captionLabel.SetJustify(Gtk.Justification.Center);
        box.Append(captionLabel);
        box.SetVisible(false);
        return box;
    }

    private void Ui(Action action) => _ = _dispatcher.ExecuteOnUIThread(() =>
    {
        if (!_disposed) action();
    });

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(DailyBriefingPanel)); }
    }
}
