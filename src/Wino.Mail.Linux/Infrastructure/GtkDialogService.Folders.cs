using Wino.Core.Domain;
using Wino.Core.Domain.Models.Folders;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

// Folder dialogs. ML-09 adds the folder pickers here.
internal sealed partial class GtkDialogService
{
    public Task<FolderRenameDialogResult?> ShowRenameFolderDialogAsync(string folderName, string? displayName, bool canRenameOnServer)
        => PresentAsync<FolderRenameDialogResult?>(async owner =>
        {
            var form = new FormDialog(Translator.DialogMessage_RenameFolderTitle,
                canRenameOnServer ? Translator.RenameFolderDialog_FolderNameDescription : Translator.RenameFolderDialog_SystemFolderNameDescription,
                Translator.FolderOperation_Rename);
            var name = Adw.EntryRow.New();
            name.SetUseMarkup(false);
            name.SetTitle(Translator.RenameFolderDialog_FolderNameHeader);
            name.SetText(folderName ?? string.Empty);
            name.SetSensitive(canRenameOnServer);
            name.SetActivatesDefault(true);
            var display = Adw.EntryRow.New();
            display.SetUseMarkup(false);
            display.SetTitle(Translator.RenameFolderDialog_DisplayNameHeader);
            display.SetText(displayName ?? string.Empty);
            display.SetActivatesDefault(true);
            var group = form.AddGroup();
            group.Add(name);
            group.Add(display);
            var caption = Gtk.Label.New(Translator.RenameFolderDialog_DisplayNameDescription);
            caption.SetWrap(true);
            caption.SetXalign(0);
            caption.AddCssClass("caption");
            caption.AddCssClass("dim-label");
            form.Add(caption);
            form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.GetText());
            form.Own(GtkSignals.OnChanged(name, form.Refresh));
            form.Own(GtkSignals.OnChanged(display, form.Refresh));
            var focus = canRenameOnServer ? name : display;
            form.SetInitialFocus(focus);
            MainLoop.Post(() => focus.SelectRegion(0, -1));
            form.Refresh();
            var confirmed = await form.PresentAsync(owner);
            return confirmed ? new FolderRenameDialogResult(name.GetText().Trim(), display.GetText().Trim()) : null;
        }, null);
}
