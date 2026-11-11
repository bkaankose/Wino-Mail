using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Dialogs;

public sealed partial class RecurringEventDeleteDialog : ContentDialog
{
    public CalendarEventTargetType? Result { get; private set; }

    public RecurringEventDeleteDialog()
    {
        InitializeComponent();
    }

    private void PrimaryButtonClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = SeriesOption.IsChecked == true
            ? CalendarEventTargetType.Series
            : CalendarEventTargetType.Single;
    }
}
