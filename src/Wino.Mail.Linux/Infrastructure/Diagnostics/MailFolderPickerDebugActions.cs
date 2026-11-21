#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-folders</c> <c>as</c> (first mail account): <c>['move-dialog']</c> (<c>PickFolderAsync</c>),
/// <c>['move-selection']</c> (the shared Move of the list selection), <c>['popover']</c> (the multi-selection pane's
/// Move button path, anchored on the list), <c>['filter', text]</c>, <c>['pick', name]</c>, <c>['first']</c> on the open picker,
/// <c>['system-folders']</c>, <c>['system-set', row 0..4, name]</c>, <c>['system-save']</c>, <c>['system-state', path]</c>.
/// </summary>
internal static class MailFolderPickerDebugActions
{
    private static FolderPicker? _picker;
    private static FormDialog? _systemForm;
    private static Adw.ComboRow[] _systemRows = [];
    private static string[] _systemNames = [];
    private static MoveFolderPopover? _popover;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        FolderPicker.DebugOpened += picker => _picker = picker;
        SystemFolderConfigurationDialog.DebugOpened += (form, rows, names) =>
        {
            _systemForm = form;
            _systemRows = rows;
            _systemNames = names;
        };
        var action = Gio.SimpleAction.New("debug-mail-folders", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) => Run(args.Parameter?.GetStrv(out var _) ?? [], services);
        application.AddAction(action);
    }

    private static async void Run(string[] arguments, Func<IServiceProvider?> services)
    {
        try
        {
            if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
            var provider = services() ?? throw new InvalidOperationException("No services.");
            var folders = provider.GetRequiredService<IFolderService>();
            switch (arguments[0])
            {
                case "move-dialog":
                {
                    var account = (await provider.GetRequiredService<IAccountService>().GetAccountsAsync()).First();
                    var picked = await provider.GetRequiredService<IMailDialogService>().PickFolderAsync(account.Id, PickFolderReason.Move, folders);
                    Serilog.Log.Information("Debug action: Folder picker answered {Folder}", picked?.ResolvedDisplayName ?? "null");
                    return;
                }
                case "move-selection":
                {
                    var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                    view.ViewModel.ExecuteMailOperationCommand.Execute(MailOperation.Move);
                    break;
                }
                case "popover":
                {
                    var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                    _popover?.Dispose();
                    _popover = await MoveFolderPopover.ShowForAsync(view.List, [.. view.ViewModel.SelectedItems], view.ViewModel, folders, provider.GetRequiredService<IDispatcher>());
                    Serilog.Log.Information("Debug action: folders popover {Result}", _popover is null ? "shared path" : "shown");
                    return;
                }
                case "filter": Picker().SetFilter(Argument(arguments, 1)); break;
                case "pick":
                {
                    var picker = Picker();
                    var result = picker.PickByName(Argument(arguments, 1));
                    Serilog.Log.Information("Debug action: folders pick {Name} found={Found} warning={Warning}", arguments[1], result, picker.WarningText);
                    return;
                }
                case "first":
                {
                    var picker = Picker();
                    Serilog.Log.Information("Debug action: folders first visible={Visible} picked={Picked}", picker.VisibleCount, picker.PickFirstVisible());
                    return;
                }
                case "system-folders":
                {
                    var account = (await provider.GetRequiredService<IAccountService>().GetAccountsAsync()).First();
                    await provider.GetRequiredService<IMailDialogService>().HandleSystemFolderConfigurationDialogAsync(account.Id, folders);
                    var special = (await folders.GetFoldersAsync(account.Id)).Where(static folder => folder.SpecialFolderType != SpecialFolderType.Other)
                        .Select(static folder => $"{folder.SpecialFolderType}={folder.FolderName}");
                    Serilog.Log.Information("Debug action: system folders returned; {Special}", string.Join(", ", special));
                    return;
                }
                case "system-set":
                {
                    var row = _systemRows[int.Parse(Argument(arguments, 1), System.Globalization.CultureInfo.InvariantCulture)];
                    var index = Array.IndexOf(_systemNames, Argument(arguments, 2));
                    if (index < 0) throw new ArgumentException($"No folder '{arguments[2]}'.");
                    row.SetSelected((uint)index);
                    break;
                }
                case "system-save": (_systemForm ?? throw new InvalidOperationException("The dialog is not open.")).DebugConfirm(); break;
                case "system-state":
                {
                    var text = new StringBuilder();
                    text.Append("open=").Append(_systemForm is not null).Append(" error=").Append(_systemForm?.ErrorText ?? string.Empty).Append('\n');
                    foreach (var row in _systemRows) text.Append(row.GetTitle()).Append('=').Append(_systemNames.ElementAtOrDefault((int)row.GetSelected()) ?? "?").Append('\n');
                    DebugActions.WriteText(Argument(arguments, 1), text.ToString());
                    break;
                }
                default: throw new ArgumentException($"Unknown command '{arguments[0]}'.");
            }
            Serilog.Log.Information("Debug action: folders {Command} visible={Visible} warning={Warning}", string.Join(' ', arguments), _picker?.VisibleCount ?? -1, _picker?.WarningText ?? string.Empty);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-folders failed."); }
    }

    private static FolderPicker Picker() => _picker ?? throw new InvalidOperationException("No folder picker is open.");

    private static string Argument(string[] arguments, int index)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException($"Expected argument {index}.");
}
#endif
