using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Models.Contacts;

namespace MailCal.Dialogs;

public sealed partial class ContactDestinationPickerDialog : ContentDialog
{
    internal ContactCreateDestination[] Destinations { get; }
    public ContactCreateDestination? PickedDestination { get; private set; }

    public ContactDestinationPickerDialog(IReadOnlyList<ContactCreateDestination> destinations)
    {
        Destinations = [.. destinations];
        InitializeComponent();
    }

    private void ItemClicked(object sender, ItemClickEventArgs e)
    {
        PickedDestination = e.ClickedItem as ContactCreateDestination;
        Hide();
    }
}
