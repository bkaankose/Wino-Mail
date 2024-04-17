using Wino.Views.Abstract;

namespace Wino.Views.Settings
{
    public sealed partial class SettingOptionsPage : SettingOptionsPageAbstract
    {
        public SettingOptionsPage()
        {
            InitializeComponent();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();

            Bindings.Update();
        }
    }
}
