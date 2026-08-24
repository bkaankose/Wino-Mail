using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain.Entities.Shared;
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
    }

    private void ContactEditPage_Unloaded(object sender, RoutedEventArgs e)
        => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;

    private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.PreviewPhotoBytes) or nameof(ViewModel.PreviewPhotoPath))
            Bindings.Update();
    }

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
