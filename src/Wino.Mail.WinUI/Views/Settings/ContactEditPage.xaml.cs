using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.ViewModels;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ContactEditPage : ContactEditPageAbstract
{
    public ContactEditPage() => InitializeComponent();

    private void ContactEditPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Bindings.Update();
        SelectCategoryNavigationItem();
    }

    private void ContactEditPage_Unloaded(object sender, RoutedEventArgs e)
        => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;

    private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.PreviewPhotoBytes) or nameof(ViewModel.PreviewPhotoPath))
            Bindings.Update();
        else if (e.PropertyName == nameof(ViewModel.SelectedCategory))
            SelectCategoryNavigationItem();
    }

    private void CategoryNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem selectedItem)
            return;

        ViewModel.SelectedCategory = selectedItem switch
        {
            _ when ReferenceEquals(selectedItem, ContactInformationNavigationItem) => ContactEditorCategory.ContactInformation,
            _ when ReferenceEquals(selectedItem, WorkNavigationItem) => ContactEditorCategory.Work,
            _ when ReferenceEquals(selectedItem, AddressNavigationItem) => ContactEditorCategory.Address,
            _ when ReferenceEquals(selectedItem, OtherNavigationItem) => ContactEditorCategory.Other,
            _ when ReferenceEquals(selectedItem, NotesNavigationItem) => ContactEditorCategory.Notes,
            _ => ViewModel.SelectedCategory
        };
    }

    private void SelectCategoryNavigationItem()
    {
        if (CategoryNavigation is null)
            return;

        CategoryNavigation.SelectedItem = ViewModel.SelectedCategory switch
        {
            ContactEditorCategory.Work => WorkNavigationItem,
            ContactEditorCategory.Address => AddressNavigationItem,
            ContactEditorCategory.Other => OtherNavigationItem,
            ContactEditorCategory.Notes => NotesNavigationItem,
            _ => ContactInformationNavigationItem
        };
    }

    public Visibility GetContactInformationCategoryVisibility(ContactEditorCategory category)
        => category == ContactEditorCategory.ContactInformation ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetWorkCategoryVisibility(ContactEditorCategory category)
        => category == ContactEditorCategory.Work ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetAddressCategoryVisibility(ContactEditorCategory category)
        => category == ContactEditorCategory.Address ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetOtherCategoryVisibility(ContactEditorCategory category)
        => category == ContactEditorCategory.Other ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetNotesCategoryVisibility(ContactEditorCategory category)
        => category == ContactEditorCategory.Notes ? Visibility.Visible : Visibility.Collapsed;

    private void ContactEditPage_LosingFocus(UIElement sender, LosingFocusEventArgs args)
        => ViewModel.MarkDirty();

    private void RemoveEmail_Click(object sender, RoutedEventArgs e)
        => ViewModel.RemoveEmailCommand.Execute((sender as Button)?.DataContext as ContactEmailAddress);

    private void RemovePhone_Click(object sender, RoutedEventArgs e)
        => ViewModel.RemovePhoneCommand.Execute((sender as Button)?.DataContext as ContactPhoneNumber);

    private void RemoveImAddress_Click(object sender, RoutedEventArgs e)
        => ViewModel.RemoveImAddressCommand.Execute((sender as Button)?.DataContext as ContactImAddress);

    private void RemoveRelation_Click(object sender, RoutedEventArgs e)
        => ViewModel.RemoveRelationCommand.Execute((sender as Button)?.DataContext as ContactRelation);
}
