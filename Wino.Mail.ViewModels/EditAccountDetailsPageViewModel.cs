using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class EditAccountDetailsPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IThemeService _themeService;
    private readonly IImapTestService _imapTestService;
    private readonly IMailDialogService _mailDialogService;

    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string SenderName { get; set; }

    [ObservableProperty]
    public partial AppColorViewModel SelectedColor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImapServer))]
    public partial CustomServerInformation ServerInformation { get; set; }

    [ObservableProperty]
    public partial List<AppColorViewModel> AvailableColors { get; set; }


    [ObservableProperty]
    public partial int SelectedIncomingServerConnectionSecurityIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedIncomingServerAuthenticationMethodIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedOutgoingServerConnectionSecurityIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedOutgoingServerAuthenticationMethodIndex { get; set; }

    public List<ImapAuthenticationMethodModel> AvailableAuthenticationMethods { get; } =
    [
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.Auto, Translator.ImapAuthenticationMethod_Auto),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.None, Translator.ImapAuthenticationMethod_None),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.NormalPassword, Translator.ImapAuthenticationMethod_Plain),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.EncryptedPassword, Translator.ImapAuthenticationMethod_EncryptedPassword),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.Ntlm, Translator.ImapAuthenticationMethod_Ntlm),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.CramMd5, Translator.ImapAuthenticationMethod_CramMD5),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.DigestMd5, Translator.ImapAuthenticationMethod_DigestMD5)
    ];

    public List<ImapConnectionSecurityModel> AvailableConnectionSecurities { get; set; } =
    [
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.Auto, Translator.ImapConnectionSecurity_Auto),
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.SslTls, Translator.ImapConnectionSecurity_SslTls),
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.StartTls, Translator.ImapConnectionSecurity_StartTls),
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.None, Translator.ImapConnectionSecurity_None)
    ];

    public bool IsImapServer => ServerInformation != null;

    public EditAccountDetailsPageViewModel(IAccountService accountService,
                                           IThemeService themeService,
                                           IImapTestService imapTestService,
                                           IMailDialogService mailDialogService)
    {
        _accountService = accountService;
        _themeService = themeService;
        _imapTestService = imapTestService;
        _mailDialogService = mailDialogService;

        var colorHexList = _themeService.GetAvailableAccountColors();

        AvailableColors = colorHexList.Select(a => new AppColorViewModel(a)).ToList();
    }

    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        await UpdateAccountAsync();

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    [RelayCommand]
    private async Task ValidateImapSettingsAsync()
    {
        try
        {
            await _imapTestService.TestImapConnectionAsync(ServerInformation, true);
            _mailDialogService.InfoBarMessage(Translator.IMAPSetupDialog_ValidationSuccess_Title, Translator.IMAPSetupDialog_ValidationSuccess_Message, Core.Domain.Enums.InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _mailDialogService.InfoBarMessage(Translator.IMAPSetupDialog_ValidationFailed_Title, ex.Message, Core.Domain.Enums.InfoBarMessageType.Error); ;
        }
    }

    [RelayCommand]
    private async Task UpdateCustomServerInformationAsync()
    {
        if (ServerInformation != null)
        {
            ServerInformation.IncomingAuthenticationMethod = AvailableAuthenticationMethods[SelectedIncomingServerAuthenticationMethodIndex].ImapAuthenticationMethod;
            ServerInformation.IncomingServerSocketOption = AvailableConnectionSecurities[SelectedIncomingServerConnectionSecurityIndex].ImapConnectionSecurity;

            ServerInformation.OutgoingAuthenticationMethod = AvailableAuthenticationMethods[SelectedOutgoingServerAuthenticationMethodIndex].ImapAuthenticationMethod;
            ServerInformation.OutgoingServerSocketOption = AvailableConnectionSecurities[SelectedOutgoingServerConnectionSecurityIndex].ImapConnectionSecurity;

            Account.ServerInformation = ServerInformation;
        }

        await _accountService.UpdateAccountCustomServerInformationAsync(Account.ServerInformation);

        _mailDialogService.InfoBarMessage(Translator.IMAPSetupDialog_SaveImapSuccess_Title, Translator.IMAPSetupDialog_SaveImapSuccess_Message, Core.Domain.Enums.InfoBarMessageType.Success);
    }

    private Task UpdateAccountAsync()
    {
        Account.Name = AccountName;
        Account.SenderName = SenderName;
        Account.AccountColorHex = SelectedColor == null ? string.Empty : SelectedColor.Hex;

        return _accountService.UpdateAccountAsync(Account);
    }

    [RelayCommand]
    private void ResetColor()
        => SelectedColor = null;

    partial void OnSelectedColorChanged(AppColorViewModel oldValue, AppColorViewModel newValue)
    {
        _ = UpdateAccountAsync();
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is MailAccount account)
        {
            Account = account;
            AccountName = account.Name;
            SenderName = account.SenderName;
            ServerInformation = Account.ServerInformation;

            if (!string.IsNullOrEmpty(account.AccountColorHex))
            {
                SelectedColor = AvailableColors.FirstOrDefault(a => a.Hex == account.AccountColorHex);
            }

            if (ServerInformation != null)
            {
                SelectedIncomingServerAuthenticationMethodIndex = AvailableAuthenticationMethods.FindIndex(a => a.ImapAuthenticationMethod == ServerInformation.IncomingAuthenticationMethod);
                SelectedIncomingServerConnectionSecurityIndex = AvailableConnectionSecurities.FindIndex(a => a.ImapConnectionSecurity == ServerInformation.IncomingServerSocketOption);

                SelectedOutgoingServerAuthenticationMethodIndex = AvailableAuthenticationMethods.FindIndex(a => a.ImapAuthenticationMethod == ServerInformation.OutgoingAuthenticationMethod);
                SelectedOutgoingServerConnectionSecurityIndex = AvailableConnectionSecurities.FindIndex(a => a.ImapConnectionSecurity == ServerInformation.OutgoingServerSocketOption);
            }
        }
    }
}
