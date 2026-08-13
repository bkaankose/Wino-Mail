using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.Dialogs;

public sealed partial class WinoSimilarMailDialog : ContentDialog
{
    public IReadOnlyList<WinoSimilarMailItem> Items { get; }
    public WinoSimilarMailItem? SelectedMail { get; private set; }

    public WinoSimilarMailDialog(IReadOnlyList<WinoSimilarMailItem> items)
    {
        Items = items;
        InitializeComponent();
    }

    private void SimilarMailItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WinoSimilarMailItem item)
            return;
        SelectedMail = item;
        Hide();
    }
}
