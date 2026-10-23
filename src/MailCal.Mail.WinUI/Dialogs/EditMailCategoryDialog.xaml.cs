using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Models.MailItem;
using WinRT;

namespace MailCal.Dialogs;

public sealed partial class EditMailCategoryDialog : ContentDialog
{
    public MailCategoryDialogResult? Result { get; private set; }
    public string CategoryName { get; set; }
    public MailCategoryColorOption? SelectedColor { get; set; }

    internal MailCategoryColorOptionList AvailableColors { get; } = new(MailCategoryPalette.DefaultOptions);

    public EditMailCategoryDialog(MailCategory? category = null)
    {
        InitializeComponent();

        Title = category == null ? Translator.MailCategoryDialog_CreateTitle : Translator.MailCategoryDialog_EditTitle;
        CategoryName = category?.Name ?? string.Empty;
        SelectedColor = MailCategoryPalette.DefaultOptions.FirstOrDefault(a =>
            a.BackgroundColorHex == category?.BackgroundColorHex &&
            a.TextColorHex == category?.TextColorHex) ?? MailCategoryPalette.DefaultOptions.First();

        IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(CategoryName);
    }

    private void CategoryNameTextChanged(object sender, TextChangedEventArgs e)
        => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(CategoryNameTextBox.Text) && SelectedColor != null;

    private void ColorOptionClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MailCategoryColorOption option)
        {
            SelectedColor = option;
            ColorsGridView.SelectedItem = option;
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(CategoryNameTextBox.Text);
        }
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedColor == null)
        {
            args.Cancel = true;
            return;
        }

        Result = new MailCategoryDialogResult(CategoryNameTextBox.Text?.Trim(), SelectedColor.BackgroundColorHex, SelectedColor.TextColorHex);
        Hide();
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }
}

/// <summary>
/// The colour swatches bound to the dialog's grid. Native AOT requires a concrete, WinRT-exposed collection
/// type: an array of a non-WinRT record has no generated vector vtable, so the grid rejects it with
/// E_INVALIDARG in Release.
/// </summary>
[GeneratedWinRTExposedType]
public sealed partial class MailCategoryColorOptionList(IEnumerable<MailCategoryColorOption> options) : List<MailCategoryColorOption>(options);
