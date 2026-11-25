
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Account;

public sealed partial class ImapCalDavSettingsPage : ImapCalDavSettingsPageAbstract
{
    public ImapCalDavSettingsPage()
    {
        InitializeComponent();

        ViewModel.IsSecureSignInSupportedByHost = true;
        ViewModel.IsFeaturesStepSupportedByHost = true;
    }

    public double GetContentMaxWidth(bool isSignInStepVisible) => isSignInStepVisible ? 440 : 1000;
}
