using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class PrivacySecurityPage : PrivacySecurityPageAbstract
{
    public PrivacySecurityPage()
    {
        InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    private void ModePicker_Loaded(object sender, RoutedEventArgs e) => SyncModeSelection();

    private void ModePicker_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is RemoteContentModeOption option)
            option.IsSelected = true;
        else
            SyncModeSelection();
    }

    // A mode is always active, so Ctrl+click deselection snaps back to the saved mode.
    private void SyncModeSelection()
    {
        var index = Array.FindIndex(ViewModel.ModeOptions, option => option.Mode == ViewModel.SelectedMode);

        if (index >= 0 && !ReferenceEquals(ModePicker.SelectedItem, ViewModel.ModeOptions[index]))
            ModePicker.Select(index);
    }
}
