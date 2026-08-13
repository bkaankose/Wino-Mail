using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Dialogs;

public sealed partial class EditMailCategoryDialog : ContentDialog
{
    public MailCategoryDialogResult? Result { get; private set; }
    public string CategoryName { get; set; }
    public MailCategoryColorOption? SelectedColor { get; set; }

    public System.Collections.Generic.IReadOnlyList<MailCategoryColorOption> AvailableColors => MailCategoryPalette.DefaultOptions;

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
