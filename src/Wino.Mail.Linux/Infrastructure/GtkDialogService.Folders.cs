using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Folders;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

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

    public Task<IMailItemFolder?> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
        => PresentAsync<IMailItemFolder?>(owner => FolderPickerDialog.PresentAsync(owner, [.. availableFolders ?? []], null), null);

    public async Task<IMailItemFolder?> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
    {
        var structure = await folderService.GetFolderStructureForAccountAsync(accountId, false);
        return await ShowMoveMailFolderDialogAsync(structure?.Folders ?? []);
    }

    /// <summary>The Windows flow: pick, save, report, then synchronize the folders again.</summary>
    public async Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
    {
        try
        {
            // Special-folder roles belong to the account's own folders only.
            var folders = (await folderService.GetFoldersAsync(accountId) ?? [])
                .Where(static folder => folder.Scope == MailFolderScope.Personal && !folder.IsContainer)
                .ToList();
            var configuration = await PresentAsync<SystemFolderConfiguration?>(owner => SystemFolderConfigurationDialog.PresentAsync(owner, folders), null);
            if (configuration is null) return;
            await folderService.UpdateSystemFolderConfigurationAsync(accountId, configuration);
            InfoBarMessage(Translator.SystemFolderConfigSetupSuccess_Title, Translator.SystemFolderConfigSetupSuccess_Message, InfoBarMessageType.Success);
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(accountId));
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions { AccountId = accountId, Type = MailSynchronizationType.FullFolders }));
        }
        catch (Exception exception)
        {
            InfoBarMessage(Translator.Error_FailedToSetupSystemFolders_Title, exception.Message, InfoBarMessageType.Error);
        }
    }
}
