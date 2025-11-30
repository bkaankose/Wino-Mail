using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ContactsPage : ContactsPageAbstract
{
    public ContactsPage()
    {
        InitializeComponent();
    }

    private void EditContact_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is Contact contact)
        {
            ViewModel.EditContactCommand.Execute(contact);
        }
    }

    private void PickContactPhoto_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is Contact contact)
        {
            ViewModel.PickContactPhotoCommand.Execute(contact);
        }
    }

    private void DeleteContact_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is Contact contact)
        {
            ViewModel.DeleteContactCommand.Execute(contact);
        }
    }
}