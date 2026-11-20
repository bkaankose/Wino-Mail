using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

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
