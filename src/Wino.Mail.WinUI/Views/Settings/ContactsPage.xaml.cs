using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Helpers;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ContactsPage : ContactsPageAbstract, ITitleBarSearchHost
{
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Contacts;
    private CancellationTokenSource _searchCancellationTokenSource;
    private string _searchText = string.Empty;

    private CollectionViewSource ContactCollectionViewSource => (CollectionViewSource)Resources["ContactCollectionViewSource"];

    public string SearchText
    {
        get => _searchText;
        set => _searchText = value ?? string.Empty;
    }

    public string SearchPlaceholderText => Translator.ContactsPage_SearchPlaceholder;

    public ContactsPage()
    {
        InitializeComponent();

        ContactCollectionViewSource.Source = ViewModel.ContactGroups;

        Loaded += ContactsPageLoaded;
        Unloaded += ContactsPageUnloaded;
    }

    private void ContactsPageLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.PropertyChanged += ViewModelPropertyChanged;

    }

    private void ToggleFavorite_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountContactViewModel contact })
        {
            ViewModel.ToggleFavoriteCommand.Execute(contact);
        }
    }

    private void ContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView)
            return;

        if (!ViewModel.IsSelectionMode)
            return;

        foreach (var removedItem in e.RemovedItems.OfType<AccountContactViewModel>())
        {
            var selectedContact = ViewModel.SelectedContacts.FirstOrDefault(c => c.Id == removedItem.Id);

            if (selectedContact != null)
            {
                ViewModel.SelectedContacts.Remove(selectedContact);
            }
        }

        foreach (var addedItem in e.AddedItems.OfType<AccountContactViewModel>())
        {
            var alreadySelected = ViewModel.SelectedContacts.Any(c => c.Id == addedItem.Id);

            if (!alreadySelected)
            {
                ViewModel.SelectedContacts.Add(addedItem);
            }
        }
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContactsPageViewModel.IsSelectionMode) && !ViewModel.IsSelectionMode)
        {
            ClearSelection();
        }
    }

    private void ContactsPageUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = null;
    }

    private void ClearSelection()
    {
        ContactsListView.SelectionChanged -= ContactsListView_SelectionChanged;
        ContactsListView.SelectedItems.Clear();
        ContactsListView.SelectionChanged += ContactsListView_SelectionChanged;
        ViewModel.SelectedContacts.Clear();
    }

    public async Task OnTitleBarSearchTextChangedAsync()
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = null;
        SearchSuggestions.Clear();

        var queryText = SearchText;
        if (string.IsNullOrWhiteSpace(queryText))
            return;

        _searchCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _searchCancellationTokenSource.Token;

        try
        {
            await Task.Delay(150, cancellationToken);
            var contacts = await ViewModel.SearchContactsAsync(queryText, 6);

            if (cancellationToken.IsCancellationRequested || !string.Equals(SearchText, queryText, StringComparison.Ordinal))
                return;

            foreach (var contact in contacts)
            {
                var subtitle = string.Join(" • ", new[] { contact.SecondaryValue, contact.SourceLabel }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                SearchSuggestions.Add(new TitleBarSearchSuggestion(
                    contact.Name ?? contact.SourceContact.DisplayValue,
                    subtitle,
                    contact,
                    XamlHelpers.GetContactPicture(contact)));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
    {
        SearchText = suggestion.Title;
    }

    public async Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = chosenSuggestion?.Title ?? queryText;

        var suggestedContact = chosenSuggestion?.Tag as AccountContactViewModel
            ?? (await ViewModel.SearchContactsAsync(queryText, 1)).FirstOrDefault();
        if (suggestedContact is null)
            return;

        SearchSuggestions.Clear();
        var loadedContact = await ViewModel.LoadAndSelectContactAsync(suggestedContact.Id);
        if (loadedContact is null)
            return;

        ContactsListView.SelectedItem = loadedContact;
        ContactsListView.ScrollIntoView(loadedContact, ScrollIntoViewAlignment.Leading);
    }
}
