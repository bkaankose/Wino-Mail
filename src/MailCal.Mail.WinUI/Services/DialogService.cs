using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models;
using MailCal.Core.Domain.Models.Accounts;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Contacts;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.MailItem;
using MailCal.Core.Domain.Models.Synchronization;
using MailCal.Editor;
using MailCal.Dialogs;
using MailCal.Mail.Dialogs;
using MailCal.Mail.WinUI;
using MailCal.Mail.WinUI.Extensions;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Mail.ViewModels;
using MailCal.Mail.WinUI.Services;
using MailCal.Messaging.Server;
using MailCal.Messaging.UI;

namespace MailCal.Services;

public class DialogService : DialogServiceBase, IMailDialogService, IMailCalDialogService
{
    public DialogService(INewThemeService themeService,
                         IConfigurationService configurationService,
                         IApplicationResourceManager<ResourceDictionary> applicationResourceManager) : base(themeService, configurationService, applicationResourceManager)
    {
    }

    public void ShowReadOnlyCalendarMessage()
        => InfoBarMessage(
            Translator.CalendarReadOnly_Title,
            Translator.CalendarReadOnly_Message,
            InfoBarMessageType.Warning);

    public async Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
    {
        var createAccountAliasDialog = new CreateAccountAliasDialog()
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(createAccountAliasDialog);

        return createAccountAliasDialog;
    }

#pragma warning disable CS8625
    public async Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null)
#pragma warning restore CS8625
    {
        var dialog = new EditMailCategoryDialog(category)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);
        return dialog.Result;
    }

    public async Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
    {
        try
        {
            var configurableFolder = await folderService.GetFoldersAsync(accountId);

            var systemFolderConfigurationDialog = new SystemFolderConfigurationDialog(configurableFolder)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(systemFolderConfigurationDialog);

            var configuration = systemFolderConfigurationDialog.Configuration;

            if (configuration != null)
            {
                await folderService.UpdateSystemFolderConfigurationAsync(accountId, configuration);

                InfoBarMessage(Translator.SystemFolderConfigSetupSuccess_Title, Translator.SystemFolderConfigSetupSuccess_Message, InfoBarMessageType.Success);

                WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(accountId));

                var options = new MailSynchronizationOptions()
                {
                    AccountId = accountId,
                    Type = MailSynchronizationType.FullFolders,
                };

                WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(options));
            }
        }
        catch (Exception ex)
        {
            InfoBarMessage(Translator.Error_FailedToSetupSystemFolders_Title, ex.Message, InfoBarMessageType.Error);
        }
    }

    public async Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
    {
        var moveDialog = new MoveMailDialog(availableFolders)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(moveDialog);

        return moveDialog.SelectedFolder;
    }

    public async Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
    {
        var visibleFolders = await folderService.GetFolderStructureForAccountAsync(accountId, false);

        return await ShowMoveMailFolderDialogAsync(visibleFolders.Folders);
    }



    public Task<bool> ShowHardDeleteConfirmationAsync()
        => ShowMailCalCustomMessageDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage,
                                           Translator.DialogMessage_HardDeleteConfirmationTitle,
                                           Translator.Buttons_Yes,
                                           MailCalCustomMessageDialogIcon.Warning,
                                           Translator.Buttons_No);

    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title,
                                                                          string description,
                                                                          string primaryButtonText,
                                                                          string secondaryButtonText,
                                                                          string cancelButtonText,
                                                                          MailCalCustomMessageDialogIcon? icon = null)
    {
        var informationContainer = new CustomMessageDialogInformationContainer(
            title,
            description,
            icon ?? MailCalCustomMessageDialogIcon.Information,
            false);

        var dialog = new MailCalMessageDialog(informationContainer, primaryButtonText, secondaryButtonText, cancelButtonText)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        var dialogResult = await HandleDialogPresentationAsync(dialog);

        return dialogResult switch
        {
            ContentDialogResult.Primary => ThreeButtonDialogResult.Primary,
            ContentDialogResult.Secondary => ThreeButtonDialogResult.Secondary,
            _ => ThreeButtonDialogResult.Cancel
        };
    }

    public async Task<bool> ShowServerCertificateTrustDialogAsync(string summary, byte[] certificateRawData)
    {
        while (true)
        {
            var informationContainer = new CustomMessageDialogInformationContainer(
                Translator.GeneralTitle_Warning,
                summary,
                MailCalCustomMessageDialogIcon.Warning,
                false);
            var dialog = new MailCalMessageDialog(informationContainer,
                                               Translator.Buttons_Allow,
                                               Translator.IMAPSetupDialog_CertificateView,
                                               Translator.Buttons_Cancel,
                                               ContentDialogButton.Close)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            var result = await HandleDialogPresentationAsync(dialog);
            if (result == ContentDialogResult.Primary)
                return true;

            if (result != ContentDialogResult.Secondary)
                return false;

            using var certificate = X509CertificateLoader.LoadCertificate(certificateRawData);
            await ShowMessageAsync(
                certificate.ToString(verbose: true),
                Translator.IMAPSetupDialog_CertificateView,
                MailCalCustomMessageDialogIcon.Information);
        }
    }

    public async Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
    {
        var accountPicker = new AccountPickerDialog(availableAccounts)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(accountPicker);

        return accountPicker.PickedAccount ?? null!;
    }

    public async Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations)
    {
        var dialog = new ContactDestinationPickerDialog(destinations)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };
        await HandleDialogPresentationAsync(dialog);
        return dialog.PickedDestination;
    }

    public async Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount)
    {
        var dialog = new NewContactListDialog(accounts, selectedAccount)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };
        await HandleDialogPresentationAsync(dialog);
        return dialog.Result;
    }

    public async Task<AccountTaskList?> ShowTaskListPickerDialogAsync(
        IReadOnlyList<AccountTaskList> taskLists,
        IReadOnlyList<MailAccount> accounts)
    {
        var dialog = new TaskListPickerDialog(taskLists, accounts)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };
        await HandleDialogPresentationAsync(dialog);
        return dialog.PickedList;
    }

    public async Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
    {
        var calendarPicker = new SingleCalendarPickerDialog(availableCalendarGroups)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(calendarPicker);

        return new AccountCalendarPickingResult(calendarPicker.PickedCalendar, calendarPicker.ShouldNavigateToCalendarSettings);
    }

    public async Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
    {
        SignatureEditorDialog signatureEditorDialog;
        if (signatureModel != null)
        {
            signatureEditorDialog = new SignatureEditorDialog(signatureModel)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };
        }
        else
        {
            signatureEditorDialog = new SignatureEditorDialog()
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };
        }

        var result = await HandleDialogPresentationAsync(signatureEditorDialog);

        return result == ContentDialogResult.Primary ? signatureEditorDialog.Result : null!;
    }

    public async Task ShowMessageSourceDialogAsync(string messageSource)
    {
        var dialog = new MessageSourceDialog()
        {
            MessageSource = messageSource,
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        if (dialog.Copied)
            InfoBarMessage(Translator.ClipboardTextCopied_Title, string.Format(Translator.ClipboardTextCopied_Message, Translator.MessageSourceDialog_Title), InfoBarMessageType.Information);
    }

    public async Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog)
    {
        var dialog = new ImapValidationFailedDialog
        {
            ErrorMessage = errorMessage,
            ProtocolLog = protocolLog,
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        if (dialog.Copied)
        {
            InfoBarMessage(
                Translator.ClipboardTextCopied_Title,
                string.Format(Translator.ClipboardTextCopied_Message, Translator.ImapValidationFailedDialog_Title),
                InfoBarMessageType.Information);
        }
    }

    public async Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
    {
        var accountReorderDialog = new AccountReorderDialog(availableAccounts)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(accountReorderDialog);
    }

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
    public async Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null)
#pragma warning restore CS8625
    {
        var dialog = new KeyboardShortcutDialog(existingShortcut)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        return dialog.Result;
    }

    public async Task<string?> ShowBackupPasswordDialogAsync(SyncSnapshotSecretRequest request)
    {
        var dialog = new MailCalAccountSyncSecretDialog(request)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        return dialog.Result;
    }

    public async Task<bool> ShowCautiousConfirmationDialogAsync(string title, string message, string confirmButtonText)
    {
        var informationContainer = new CustomMessageDialogInformationContainer(title, message, MailCalCustomMessageDialogIcon.Warning, false);
        var dialog = new MailCalMessageDialog(informationContainer,
                                           confirmButtonText,
                                           closeButtonText: Translator.Buttons_Cancel,
                                           defaultButton: ContentDialogButton.Close)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        return await HandleDialogPresentationAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<EditorLinkCommandArgs?> ShowEditorLinkDialogAsync(string url, string text, bool isImageSelected)
    {
        var dialog = new EditorLinkDialog(url, text, isImageSelected)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        return dialog.Result;
    }

    public async Task<EditorImagePropertiesCommandArgs?> ShowEditorImagePropertiesDialogAsync(string altText, string linkUrl)
    {
        var dialog = new EditorImagePropertiesDialog(altText, linkUrl)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        return dialog.Result;
    }

    public async Task<EditorTableCommandArgs?> ShowEditorTableDialogAsync()
    {
        var dialog = new EditorTableDialog
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        return dialog.Result;
    }

    public ExternalBrowserAuthenticationDialog? OpenExternalBrowserAuthenticationDialog(ExternalBrowserAuthenticationRequest request, Action cancelRequested)
    {
        var dialog = new ExternalBrowserAuthenticationDialog
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme(),
            AuthorizationUri = request.AuthorizationUri,
            Message = string.Format(Translator.ExternalBrowserAuthenticationDialog_Message, request.ProviderDisplayName)
        };

        dialog.CancelRequested += (_, _) => cancelRequested();

        // The sign-in waits for the browser redirect, which the user completes away from Mail-Cal,
        // so the surface opens on the main window rather than whichever window was last active.
        return StartSessionDialog(dialog, MailCalApplication.MainWindow?.Content?.XamlRoot) == null ? null : dialog;
    }
}
