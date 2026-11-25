using System;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Mail.WinUI.Dialogs;

public sealed partial class PictureResizeDialog : ContentDialog
{
    private readonly double _ratio;
    private bool _isSyncing;

    /// <summary>The size to apply, or null when the dialog was cancelled.</summary>
    public PictureSize? Result { get; private set; }

    public PictureResizeDialog(PictureSize currentSize)
    {
        InitializeComponent();

        _ratio = currentSize.Height > 0 ? (double)currentSize.Width / currentSize.Height : 1;
        _isSyncing = true;
        WidthBox.Value = currentSize.Width;
        HeightBox.Value = currentSize.Height;
        _isSyncing = false;
    }

    private void WidthBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isSyncing || KeepProportionsCheckBox.IsChecked != true || double.IsNaN(args.NewValue))
            return;

        _isSyncing = true;
        HeightBox.Value = Math.Max(1, Math.Round(args.NewValue / _ratio));
        _isSyncing = false;
    }

    private void HeightBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isSyncing || KeepProportionsCheckBox.IsChecked != true || double.IsNaN(args.NewValue))
            return;

        _isSyncing = true;
        WidthBox.Value = Math.Max(1, Math.Round(args.NewValue * _ratio));
        _isSyncing = false;
    }

    private void ApplyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (double.IsNaN(WidthBox.Value) || double.IsNaN(HeightBox.Value))
        {
            args.Cancel = true;
            return;
        }

        Result = new PictureSize((int)Math.Round(WidthBox.Value), (int)Math.Round(HeightBox.Value));
    }
}
