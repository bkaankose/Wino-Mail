using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Dialogs;
using Wino.Mail.Dialogs;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Services;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Services;

public class DialogService : DialogServiceBase, IMailDialogService
{
    private readonly IWinoAccountProfileService _winoAccountProfileService;
    private readonly IWinoAccountDataSyncService _winoAccountDataSyncService;

    public DialogService(INewThemeService themeService,
                         IConfigurationService configurationService,
                         IApplicationResourceManager<ResourceDictionary> applicationResourceManager,
                         IWinoAccountProfileService winoAccountProfileService,
                         IWinoAccountDataSyncService winoAccountDataSyncService) : base(themeService, configurationService, applicationResourceManager)
    {
        _winoAccountProfileService = winoAccountProfileService;
        _winoAccountDataSyncService = winoAccountDataSyncService;
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
        var allFolders = await folderService.GetFolderStructureForAccountAsync(accountId, true);

        return await ShowMoveMailFolderDialogAsync(allFolders.Folders);
    }



    public Task<bool> ShowHardDeleteConfirmationAsync()
        => ShowWinoCustomMessageDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage,
                                           Translator.DialogMessage_HardDeleteConfirmationTitle,
                                           Translator.Buttons_Yes,
                                           WinoCustomMessageDialogIcon.Warning,
                                           Translator.Buttons_No);

    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title,
                                                                          string description,
                                                                          string primaryButtonText,
                                                                          string secondaryButtonText,
                                                                          string cancelButtonText,
                                                                          WinoCustomMessageDialogIcon? icon = null)
    {
        var informationContainer = new CustomMessageDialogInformationContainer(
            title,
            description,
            icon ?? WinoCustomMessageDialogIcon.Information,
            false);

        var dialog = new ContentDialog
        {
            Style = ApplicationResourceManager.GetResource<Style>("WinoDialogStyle"),
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme(),
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            CloseButtonText = cancelButtonText,
            ContentTemplate = ApplicationResourceManager.GetResource<DataTemplate>("CustomWinoContentDialogContentTemplate"),
            Content = informationContainer
        };

        var dialogResult = await HandleDialogPresentationAsync(dialog);

        return dialogResult switch
        {
            ContentDialogResult.Primary => ThreeButtonDialogResult.Primary,
            ContentDialogResult.Secondary => ThreeButtonDialogResult.Secondary,
            _ => ThreeButtonDialogResult.Cancel
        };
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

    public async Task<Core.Domain.Entities.Shared.AccountContact?> ShowEditContactDialogAsync(Core.Domain.Entities.Shared.AccountContact? contact = null)
    {
        var dialog = new ContactEditDialog(contact, this)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        var result = await HandleDialogPresentationAsync(dialog);

        if (result == ContentDialogResult.Primary)
        {
            return dialog.Contact;
        }

        return null;
    }

    public async Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync()
    {
        var dialog = new WinoAccountRegistrationDialog(_winoAccountProfileService)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        if (!string.IsNullOrWhiteSpace(dialog.ConfirmationEmailAddress))
        {
            await ShowMessageAsync(
                string.Format(Translator.WinoAccount_EmailConfirmationSentDialog_Message, dialog.ConfirmationEmailAddress),
                Translator.WinoAccount_EmailConfirmationSentDialog_Title);
        }

        return null;
    }

    public async Task<WinoAccount?> ShowWinoAccountLoginDialogAsync()
    {
        var dialog = new WinoAccountLoginDialog(_winoAccountProfileService)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        var result = await HandleDialogPresentationAsync(dialog);

        if (dialog.EmailConfirmationRequiredDetails != null && !string.IsNullOrWhiteSpace(dialog.PendingConfirmationEmailAddress))
        {
            var confirmationDialog = new WinoAccountEmailConfirmationRequiredDialog(
                _winoAccountProfileService,
                dialog.PendingConfirmationEmailAddress,
                dialog.EmailConfirmationRequiredDetails)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(confirmationDialog);

            if (confirmationDialog.ResendSucceeded)
            {
                await ShowMessageAsync(
                    string.Format(Translator.WinoAccount_EmailConfirmationResentDialog_Message, dialog.PendingConfirmationEmailAddress),
                    Translator.WinoAccount_EmailConfirmationResentDialog_Title);
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(dialog.PasswordResetEmailAddress))
        {
            await ShowMessageAsync(
                string.Format(Translator.WinoAccount_ForgotPasswordDialog_SuccessMessage, dialog.PasswordResetEmailAddress),
                Translator.WinoAccount_ForgotPasswordDialog_SuccessTitle);

            return null;
        }

        return dialog.Result;
    }

    public async Task<WinoAccountSyncExportResult?> ShowWinoAccountExportDialogAsync()
    {
        var dialog = new WinoAccountSyncExportDialog(_winoAccountDataSyncService)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        if (dialog.FailureException != null)
        {
            throw dialog.FailureException;
        }

        return dialog.Result;
    }
}
