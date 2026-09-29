using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Views;

/// <summary>
/// Fills the wizard viewport: the provider list takes the free height and the buttons stay at the bottom,
/// so the host must not scroll this page as a whole.
/// </summary>
public sealed partial class ProviderSelectionPage : ProviderSelectionPageAbstract
{
    private bool _isSyncingSelection;

    public ProviderSelectionPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        QueueSelectionSync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // Opening the catalog preselects the IMAP / SMTP entry, and filtering replaces the catalog source,
        // which drops the view's selection. Both need the view to catch up with the ViewModel.
        if (e.PropertyName is nameof(ProviderSelectionPageViewModel.IsCatalogVisible)
            or nameof(ProviderSelectionPageViewModel.FilteredCatalogProviders)
            or nameof(ProviderSelectionPageViewModel.SelectedProvider))
        {
            QueueSelectionSync();
        }
    }

    private void ProviderSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (_isSyncingSelection || sender.SelectedItem is not IProviderDetail provider) return;

        ViewModel.SelectedProvider = provider;
    }

    /// <summary>
    /// The featured tiles, the IMAP / SMTP entry and the catalog list are three selection surfaces for one
    /// choice. Only the one holding the selected provider shows the accent border.
    /// </summary>
    private void QueueSelectionSync()
        => DispatcherQueue.TryEnqueue(SyncSelection);

    private void SyncSelection()
    {
        _isSyncingSelection = true;
        try
        {
            var selected = ViewModel.SelectedProvider;
            Sync(FeaturedProvidersView, ViewModel.FeaturedProviders, selected);
            Sync(CustomServerView, ViewModel.CustomServerProviders, selected);
            Sync(CatalogProvidersView, ViewModel.FilteredCatalogProviders, selected);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private static void Sync(ItemsView view, IList<IProviderDetail> source, IProviderDetail selected)
    {
        var index = selected == null ? -1 : source.IndexOf(selected);

        if (index < 0)
        {
            view.DeselectAll();
        }
        else if (!ReferenceEquals(view.SelectedItem, selected))
        {
            view.Select(index);
        }
    }
}
