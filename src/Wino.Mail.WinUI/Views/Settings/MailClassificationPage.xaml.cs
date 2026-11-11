using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.ViewModels.Data;
using Wino.Views.Abstract;
using WinRT;

namespace Wino.Views.Settings;

public sealed partial class MailClassificationPage : MailClassificationPageAbstract
{
    public MailClassificationPage() => InitializeComponent();

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    [DynamicWindowsRuntimeCast(typeof(ToggleSwitch))]
    private void IndexingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: WinoIntelligenceMailboxItemViewModel mailbox } toggle ||
            toggle.IsOn == mailbox.IsEnabled)
        {
            return;
        }

        ViewModel.ToggleIntelligenceMailboxCommand.Execute(mailbox);
    }
}
