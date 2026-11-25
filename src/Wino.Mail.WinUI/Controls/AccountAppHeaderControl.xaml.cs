using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.AccountSettings;

namespace Wino.Mail.WinUI.Controls;

/// <summary>The switch and "Syncs with" blocks that start every app page of an account.</summary>
public sealed partial class AccountAppHeaderControl : UserControl
{
    private bool _isSyncingSelection;

    [GeneratedDependencyProperty]
    public partial AccountAppPageViewModelBase? ViewModel { get; set; }

    public AccountAppHeaderControl()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            Detach(_attachedViewModel);
            _attachedViewModel = null;
        };
        Loaded += (_, _) => OnViewModelChanged(ViewModel);
    }

    private AccountAppPageViewModelBase? _attachedViewModel;

    partial void OnViewModelChanged(AccountAppPageViewModelBase? newValue)
    {
        Detach(_attachedViewModel);
        _attachedViewModel = newValue;

        if (newValue == null)
            return;

        newValue.PropertyChanged += ViewModelPropertyChanged;
        newValue.SourceOptions.CollectionChanged += SourceOptionsChanged;
        QueueSelectionSync();
    }

    private void Detach(AccountAppPageViewModelBase? viewModel)
    {
        if (viewModel == null)
            return;

        viewModel.PropertyChanged -= ViewModelPropertyChanged;
        viewModel.SourceOptions.CollectionChanged -= SourceOptionsChanged;
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountAppPageViewModelBase.SelectedSourceOption) or null or "")
            QueueSelectionSync();
    }

    private void SourceOptionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueSelectionSync();

    private void SourceChoiceSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (_isSyncingSelection || ViewModel == null || sender.SelectedItem is not AccountAppSourceOption option)
            return;

        if (!ReferenceEquals(ViewModel.SelectedSourceOption, option))
            ViewModel.SelectedSourceOption = option;
    }

    private void QueueSelectionSync() => DispatcherQueue.TryEnqueue(SyncSelection);

    private void SyncSelection()
    {
        if (ViewModel == null)
            return;

        _isSyncingSelection = true;
        try
        {
            var selected = ViewModel.SelectedSourceOption;
            var index = selected == null ? -1 : ViewModel.SourceOptions.IndexOf(selected);

            if (index < 0)
                SourceChoiceView.DeselectAll();
            else if (!ReferenceEquals(SourceChoiceView.SelectedItem, selected))
                SourceChoiceView.Select(index);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }
}
