using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class ImapCalDavSettingsPage : ImapCalDavSettingsPageAbstract
{
    public ImapCalDavSettingsPage()
    {
        InitializeComponent();
    }

    public double GetContentMaxWidth(bool isSignInStepVisible) => isSignInStepVisible ? 440 : 1000;
}
