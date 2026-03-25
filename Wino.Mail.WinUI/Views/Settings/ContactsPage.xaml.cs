using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ContactsPage : ContactsPageAbstract, ITitleBarSearchHost
{
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];

    public string SearchText
    {
        get => ViewModel.SearchQuery;
        set => ViewModel.SearchQuery = value;
    }

    public string SearchPlaceholderText => Translator.ContactsPage_SearchPlaceholder;

    public ContactsPage()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        Unloaded += ContactsPageUnloaded;
    }

    private void EditContact_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is AccountContactViewModel contact)
        {
            ViewModel.EditContactCommand.Execute(contact);
        }
    }

    private void PickContactPhoto_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is AccountContactViewModel contact)
        {
            ViewModel.PickContactPhotoCommand.Execute(contact);
        }
    }

    private void DeleteContact_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is AccountContactViewModel contact)
        {
            ViewModel.DeleteContactCommand.Execute(contact);
        }
    }

    private void ContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView)
            return;

        if (!ViewModel.IsSelectionMode)
        {
            ClearSelection();
            return;
        }

        foreach (var removedItem in e.RemovedItems.OfType<AccountContactViewModel>())
        {
            var selectedContact = ViewModel.SelectedContacts.FirstOrDefault(c =>
                string.Equals(c.Address, removedItem.Address, StringComparison.OrdinalIgnoreCase));

            if (selectedContact != null)
            {
                ViewModel.SelectedContacts.Remove(selectedContact);
            }
        }

        foreach (var addedItem in e.AddedItems.OfType<AccountContactViewModel>())
        {
            var alreadySelected = ViewModel.SelectedContacts.Any(c =>
                string.Equals(c.Address, addedItem.Address, StringComparison.OrdinalIgnoreCase));

            if (!alreadySelected)
            {
                ViewModel.SelectedContacts.Add(addedItem);
            }
        }
    }

    private void SelectAllContacts_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!ViewModel.IsSelectionMode)
            return;

        ContactsListView.SelectAll();
    }

    private void ClearSelection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ClearSelection();
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
        Unloaded -= ContactsPageUnloaded;
    }

    private void ClearSelection()
    {
        ContactsListView.SelectionChanged -= ContactsListView_SelectionChanged;
        ContactsListView.SelectedItems.Clear();
        ContactsListView.SelectionChanged += ContactsListView_SelectionChanged;
        ViewModel.SelectedContacts.Clear();
    }

    public Task OnTitleBarSearchTextChangedAsync() => Task.CompletedTask;

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
    {
    }

    public Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = queryText;

        if (ViewModel.ReloadContactsCommand.CanExecute(null))
        {
            ViewModel.ReloadContactsCommand.Execute(null);
        }

        return Task.CompletedTask;
    }
}
