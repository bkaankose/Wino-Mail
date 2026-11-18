
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

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
