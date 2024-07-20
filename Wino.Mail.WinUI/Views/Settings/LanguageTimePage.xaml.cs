using Wino.Views.Abstract;

namespace Wino.Views.Settings
{
    public sealed partial class LanguageTimePage : LanguageTimePageAbstract
    {
        public LanguageTimePage()
        {
            this.InitializeComponent();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();

            Bindings.Update();
        }
    }
}
