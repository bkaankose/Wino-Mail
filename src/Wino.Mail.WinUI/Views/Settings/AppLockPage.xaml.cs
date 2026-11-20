using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.AppLock;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

public sealed partial class AppLockPage : AppLockPageAbstract
{
    public AppLockPage()
    {
        InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    private void MethodPicker_Loaded(object sender, RoutedEventArgs e) => SyncMethodSelection(ViewModel.SelectedMethod);

    private void MethodPicker_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        // A method is always picked, so a deselection or a tile that cannot be picked snaps back.
        if (sender.SelectedItem is not AppLockMethodOption option || !ViewModel.SelectMethod(option.Method))
            SyncMethodSelection(ViewModel.SelectedMethod);
    }

    private AppLockMethod SyncMethodSelection(AppLockMethod method)
    {
        if (!MethodPicker.IsLoaded)
            return method;

        var index = Array.FindIndex(ViewModel.MethodOptions, option => option.Method == method);

        if (index >= 0 && !ReferenceEquals(MethodPicker.SelectedItem, ViewModel.MethodOptions[index]))
            MethodPicker.Select(index);

        return method;
    }
}
