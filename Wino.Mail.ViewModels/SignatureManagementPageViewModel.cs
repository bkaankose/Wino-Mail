using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Messages.Mails;

namespace Wino.Mail.ViewModels
{
    public partial class SignatureManagementPageViewModel : BaseViewModel
    {
        public Func<Task<string>> GetHTMLBodyFunction;
        public Func<Task<string>> GetTextBodyFunction;

        public List<EditorToolbarSection> ToolbarSections { get; set; } = new List<EditorToolbarSection>()
        {
            new EditorToolbarSection(){  SectionType = EditorToolbarSectionType.Format },
            new EditorToolbarSection(){  SectionType = EditorToolbarSectionType.Insert },
        };

        [ObservableProperty]
        private EditorToolbarSection selectedToolbarSection;

        [ObservableProperty]
        private bool isSignatureEnabled;

        public MailAccount Account { get; set; }

        public AsyncRelayCommand SaveSignatureCommand { get; set; }

        public INativeAppService NativeAppService { get; }
        private readonly ISignatureService _signatureService;
        private readonly IAccountService _accountService;

        public SignatureManagementPageViewModel(IDialogService dialogService,
                                                INativeAppService nativeAppService,
                                                ISignatureService signatureService,
                                                IAccountService accountService) : base(dialogService)
        {
            SelectedToolbarSection = ToolbarSections[0];
            NativeAppService = nativeAppService;
            _signatureService = signatureService;
            _accountService = accountService;
            SaveSignatureCommand = new AsyncRelayCommand(SaveSignatureAsync);
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters is Guid accountId)
                Account = await _accountService.GetAccountAsync(accountId);

            if (Account != null)
            {
                var accountSignature = await _signatureService.GetAccountSignatureAsync(Account.Id);

                IsSignatureEnabled = accountSignature != null;

                if (IsSignatureEnabled)
                    Messenger.Send(new HtmlRenderingRequested(accountSignature.HtmlBody));
                else
                    Messenger.Send(new HtmlRenderingRequested(string.Empty)); // To get the theme changes. Render empty html.
            }
        }

        private async Task SaveSignatureAsync()
        {
            if (IsSignatureEnabled)
            {
                var newSignature = Regex.Unescape(await GetHTMLBodyFunction());

                await _signatureService.UpdateAccountSignatureAsync(Account.Id, newSignature);

                DialogService.InfoBarMessage(Translator.Info_SignatureSavedTitle, Translator.Info_SignatureSavedMessage, Core.Domain.Enums.InfoBarMessageType.Success);
            }
            else
            {
                await _signatureService.DeleteAccountSignatureAssignment(Account.Id);

                DialogService.InfoBarMessage(Translator.Info_SignatureDisabledTitle, Translator.Info_SignatureDisabledMessage, Core.Domain.Enums.InfoBarMessageType.Success);
            }
        }
    }
}
