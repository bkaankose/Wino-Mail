using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.UWP.Extensions;
using Wino.Core.UWP.Services;
using Wino.Dialogs;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Services
{
    public class DialogService : DialogServiceBase, IMailDialogService
    {
        public DialogService(IThemeService themeService,
                             IConfigurationService configurationService,
                             IApplicationResourceManager<ResourceDictionary> applicationResourceManager) : base(themeService, configurationService, applicationResourceManager)
        {

        }

        public override IAccountCreationDialog GetAccountCreationDialog(MailProviderType type)
        {
            if (type == MailProviderType.IMAP4)
            {
                return new NewImapSetupDialog
                {
                    RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
                };
            }
            else
            {
                return base.GetAccountCreationDialog(type);
            }
        }

        public async Task<MailAccount> ShowEditAccountDialogAsync(MailAccount account)
        {
            var editAccountDialog = new AccountEditDialog(account)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(editAccountDialog);

            return editAccountDialog.IsSaved ? editAccountDialog.Account : null;
        }

        public async Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
        {
            var createAccountAliasDialog = new CreateAccountAliasDialog()
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(createAccountAliasDialog);

            return createAccountAliasDialog;
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

                    WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(options, SynchronizationSource.Client));
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

        public async Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
        {
            var accountPicker = new AccountPickerDialog(availableAccounts)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(accountPicker);

            return accountPicker.PickedAccount;
        }

        public async Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature signatureModel = null)
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

            return result == ContentDialogResult.Primary ? signatureEditorDialog.Result : null;
        }

        public async Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
        {
            var accountReorderDialog = new AccountReorderDialog(availableAccounts)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(accountReorderDialog);
        }
    }
}
