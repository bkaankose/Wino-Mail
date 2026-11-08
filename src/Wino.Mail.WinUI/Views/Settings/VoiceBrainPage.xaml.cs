using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class VoiceBrainPage : VoiceBrainPageAbstract
{
    public VoiceBrainPage()
    {
        InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }
}
