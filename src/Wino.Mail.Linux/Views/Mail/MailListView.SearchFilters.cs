using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The advanced search filters (macOS <c>ShowSearchFilters</c>): opened from the scope bar's Filters token and from
/// "Search filters…" in the filter and sort menu; Search applies through <see cref="ApplySearchFilterEditorAsync"/>.
/// </summary>
internal sealed partial class MailListView
{
    private MailSearchFilterPopover? _filterPopover;

    internal MailSearchFilterPopover? SearchFilterPopover => _filterPopover;

    partial void SetUpSearchFilters()
    {
        var self = _self;
        Action<Gtk.Widget> requested = anchor => self.Value?.ShowSearchFilters(anchor);
        SearchFiltersRequested += requested;
        Bindings.Own(new ActionDisposable(() =>
        {
            if (self.Value is not { } view) return;
            view.SearchFiltersRequested -= requested;
            view.CloseSearchFilters();
        }));
    }

    partial void AppendFilterMenu(WinoMenuBuilder builder)
    {
        var self = _self;
        builder.Separator();
        builder.Item(Translator.MacOS_MailList_SearchFilters, () => MainLoop.Post(() =>
        {
            if (self.Value is { } view && view._header is { } header) view.ShowSearchFilters(header.FilterButton);
        }), icon: WinoIconGlyph.Filter);
    }

    internal void ShowSearchFilters(Gtk.Widget anchor)
    {
        if (IsReleased || anchor.GetRoot() is null) return;
        CloseSearchFilters();
        ViewModel.SearchFilterEditor.Load(ViewModel.SearchScope, _searchText, ViewModel.SearchFilters ?? MailSearchFilters.Empty, !IsSearchReachOnline);
        var self = _self;
        var popover = new MailSearchFilterPopover(ViewModel.SearchFilterEditor, () => self.Value?.Observe(self.Value.ApplySearchFilterEditorAsync()));
        // Unparenting a popover inside its own closed emission crashes GTK: dispose it a turn later.
        popover.Closed += () => MainLoop.Post(() =>
        {
            if (self.Value is { } view && ReferenceEquals(view._filterPopover, popover)) view.CloseSearchFilters();
        });
        _filterPopover = popover;
        popover.Show(anchor);
    }

    internal void CloseSearchFilters()
    {
        var popover = _filterPopover;
        _filterPopover = null;
        popover?.Dispose();
    }

#if DEBUG
    internal Gtk.Widget? DebugFilterAnchor => _header?.FilterButton;
#endif
}
