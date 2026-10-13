using Wino.Mail.UIKit.Views.Settings;
using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Presentation;
using Wino.Mail.UIKit.Views.Dialogs;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>System folder configuration (the Mac AppKitDialogService.Folders).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>
    /// Windows SystemFolderConfigurationDialog plus the DialogService post-flow: save the configuration, show the
    /// success banner, announce the folder change and request a full folder synchronization.
    /// </summary>
    public async Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
    {
        try
        {
            var folders = await folderService.GetFoldersAsync(accountId) ?? [];
            var configuration = await PresentAsync(presenter => ShowSystemFolderSheetAsync(presenter, folders), null);
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

    /// <summary>One folder menu per special folder (Sent, Draft, Archive, Trash, Junk), prefilled; null when cancelled.</summary>
    private static async Task<SystemFolderConfiguration?> ShowSystemFolderSheetAsync(UIViewController presenter, List<MailItemFolder> folders)
    {
        var description = $"{Translator.SystemFolderConfigDialog_MessageFirstLine}\n{Translator.SystemFolderConfigDialog_MessageSecondLine}";
        var form = new FormSheet(Translator.SettingsConfigureSpecialFolders_Title, description, Translator.Buttons_SaveConfiguration) { PrefersLargeDetent = true };

        // The first entry is "no folder", like the empty Windows ComboBox.
        var names = new List<string> { "—" };
        names.AddRange(folders.Select(folder => folder.FolderName ?? string.Empty));
        var images = new List<UIImage?> { null };
        images.AddRange(folders.Select(folder => (UIImage?)WinoIcons.Image(SpecialFolderGlyphs.GetGlyph(folder.SpecialFolderType) is var glyph && glyph != WinoIconGlyph.None ? glyph : WinoIconGlyph.Folder, 18)));

        FormSheetPopUp Picker(string header, string hint, SpecialFolderType type)
        {
            var index = folders.FindIndex(folder => folder.SpecialFolderType == type);
            var popup = form.AddPopUp(header, names, index + 1, images);
            WinoAccessibility.Help(popup, hint);
            return popup;
        }

        var sent = Picker(Translator.SystemFolderConfigDialog_SentFolderHeader, Translator.SystemFolderConfigDialog_SentFolderDescription, SpecialFolderType.Sent);
        var draft = Picker(Translator.SystemFolderConfigDialog_DraftFolderHeader, Translator.SystemFolderConfigDialog_DraftFolderDescription, SpecialFolderType.Draft);
        var archive = Picker(Translator.SystemFolderConfigDialog_ArchiveFolderHeader, Translator.SystemFolderConfigDialog_ArchiveFolderDescription, SpecialFolderType.Archive);
        var trash = Picker(Translator.SystemFolderConfigDialog_DeletedFolderHeader, Translator.SystemFolderConfigDialog_DeletedFolderDescription, SpecialFolderType.Deleted);
        var junk = Picker(Translator.SystemFolderConfigDialog_JunkFolderHeader, Translator.SystemFolderConfigDialog_JunkFolderDescription, SpecialFolderType.Junk);

        MailItemFolder? Selected(FormSheetPopUp popup)
        {
            var index = popup.SelectedIndex - 1;
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

        if (!await form.PresentAsync(presenter)) return null;
        // An unset slot is passed as null, as the Windows dialog does; the folder service skips it.
        return new SystemFolderConfiguration(Selected(sent), Selected(draft), Selected(archive), Selected(trash), Selected(junk));
    }
}
