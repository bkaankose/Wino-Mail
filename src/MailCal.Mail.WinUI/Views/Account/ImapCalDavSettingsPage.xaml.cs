using MailCal.Views.Abstract;

namespace MailCal.Views;

public sealed partial class ImapCalDavSettingsPage : ImapCalDavSettingsPageAbstract
{
    public ImapCalDavSettingsPage()
    {
        InitializeComponent();
    }

    public double GetContentMaxWidth(bool isSignInStepVisible) => isSignInStepVisible ? 440 : 1000;
}
