using Wino.Views.Abstract;

namespace Wino.Views.Settings;

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
