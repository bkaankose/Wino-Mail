#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-dialog</c> <c>s</c>: opens one dialog through <see cref="IMailDialogService"/> and logs
/// <c>Dialog {Kind} answered {Result}</c>; files are logged as count and total bytes only. Kinds: message, confirm,
/// discard, three, custom, harddelete, text, recurring, notsupported, readonly, files, metadata, content, save, folder, form.
/// </summary>
internal static class DialogDebugActions
{
    private const string Sample = "Tom & Jerry <b>not bold</b>";

    public static void Register(Adw.Application application, Func<IServiceProvider?> services, Func<Gtk.Window?> mainWindow)
    {
        var action = Gio.SimpleAction.New("debug-dialog", GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) =>
        {
            var kind = args.Parameter?.GetString(out var _) ?? string.Empty;
            if (services()?.GetService<IMailDialogService>() is not { } dialogs)
            {
                Serilog.Log.Error("Debug action debug-dialog failed: the app has not started.");
                return;
            }
            _ = RunAsync(kind, dialogs, mainWindow);
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(string kind, IMailDialogService dialogs, Func<Gtk.Window?> mainWindow)
    {
        try
        {
            object? result = kind switch
            {
                "message" => await Done(dialogs.ShowMessageAsync(Sample, Sample, WinoCustomMessageDialogIcon.Information)),
                "confirm" => await dialogs.ShowConfirmationDialogAsync(Sample, Sample, Translator.Buttons_Yes),
                "discard" => await dialogs.ShowConfirmationDialogAsync(Sample, Translator.DialogMessage_DiscardDraftConfirmationTitle, Translator.Buttons_Yes),
                "three" => await dialogs.ShowThreeButtonDialogAsync(Sample, Sample, Translator.Buttons_Save, Translator.Buttons_Discard, Translator.Buttons_Cancel),
                "custom" => await dialogs.ShowWinoCustomMessageDialogAsync(Sample, Sample, Translator.Buttons_Yes, WinoCustomMessageDialogIcon.Question,
                    Translator.Buttons_No, "Debug_DontAskAgain_F07"),
                "harddelete" => await dialogs.ShowHardDeleteConfirmationAsync(),
                "text" => await dialogs.ShowTextInputDialogAsync("a & b <c>", Sample, Sample, Translator.Buttons_Save),
                "recurring" => await dialogs.ShowRecurringEventDeleteDialogAsync(),
                "notsupported" => Done(dialogs.ShowNotSupportedMessage),
                "readonly" => Done(dialogs.ShowReadOnlyCalendarMessage),
                "files" => Describe(await dialogs.PickFilesAsync(".eml", "*")),
                "metadata" => Describe(await dialogs.PickFilesMetadataAsync()),
                "content" => $"{(await dialogs.PickWindowsFileContentAsync()).Length} bytes",
                "save" => await dialogs.PickFilePathAsync("test.eml"),
                "folder" => await dialogs.PickWindowsFolderAsync(),
                "form" => await FormAsync(mainWindow),
                _ => throw new ArgumentException($"'{kind}' is not a dialog kind."),
            };
            Serilog.Log.Information("Dialog {Kind} answered {Result}", kind, result switch { null => "null", "" => "(empty)", _ => result });
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-dialog {Kind} failed.", kind); }
    }

    private static async Task<object?> Done(Task task)
    {
        await task;
        return "done";
    }

    private static string Done(Action show)
    {
        show();
        return "shown";
    }

    private static string Describe(List<Wino.Core.Domain.Models.Common.SharedFile> files) => $"{files.Count} files, {files.Sum(static file => (long)file.Data.Length)} bytes";

    private static string Describe(List<Wino.Core.Domain.Models.Common.PickedFileMetadata> files) => $"{files.Count} files, {files.Sum(static file => file.Size)} bytes";

    private static Task<bool> FormAsync(Func<Gtk.Window?> mainWindow)
    {
        if (mainWindow() is not { } owner) throw new InvalidOperationException("There is no main window.");
        var form = new FormDialog(Sample, Sample, Translator.Buttons_Save);
        var row = Adw.EntryRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(Sample);
        form.AddGroup().Add(row);
        form.CanConfirm = () => row.GetText().Length > 0;
        form.Validate = () => row.GetText() == "bad" ? Sample : null;
        form.Own(GtkSignals.OnChanged(row, form.Refresh));
        form.SetInitialFocus(row);
        return form.PresentAsync(owner);
    }
}
#endif
