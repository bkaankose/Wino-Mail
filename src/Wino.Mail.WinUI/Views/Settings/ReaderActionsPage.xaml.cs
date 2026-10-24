using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;
using Windows.System;

namespace Wino.Views.Settings;

public sealed partial class ReaderActionsPage : ReaderActionsPageAbstract
{
    public ReaderActionsPage()
    {
        InitializeComponent();
    }

    // GridView has no keyboard reordering, so Alt+arrow keys move the focused action.
    private void ActionsGridView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.KeyStatus.IsMenuKeyDown)
            return;

        var offset = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => -1,
            VirtualKey.Right or VirtualKey.Down => 1,
            _ => 0
        };

        if (offset == 0 || (e.OriginalSource as FrameworkElement)?.DataContext is not ReaderCommandLayoutItemViewModel item)
            return;

        e.Handled = true;

        if (!ViewModel.MoveItem(item, offset))
            return;

        ActionsGridView.UpdateLayout();
        (ActionsGridView.ContainerFromItem(item) as Control)?.Focus(FocusState.Keyboard);
    }
}
