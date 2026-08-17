using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Dialogs;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class WinoIntelligenceManagementPage : WinoIntelligenceManagementPageAbstract
{
    private bool _isApplyingToggleState;
    private bool _isApplyingIntelligencePreferenceState;

    public WinoIntelligenceManagementPage() => InitializeComponent();

    private async void WinoIntelligenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingToggleState || !ViewModel.IsPageReady || sender is not ToggleSwitch toggleSwitch)
            return;

        _isApplyingToggleState = true;
        try
        {
            var actualState = await ViewModel.SetSemanticIndexingEnabledAsync(toggleSwitch.IsOn);
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingToggleState = false;
        }
    }

    private async void DailyBriefingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingIntelligencePreferenceState || !ViewModel.IsPageReady || sender is not ToggleSwitch toggleSwitch)
            return;

        _isApplyingIntelligencePreferenceState = true;
        try
        {
            var actualState = await ViewModel.SetDailyBriefingEnabledAsync(toggleSwitch.IsOn);
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingIntelligencePreferenceState = false;
        }
    }

    private async void IntelligenceIndicatorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingIntelligencePreferenceState || !ViewModel.IsPageReady ||
            sender is not ToggleSwitch toggleSwitch || toggleSwitch.DataContext is not IntelligenceIndicatorSettingsItem item)
            return;

        _isApplyingIntelligencePreferenceState = true;
        try
        {
            var actualState = await ViewModel.SetIntelligenceIndicatorVisibilityAsync(item.Identifier, toggleSwitch.IsOn);
            item.IsVisible = actualState;
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingIntelligencePreferenceState = false;
        }
    }

    private void CoverageFolderCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IntelligenceFolderCoverageItem item })
            _ = ShowCoverageRuleDialogAsync(item);
    }

    private void ChangeDefaultCoverageRule_Click(object sender, RoutedEventArgs e)
        => _ = ShowCoverageRuleDialogAsync(null);

    /// <summary>
    /// Opens the coverage editor for one folder, or for the account default when none is given.
    /// The dialog edits a copy, so dismissing it changes nothing.
    /// </summary>
    private async Task ShowCoverageRuleDialogAsync(IntelligenceFolderCoverageItem item)
    {
        if (!ViewModel.IsCoverageEditable || ViewModel.CreateRuleEditor(item) is not { } editor)
            return;

        var dialog = new IntelligenceCoverageRuleDialog(editor)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.ApplyRuleEditor(editor);
    }

    private async void PickIntelligenceFolders_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditIntelligenceFolders)
            return;

        var dialog = new IntelligenceFolderPickerDialog(ViewModel.IntelligenceFolderSelections)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SetIntelligenceFolderSelectionAsync(dialog.SelectedRemoteFolderIds);
    }
}
