using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class AppPreferencesPage : AppPreferencesPageAbstract
{
    public AppPreferencesPage()
    {
        this.InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }
}
