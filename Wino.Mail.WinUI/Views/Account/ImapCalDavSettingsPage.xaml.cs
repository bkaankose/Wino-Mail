using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class ImapCalDavSettingsPage : ImapCalDavSettingsPageAbstract
{
    public ImapCalDavSettingsPage()
    {
        InitializeComponent();
    }

    private void OnSetupModeSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        ViewModel.SelectedSetupTabIndex = SetupModeSelector.SelectedItem == null ? 0 : SetupModeSelector.Items.IndexOf(SetupModeSelector.SelectedItem);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var tabIndex = ViewModel.SelectedSetupTabIndex;
        if (tabIndex < 0 || tabIndex >= SetupModeSelector.Items.Count)
        {
            tabIndex = 0;
        }

        SetupModeSelector.SelectedItem = SetupModeSelector.Items[tabIndex];
    }
}
