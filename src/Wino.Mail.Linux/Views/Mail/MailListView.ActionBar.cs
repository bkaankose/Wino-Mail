using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The mail list action bar (macOS <c>MailListPageViewController.Bars</c>, Windows <c>ActionBarGrid</c>): the
/// ViewModel's <c>ActionItems</c> as a command bar above the list header, acting on the selection. It follows the
/// "Mail list action bar" and "Show action labels" preferences live; Move opens the folder popover at its button.
/// </summary>
internal sealed partial class MailListView
{
    // The host's margins (6 + 6) and its CSS padding (.wino-action-bar: 8 + 8); keep both in step.
    private const int ActionBarInset = 28;

    private Gtk.Box? _actionBarHost;
    private MailReaderCommandBar? _actionBar;
    private MainLoop.Coalescer? _actionBarWidth;
    private int _actionBarProbed;

    internal MailReaderCommandBar? ActionBar => _actionBar;

    internal bool IsActionBarVisible => _actionBarHost?.GetVisible() == true;

    partial void SetUpActionBar()
    {
        var self = _self;
        Bind.Bind(nameof(MailListPageViewModel.ActionItems), static vm => vm.ActionItems, _ => self.Value?.UpdateActionBar());
        Bind.Bind(nameof(MailListPageViewModel.SelectedItemsCount), static vm => vm.SelectedItemsCount, _ => self.Value?.UpdateActionBar());
        var preferences = ViewModel.PreferencesService;
        EventHandler<string> changed = (_, name) =>
        {
            if (name == nameof(IPreferencesService.IsMailListActionBarEnabled)) self.Value?.RunOnUI(() => self.Value?.UpdateActionBar());
            else if (name == nameof(IPreferencesService.IsShowActionLabelsEnabled))
                self.Value?.RunOnUI(() =>
                {
                    if (self.Value?._actionBar is { } bar) bar.ShowsLabels = preferences.IsShowActionLabelsEnabled;
                });
        };
        preferences.PreferenceChanged += changed;
        Bindings.Own(new ActionDisposable(() => preferences.PreferenceChanged -= changed));
        UpdateActionBar();
    }

    private void UpdateActionBar()
    {
        if (IsReleased || _header is null) return;
        var preferences = ViewModel.PreferencesService;
        if (!preferences.IsMailListActionBarEnabled)
        {
            _actionBarHost?.SetVisible(false);
            return;
        }
        var bar = EnsureActionBar(_header);
        _actionBarHost?.SetVisible(true);
        var hasSelection = ViewModel.SelectedItemsCount > 0;
        var commands = new List<MailReaderCommand?>();
        foreach (var item in ViewModel.ActionItems.OfType<MailOperationMenuItem>())
        {
            if (item.Operation == MailOperation.Seperator)
            {
                if (commands.Count > 0 && commands[^1] is not null) commands.Add(null);
                continue;
            }
            var title = item.Operation == MailOperation.SetFlag ? Translator.MailOperation_Flag : MailOperationDisplay.GetTitle(item.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var captured = item;
            commands.Add(new MailReaderCommand(item.Operation.ToString(), MailOperationDisplay.GetGlyph(item.Operation), title, anchor => RunActionBarItem(captured, anchor),
                item.IsEnabled && hasSelection, IsDestructive: item.Operation is MailOperation.SoftDelete or MailOperation.HardDelete));
        }
        if (commands.Count > 0 && commands[^1] is null) commands.RemoveAt(commands.Count - 1);
        bar.SetCommands([.. commands], null);
    }

    private MailReaderCommandBar EnsureActionBar(MailListHeader header)
    {
        if (_actionBar is { } existing) return existing;
        var self = _self;
        var bar = _actionBar = Bindings.Own(new MailReaderCommandBar { ShowsLabels = ViewModel.PreferencesService.IsShowActionLabelsEnabled });
        var host = _actionBarHost = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        host.AccessibleRole = Gtk.AccessibleRole.Toolbar;
        WinoA11y.Label(host, Translator.MacOS_MailList_ActionBar);
        host.AddCssClass(WinoCssClass.ActionBar);
        host.SetMarginTop(5);
        host.SetMarginStart(6);
        host.SetMarginEnd(6);
        bar.Root.SetHexpand(true);
        host.Append(bar.Root);

        var probe = Gtk.DrawingArea.New();
        probe.SetHexpand(true);
        probe.SetContentHeight(0);
        probe.SetCanTarget(false);
        probe.SetCanFocus(false);
        _actionBarWidth = Bindings.Own(new MainLoop.Coalescer(() =>
        {
            if (self.Value is { _actionBar: { } shown } view) shown.SetAvailableWidth(view._actionBarProbed - ActionBarInset);
        }));
        // resize runs during allocation: only the width is stored there, the bar changes on the next turn.
        Bindings.Own(GtkSignals.Connect<Gtk.DrawingArea, Gtk.DrawingArea.ResizeSignalArgs>(h => probe.OnResize += h, h => probe.OnResize -= h, (_, args) =>
        {
            if (self.Value is not { } view) return;
            view._actionBarProbed = args.Width;
            view._actionBarWidth?.Request();
        }));
        header.ActionBarSlot.Append(probe);
        header.ActionBarSlot.Append(host);
        return bar;
    }

    private void RunActionBarItem(MailOperationMenuItem item, Gtk.Widget anchor)
    {
        if (IsReleased) return;
        if (item.Operation == MailOperation.Move)
        {
            MultiSelectionOperation(MailOperation.Move, anchor);
            return;
        }
        Observe(ViewModel.ExecuteTopBarActionCommand.ExecuteAsync(item));
    }
}
