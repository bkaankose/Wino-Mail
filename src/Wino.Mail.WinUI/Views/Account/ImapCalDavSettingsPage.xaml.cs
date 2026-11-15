using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class ImapCalDavSettingsPage : ImapCalDavSettingsPageAbstract
{
    public ImapCalDavSettingsPage()
    {
        InitializeComponent();

        ViewModel.IsSecureSignInSupportedByHost = true;
    }

    public double GetContentMaxWidth(bool isSignInStepVisible) => isSignInStepVisible ? 440 : 1000;
}
