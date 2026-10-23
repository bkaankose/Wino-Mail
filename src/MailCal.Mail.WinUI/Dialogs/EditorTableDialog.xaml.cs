using System;
using Microsoft.UI.Xaml.Controls;
using MailCal.Editor;

namespace MailCal.Dialogs;

public sealed partial class EditorTableDialog : ContentDialog
{
    /// <summary>
    /// The table to insert, or null when the dialog was cancelled.
    /// </summary>
    public EditorTableCommandArgs? Result { get; private set; }

    public EditorTableDialog()
    {
        InitializeComponent();
    }

    // A cleared NumberBox reports NaN, which falls back to the minimum.
    private static int ReadCount(NumberBox numberBox)
        => double.IsNaN(numberBox.Value) ? 1 : (int)Math.Max(1, numberBox.Value);

    private void InsertClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = new EditorTableCommandArgs(ReadCount(RowsNumberBox), ReadCount(ColumnsNumberBox));
}
