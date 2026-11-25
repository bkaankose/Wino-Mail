using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.FlaggedMail;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// Flagged mail in To Do (Windows ToDoPage flagged mail list, macOS ToDoPageViewController.FlaggedMail): the flagged
/// messages of every account in place of the task list. The check completes one by clearing its flag; the row opens the
/// message. Rows are rebuilt once per main-loop turn; a row leaves when the shared list drops its item.
/// </summary>
internal sealed partial class ToDoPageView
{
    private const string FlaggedListPage = "list";
    private const string FlaggedEmptyPage = "empty";

    private readonly Gtk.Stack _flaggedStack = Gtk.Stack.New();
    private readonly Gtk.ListBox _flaggedList = Gtk.ListBox.New();
    private readonly List<IDisposable> _flaggedRows = [];
    private MainLoop.Coalescer? _flaggedReload;
    private FlaggedMailItemViewModel[] _flaggedShown = [];

    private Gtk.Widget BuildFlaggedMailSurface()
    {
        _flaggedList.SetSelectionMode(Gtk.SelectionMode.None);
        _flaggedList.AddCssClass("wino-flagged-list");
        _flaggedList.SetMarginStart(16);
        _flaggedList.SetMarginEnd(16);
        _flaggedList.SetMarginBottom(4);
        _flaggedList.SetValign(Gtk.Align.Start);
        WinoA11y.Label(_flaggedList, Translator.ToDoPage_FlaggedMail);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetChild(_flaggedList);

        var empty = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        empty.SetHalign(Gtk.Align.Center);
        empty.SetValign(Gtk.Align.Center);
        empty.SetMarginStart(24);
        empty.SetMarginEnd(24);
        var icon = new WinoIcon(WinoIconGlyph.Flag, 48).Root;
        icon.AddCssClass("dim-label");
        icon.SetMarginBottom(4);
        empty.Append(icon);
        var title = Gtk.Label.New(Translator.ToDoPage_FlaggedMailEmptyTitle);
        title.AddCssClass("heading");
        title.SetWrap(true);
        title.SetJustify(Gtk.Justification.Center);
        title.SetMaxWidthChars(40);
        empty.Append(title);
        var body = Gtk.Label.New(Translator.ToDoPage_FlaggedMailEmptyBody);
        body.AddCssClass("dim-label");
        body.SetWrap(true);
        body.SetJustify(Gtk.Justification.Center);
        body.SetMaxWidthChars(40);
        empty.Append(body);

        _flaggedStack.SetTransitionType(Gtk.StackTransitionType.None);
        _flaggedStack.AddNamed(scroll, FlaggedListPage);
        _flaggedStack.AddNamed(empty, FlaggedEmptyPage);
        _flaggedStack.SetVisible(false);
        var self = _self;
        _flaggedReload = Bindings.Own(new MainLoop.Coalescer(() => self.Value?.ReloadFlaggedMail()));
        Bindings.Own(new ActionDisposable(DisposeFlaggedRows));
        return _flaggedStack;
    }

    private void BindFlaggedMail()
    {
        if (ViewModel.FlaggedMail is not { } flagged) return;
        var self = _self;
        var dispatcher = Dispatcher;
        var reload = _flaggedReload;
        NotifyCollectionChangedEventHandler itemsChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(() => reload?.Request());
        flagged.Items.CollectionChanged += itemsChanged;
        Bindings.Own(new ActionDisposable(() => flagged.Items.CollectionChanged -= itemsChanged));
        PropertyChangedEventHandler loadingChanged = (_, args) =>
        {
            if (args.PropertyName is nameof(FlaggedMailListViewModel.IsLoading) or nameof(FlaggedMailListViewModel.IsEmpty))
                _ = dispatcher.ExecuteOnUIThread(() => reload?.Request());
        };
        flagged.PropertyChanged += loadingChanged;
        Bindings.Own(new ActionDisposable(() => flagged.PropertyChanged -= loadingChanged));
        Bind.Bind(nameof(ToDoPageViewModel.IsFlaggedMailSelected), static vm => vm.IsFlaggedMailSelected, selected =>
        {
            if (self.Value is not { } view) return;
            view._flaggedStack.SetVisible(selected);
            view._listHost.SetVisible(!selected);
            view.ApplyEmptyState();
            reload?.Request();
        });
    }

    private void ReloadFlaggedMail()
    {
        if (IsReleased || ViewModel.FlaggedMail is not { } flagged) return;
        var selected = ViewModel.IsFlaggedMailSelected;
        _flaggedStack.SetVisibleChildName(selected && flagged.IsEmpty ? FlaggedEmptyPage : FlaggedListPage);
        var items = selected ? flagged.Items.ToArray() : [];
        if (items.SequenceEqual(_flaggedShown)) return;
        var watch = Stopwatch.StartNew();
        DisposeFlaggedRows();
        _flaggedList.RemoveAll();
        _flaggedShown = items;
        foreach (var item in items) _flaggedList.Append(FlaggedRow(item));
        Serilog.Log.Debug("Flagged mail rows rebuilt: {Count} rows in {Elapsed} ms", items.Length, watch.Elapsed.TotalMilliseconds);
    }

    private Gtk.ListBoxRow FlaggedRow(FlaggedMailItemViewModel item)
    {
        var complete = WinoIcons.Button(WinoIconGlyph.CheckmarkCircle, Translator.ToDoPage_FlaggedMailComplete, 18);
        complete.SetTooltipText(Translator.ToDoPage_FlaggedMailComplete);
        complete.SetValign(Gtk.Align.Center);
        complete.SetSizeRequest(28, 28);
        complete.SetSensitive(!item.IsCompleting);

        var subject = Gtk.Label.New(item.Subject);
        subject.AddCssClass("heading");
        subject.SetXalign(0);
        subject.SetEllipsize(Pango.EllipsizeMode.End);
        var details = Gtk.Label.New(item.DetailsText);
        details.AddCssClass("caption");
        details.AddCssClass("dim-label");
        details.SetXalign(0);
        details.SetEllipsize(Pango.EllipsizeMode.End);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.SetValign(Gtk.Align.Center);
        text.Append(subject);
        text.Append(details);
        var flag = new WinoIcon(WinoIconGlyph.Flag, 14).Root;
        flag.AddCssClass("error");
        flag.SetValign(Gtk.Align.Center);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        content.SetCanTarget(false);
        text.SetHexpand(true);
        content.Append(text);
        content.Append(flag);
        var open = Gtk.Button.New();
        open.AddCssClass("flat");
        open.SetHexpand(true);
        open.SetChild(content);
        WinoA11y.Label(open, string.IsNullOrEmpty(item.DetailsText) ? item.Subject : item.Subject + ", " + item.DetailsText);

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        row.SetMarginTop(2);
        row.SetMarginBottom(2);
        row.SetSizeRequest(-1, 52);
        row.Append(complete);
        row.Append(open);
        var listRow = Gtk.ListBoxRow.New();
        listRow.SetActivatable(false);
        listRow.SetChild(row);

        var self = _self;
        _flaggedRows.Add(GtkSignals.OnClicked(complete, () =>
        {
            if (self.Value is { } view && !view.IsReleased && view.ViewModel.FlaggedMail is { } flagged) view.Observe(flagged.CompleteCommand.ExecuteAsync(item));
        }));
        _flaggedRows.Add(GtkSignals.OnClicked(open, () =>
        {
            if (self.Value is { } view && !view.IsReleased && view.ViewModel.FlaggedMail is { } flagged) view.Observe(flagged.OpenCommand.ExecuteAsync(item));
        }));
        var dispatcher = Dispatcher;
        PropertyChangedEventHandler completing = (_, args) =>
        {
            if (args.PropertyName == nameof(FlaggedMailItemViewModel.IsCompleting))
                _ = dispatcher.ExecuteOnUIThread(() => complete.SetSensitive(!item.IsCompleting));
        };
        item.PropertyChanged += completing;
        _flaggedRows.Add(new ActionDisposable(() => item.PropertyChanged -= completing));
        return listRow;
    }

    private void DisposeFlaggedRows()
    {
        foreach (var subscription in _flaggedRows) subscription.Dispose();
        _flaggedRows.Clear();
        _flaggedShown = [];
    }

#if DEBUG
    internal string DebugDescribeFlaggedMail()
    {
        var flagged = ViewModel.FlaggedMail;
        var rows = string.Join(" | ", _flaggedShown.Select(static (item, index) => $"{index}:{item.Subject}|{item.DetailsText}|{item.IsCompleting}"));
        return $"available={ViewModel.IsFlaggedMailAvailable} selected={ViewModel.IsFlaggedMailSelected} loading={flagged?.IsLoading} count={flagged?.Items.Count} "
            + $"shown={_flaggedShown.Length} page={(_flaggedStack.GetVisible() ? _flaggedStack.GetVisibleChildName() : "hidden")} listHost={_listHost.GetVisible()} "
            + $"quickAdd={_quickAddHost.GetVisible()} empty={_empty.GetVisible()} title='{_header.Title}' | {rows}";
    }

    internal FlaggedMailItemViewModel? DebugFlaggedItem(int index) => index >= 0 && index < _flaggedShown.Length ? _flaggedShown[index] : null;
#endif
}
