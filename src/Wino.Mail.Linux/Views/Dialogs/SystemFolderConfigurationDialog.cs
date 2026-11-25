using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>The special folder setup (Windows <c>SystemFolderConfigurationDialog</c>): five folder choices with the same validation.</summary>
internal static class SystemFolderConfigurationDialog
{
    /// <summary>Main thread. Null when cancelled.</summary>
    public static async Task<SystemFolderConfiguration?> PresentAsync(Gtk.Widget owner, List<MailItemFolder> folders)
    {
        var form = new FormDialog(Translator.SettingsConfigureSpecialFolders_Title,
            $"{Translator.SystemFolderConfigDialog_MessageFirstLine}\n{Translator.SystemFolderConfigDialog_MessageSecondLine}",
            Translator.Buttons_SaveConfiguration)
        {
            ContentWidth = 460,
        };
        var group = form.AddGroup();
        // "—" is "no folder", the empty Windows combo box; a symbol, not a string to translate.
        string[] names = ["—", .. folders.Select(static folder => folder.ResolvedDisplayName ?? string.Empty)];
        var sent = Row(Translator.SystemFolderConfigDialog_SentFolderHeader, Translator.SystemFolderConfigDialog_SentFolderDescription, SpecialFolderType.Sent);
        var draft = Row(Translator.SystemFolderConfigDialog_DraftFolderHeader, Translator.SystemFolderConfigDialog_DraftFolderDescription, SpecialFolderType.Draft);
        var archive = Row(Translator.SystemFolderConfigDialog_ArchiveFolderHeader, Translator.SystemFolderConfigDialog_ArchiveFolderDescription, SpecialFolderType.Archive);
        var trash = Row(Translator.SystemFolderConfigDialog_DeletedFolderHeader, Translator.SystemFolderConfigDialog_DeletedFolderDescription, SpecialFolderType.Deleted);
        var junk = Row(Translator.SystemFolderConfigDialog_JunkFolderHeader, Translator.SystemFolderConfigDialog_JunkFolderDescription, SpecialFolderType.Junk);
        Adw.ComboRow[] rows = [sent, draft, archive, trash, junk];

        form.Validate = () =>
        {
            var chosen = rows.Select(Selected).OfType<MailItemFolder>().ToArray();
            // Both references end with the duplicate text when both rules fail.
            if (chosen.DistinctBy(static folder => folder.Id).Count() != chosen.Length) return Translator.SystemFolderConfigDialogValidation_DuplicateSystemFolders;
            return chosen.Any(static folder => folder.SpecialFolderType == SpecialFolderType.Inbox) ? Translator.SystemFolderConfigDialogValidation_InboxSelected : null;
        };
#if DEBUG
        DebugOpened?.Invoke(form, rows, names);
#endif
        var confirmed = await form.PresentAsync(owner);
#if DEBUG
        DebugOpened?.Invoke(null, rows, names);
#endif
        return confirmed ? new SystemFolderConfiguration(Selected(sent), Selected(draft), Selected(archive), Selected(trash), Selected(junk)) : null;

        Adw.ComboRow Row(string title, string description, SpecialFolderType type)
        {
            var row = Adw.ComboRow.New();
            row.SetUseMarkup(false);
            row.SetTitle(title);
            row.SetSubtitle(description);
            row.SetModel(Gtk.StringList.New(names));
            row.SetSelected((uint)(folders.FindIndex(folder => folder.SpecialFolderType == type) + 1));
            form.Own(GtkSignals.OnNotify(row, "selected", () => form.ShowError(null)));
            group.Add(row);
            return row;
        }

        MailItemFolder? Selected(Adw.ComboRow row)
        {
            var index = (int)row.GetSelected() - 1;
            return index >= 0 && index < folders.Count ? folders[index] : null;
        }
    }

#if DEBUG
    /// <summary>The open dialog (null when it closed), its five rows and the names of their model.</summary>
    internal static event Action<FormDialog?, Adw.ComboRow[], string[]>? DebugOpened;
#endif
}
