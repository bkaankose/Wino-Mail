using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Requests;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reader's commands at the end of the subject row (shared <see cref="MailReaderCommandLayout"/>): the actions of
/// Settings > Reader actions, Move next to Delete through the folder popover, the theme toggle and Pop out, and More.
/// </summary>
internal sealed partial class MailReaderView
{
    private const int SubjectMinimumWidth = 160;
    private const int ToolbarSpacing = 8;
    private const int ToolbarPaddingWide = 26;
    private const int ToolbarPaddingNarrow = 20;

    private MailReaderCommandBar? _commandBar;
    private MoveFolderPopover? _movePopover;

    partial void BuildCommandBar()
    {
        var bar = _commandBar = new MailReaderCommandBar();
        _commandSlot.SetChild(bar.Root);
        Bindings.Own(bar);
        Bindings.Own(new ActionDisposable(() =>
        {
            _movePopover?.Dispose();
            _movePopover = null;
        }));
    }

    partial void InitializeCommandBar()
    {
        if (_commandBar is not { } bar) return;
        var preferences = ViewModel.PreferencesService;
        bar.ShowsLabels = preferences.IsShowActionLabelsEnabled;
        EventHandler<string> changed = (_, name) =>
        {
            if (name == nameof(IPreferencesService.IsShowActionLabelsEnabled)) RunOnUI(() => bar.ShowsLabels = ViewModel.PreferencesService.IsShowActionLabelsEnabled);
        };
        preferences.PreferenceChanged += changed;
        Bindings.Own(new ActionDisposable(() => preferences.PreferenceChanged -= changed));
        Bind.Bind(nameof(MailRenderingPageViewModel.MenuItems), static vm => vm.MenuItems, _ => UpdateCommandBar());
        WhenWidthChanged((width, state) => bar.SetAvailableWidth(
            width - SubjectMinimumWidth - ToolbarSpacing - (state == ReaderWidthState.Wide ? ToolbarPaddingWide : ToolbarPaddingNarrow)));
        WhenRenavigating(UpdateCommandBar);
#if DEBUG
        WhenDescribing(bar.Describe);
#endif
    }

    partial void CollectPopOutCommand(ref MailReaderCommand? command);

    private void UpdateCommandBar()
    {
        if (_commandBar is not { } bar || IsReleased) return;
        var shortcuts = _provider.GetService<IKeyboardShortcutService>();
        var plan = MailReaderCommandLayout.Build(ViewModel.MenuItems, _currentItem is not null, offerMove: true);
        var commands = new List<MailReaderCommand?>();
        foreach (var slot in plan.Primary)
        {
            if (slot is null)
            {
                if (commands.Count > 0 && commands[^1] is not null) commands.Add(null);
                continue;
            }
            commands.Add(new MailReaderCommand(slot.Operation.ToString(), MailOperationDisplay.GetGlyph(slot.Operation), slot.Title, Run(slot.Operation, slot.Item),
                slot.IsEnabled, Accelerator(shortcuts, slot.Operation)));
        }
        while (commands.Count > 0 && commands[^1] is null) commands.RemoveAt(commands.Count - 1);

        ReaderBuiltInMenuItem[] secondary = [];
        if (plan.Kind == MailReaderCommandPlanKind.Message)
        {
            var builtIns = ViewModel.MenuItems.OfType<ReaderBuiltInMenuItem>().ToArray();
            var divided = false;
            foreach (var builtIn in builtIns.Where(static item => !item.IsSecondaryMenuPreferred))
            {
                if (BuiltInCommand(builtIn.Operation) is not { } command) continue;
                if (!divided && commands.Count > 0)
                {
                    commands.Add(null);
                    divided = true;
                }
                commands.Add(command);
            }
            secondary = [.. builtIns.Where(static item => item.IsSecondaryMenuPreferred)];
        }

        MailOperationMenuItem[] overflow = [.. plan.Overflow];
        bar.SetCommands([.. commands], overflow.Length == 0 && secondary.Length == 0 ? null : builder => BuildMoreMenu(builder, overflow, secondary));
    }

    private MailReaderCommand? BuiltInCommand(ReaderCommandAction action)
    {
        switch (action)
        {
            case ReaderCommandAction.EditorTheme:
                var dark = ViewModel.IsDarkWebviewRenderer;
                return new MailReaderCommand(nameof(ReaderCommandAction.EditorTheme), dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel, _ => ToggleTheme());
            case ReaderCommandAction.PopOut:
                MailReaderCommand? popOut = null;
                CollectPopOutCommand(ref popOut);
                return popOut;
            case ReaderCommandAction.ReaderView:
                return new MailReaderCommand(nameof(ReaderCommandAction.ReaderView), WinoIconGlyph.Document, Translator.Reader_ReaderView, _ => ToggleReaderView(), _currentHtml.Length > 0);
            default:
                return null;
        }
    }

    private string ThemeLabel => ViewModel.IsDarkWebviewRenderer ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private Action<Gtk.Widget> Run(MailOperation operation, MailOperationMenuItem? item)
    {
        if (item is null) return static _ => { };
        return operation switch
        {
            MailOperation.SaveAs => ShowSaveAsMenu,
            MailOperation.Move => ShowMove,
            _ => _ => RunOperation(item),
        };
    }

    private void RunOperation(MailOperationMenuItem item)
    {
        if (!IsReleased) Observe(ViewModel.OperationClickedCommand.ExecuteAsync(item));
    }

    private void BuildMoreMenu(WinoMenuBuilder builder, MailOperationMenuItem[] overflow, ReaderBuiltInMenuItem[] secondary)
    {
        var shortcuts = _provider.GetService<IKeyboardShortcutService>();
        foreach (var item in overflow)
        {
            if (item.Operation == MailOperation.Seperator)
            {
                builder.Separator();
                continue;
            }
            var title = MailOperationDisplay.GetTitle(item.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            if (item.Operation == MailOperation.SaveAs)
            {
                builder.Submenu(title, child =>
                {
                    child.Item(Translator.Buttons_PDF, () => RunOperation(MailOperationMenuItem.Create(MailOperation.SaveAsPdf)));
                    child.Item(Translator.Buttons_EML, () => RunOperation(MailOperationMenuItem.Create(MailOperation.SaveAsEml)));
                }, item.IsEnabled);
                continue;
            }
            if (item.Operation == MailOperation.Move)
            {
                builder.Item(title, () => ShowMove(_commandBar?.MoreButton ?? _commandSlot), item.IsEnabled);
                continue;
            }
            builder.Item(title, () => RunOperation(item), item.IsEnabled, Accelerator(shortcuts, item.Operation));
        }
        if (secondary.Length == 0) return;
        builder.Separator();
        foreach (var builtIn in secondary)
        {
            switch (builtIn.Operation)
            {
                case ReaderCommandAction.ReaderView:
                    builder.Check(Translator.Reader_ReaderView, ViewModel.IsReaderViewEnabled, _ => ToggleReaderView(), _currentHtml.Length > 0);
                    break;
                case ReaderCommandAction.EditorTheme:
                    builder.Item(ThemeLabel, ToggleTheme);
                    break;
                case ReaderCommandAction.PopOut:
                    MailReaderCommand? popOut = null;
                    CollectPopOutCommand(ref popOut);
                    if (popOut is { } command) builder.Item(command.Title, () => command.Run(_commandBar?.MoreButton ?? _commandSlot), command.IsEnabled);
                    break;
            }
        }
    }

    private void ShowSaveAsMenu(Gtk.Widget anchor)
    {
        if (IsReleased) return;
        var menu = WinoMenus.Create()
            .Item(Translator.Buttons_PDF, () => RunOperation(MailOperationMenuItem.Create(MailOperation.SaveAsPdf)))
            .Item(Translator.Buttons_EML, () => RunOperation(MailOperationMenuItem.Create(MailOperation.SaveAsEml)))
            .Build();
        ContextMenu.Show(anchor, menu);
    }

    private void ShowMove(Gtk.Widget anchor)
    {
        if (IsReleased || _currentItem is not { } item) return;
        _movePopover?.Dispose();
        _movePopover = null;
        Observe(ShowMoveAsync(anchor, item));
    }

    private async Task ShowMoveAsync(Gtk.Widget anchor, MailItemViewModel item)
    {
        var coordinator = _provider.GetRequiredService<WinoRequestCoordinator>();
        var popover = await MoveFolderPopover.ShowForAsync(anchor, [item], request => coordinator.ExecuteMailOperationAsync(request),
            _provider.GetRequiredService<IFolderService>(), Dispatcher);
        if (IsReleased)
        {
            popover?.Dispose();
            return;
        }
        _movePopover = popover;
    }

    private void ToggleTheme()
    {
        if (IsReleased) return;
        var dark = !ViewModel.IsDarkWebviewRenderer;
        ViewModel.IsDarkWebviewRenderer = dark;
        ViewModel.RememberReaderTheme(dark);
    }

    private void ToggleReaderView()
    {
        if (IsReleased) return;
        ViewModel.IsReaderViewEnabled = !ViewModel.IsReaderViewEnabled;
        if (_currentHtml.Length > 0) Observe(RenderActiveContentAsync());
    }

    private static string? Accelerator(IKeyboardShortcutService? shortcuts, MailOperation operation)
        => shortcuts is null ? null : MailShortcutHints.Accelerator(shortcuts, operation);

#if DEBUG
    internal MailReaderCommandBar? DebugCommandBar => _commandBar;

    /// <summary>Runs the command with that id whether it is on the bar or in More; false when the reader has none.</summary>
    internal bool DebugRunCommand(string id)
    {
        if (_commandBar is not { } bar) return false;
        if (id == nameof(ReaderCommandAction.EditorTheme)) { ToggleTheme(); return true; }
        if (id == nameof(ReaderCommandAction.ReaderView)) { ToggleReaderView(); return true; }
        var item = ViewModel.MenuItems.OfType<MailOperationMenuItem>().FirstOrDefault(candidate => candidate.Operation.ToString() == id);
        if (id == nameof(MailOperation.Move) && _currentItem is not null)
        {
            ShowMove(bar.AnchorFor(id));
            return true;
        }
        if (item is null) return false;
        Run(item.Operation, item)(bar.AnchorFor(id));
        return true;
    }
#endif
}
