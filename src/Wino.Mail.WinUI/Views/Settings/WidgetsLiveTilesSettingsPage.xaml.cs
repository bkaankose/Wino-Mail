
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

public sealed partial class WidgetsLiveTilesSettingsPage : WidgetsLiveTilesSettingsPageAbstract
{
    public WidgetsLiveTilesSettingsPage()
    {
        InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }
}
