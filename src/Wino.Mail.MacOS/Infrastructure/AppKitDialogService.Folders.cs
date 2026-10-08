using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>System folder configuration. Owned by the core dialogs work (WS4).</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>
    /// Windows SystemFolderConfigurationDialog plus the DialogService post-flow: save the configuration,
    /// show the success bar, announce the folder change and request a full folder synchronization.
    /// </summary>
    public async Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
    {
        try
        {
            var folders = await folderService.GetFoldersAsync(accountId);
            var configuration = await PresentAsync(window => ShowSystemFolderSheetAsync(window, folders ?? []));
            if (configuration is null) return;

            await folderService.UpdateSystemFolderConfigurationAsync(accountId, configuration);
            InfoBarMessage(Translator.SystemFolderConfigSetupSuccess_Title, Translator.SystemFolderConfigSetupSuccess_Message, InfoBarMessageType.Success);
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(accountId));
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FullFolders,
            }));
        }
        catch (Exception exception)
        {
            InfoBarMessage(Translator.Error_FailedToSetupSystemFolders_Title, exception.Message, InfoBarMessageType.Error);
        }
    }

    /// <summary>Five folder pop-ups prefilled from the current special folders; null when cancelled.</summary>
    private static async Task<SystemFolderConfiguration?> ShowSystemFolderSheetAsync(NSWindow window, List<MailItemFolder> folders)
    {
        var description = $"{Translator.SystemFolderConfigDialog_MessageFirstLine}\n{Translator.SystemFolderConfigDialog_MessageSecondLine}";
        var form = new FormSheet(Translator.SettingsConfigureSpecialFolders_Title, description, Translator.Buttons_SaveConfiguration, 460);

        // The first entry is "no folder", like the empty Windows ComboBox.
        var names = new List<string> { "—" };
        names.AddRange(folders.Select(folder => folder.FolderName ?? string.Empty));
        var images = new List<NSImage?> { null };
        images.AddRange(folders.Select(folder => WinoIcons.Image(Views.Shell.ShellPaneRows.FolderGlyph(folder.SpecialFolderType), 14)));

        NSPopUpButton Picker(string header, string hint, SpecialFolderType type)
        {
            var index = folders.FindIndex(folder => folder.SpecialFolderType == type);
            var popup = form.AddPopUp(header, names, index + 1, images);
            popup.ToolTip = hint;
            WinoAccessibility.Help(popup, hint);
            return popup;
        }

        var sent = Picker(Translator.SystemFolderConfigDialog_SentFolderHeader, Translator.SystemFolderConfigDialog_SentFolderDescription, SpecialFolderType.Sent);
        var draft = Picker(Translator.SystemFolderConfigDialog_DraftFolderHeader, Translator.SystemFolderConfigDialog_DraftFolderDescription, SpecialFolderType.Draft);
        var archive = Picker(Translator.SystemFolderConfigDialog_ArchiveFolderHeader, Translator.SystemFolderConfigDialog_ArchiveFolderDescription, SpecialFolderType.Archive);
        var trash = Picker(Translator.SystemFolderConfigDialog_DeletedFolderHeader, Translator.SystemFolderConfigDialog_DeletedFolderDescription, SpecialFolderType.Deleted);
        var junk = Picker(Translator.SystemFolderConfigDialog_JunkFolderHeader, Translator.SystemFolderConfigDialog_JunkFolderDescription, SpecialFolderType.Junk);

        MailItemFolder? Selected(NSPopUpButton popup)
        {
            var index = (int)popup.IndexOfSelectedItem - 1;
            return index >= 0 && index < folders.Count ? folders[index] : null;
        }

        // Same order and rules as Windows: Sent, Draft, Archive, Trash, Junk.
        form.Validate = () =>
        {
            var chosen = new[] { Selected(sent), Selected(draft), Selected(archive), Selected(trash), Selected(junk) }.OfType<MailItemFolder>().ToList();
            if (chosen.Select(folder => folder.Id).Distinct().Count() != chosen.Count) return Translator.SystemFolderConfigDialogValidation_DuplicateSystemFolders;
            if (chosen.Any(folder => folder.SpecialFolderType == SpecialFolderType.Inbox)) return Translator.SystemFolderConfigDialogValidation_InboxSelected;
            return null;
        };

        if (!await form.PresentAsync(window)) return null;
        return new SystemFolderConfiguration(Selected(sent)!, Selected(draft)!, Selected(archive)!, Selected(trash)!, Selected(junk)!);
    }
}
