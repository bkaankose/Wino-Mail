using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Views.Dialogs;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>Account alias and mail category editors (the Mac AppKitDialogService.AliasCategory).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>
    /// Windows CreateAccountAliasDialog: alias address, then reply-to address. Always returns a dialog result;
    /// <see cref="ICreateAccountAliasDialog.CreatedAccountAlias"/> is null when the person cancels, which is what the
    /// alias page checks.
    /// </summary>
    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync() => PresentAsync<ICreateAccountAliasDialog>(async presenter =>
    {
        var form = new FormSheet(Translator.CreateAccountAliasDialog_Title, Translator.CreateAccountAliasDialog_Description, Translator.Buttons_Create);
        var alias = EmailField(form.AddTextField(Translator.CreateAccountAliasDialog_AliasAddress, string.Empty, Translator.CreateAccountAliasDialog_AliasAddressPlaceholder));
        var replyTo = EmailField(form.AddTextField(Translator.CreateAccountAliasDialog_ReplyToAddress, string.Empty, Translator.CreateAccountAliasDialog_ReplyToAddressPlaceholder));
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(alias.Text);

        var result = new AliasDialogResult();
        if (await form.PresentAsync(presenter))
        {
            result.CreatedAccountAlias = new MailAccountAlias
            {
                AliasAddress = (alias.Text ?? string.Empty).Trim(),
                ReplyToAddress = (replyTo.Text ?? string.Empty).Trim(),
                Id = Guid.NewGuid(),
                IsPrimary = false,
                IsVerified = false,
                Source = AliasSource.Manual,
                SendCapability = AliasSendCapability.Unknown
            };
        }
        return result;
    }, new AliasDialogResult());

    /// <summary>Windows EditMailCategoryDialog: name, then one of the shared palette colours with a live chip; null when cancelled.</summary>
    public Task<MailCategoryDialogResult?> ShowEditMailCategoryDialogAsync(MailCategory? category = null) => PresentAsync<MailCategoryDialogResult?>(async presenter =>
    {
        var palette = MailCategoryPalette.DefaultOptions;
        var selected = palette.FirstOrDefault(option =>
            string.Equals(option.BackgroundColorHex, category?.BackgroundColorHex, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.TextColorHex, category?.TextColorHex, StringComparison.OrdinalIgnoreCase)) ?? palette[0];

        var form = new FormSheet(category is null ? Translator.MailCategoryDialog_CreateTitle : Translator.MailCategoryDialog_EditTitle, null, Translator.Buttons_Save);
        var name = form.AddTextField(Translator.MailCategoryDialog_Name, category?.Name, Translator.MailCategoryDialog_NamePlaceholder);
        name.AutocapitalizationType = UITextAutocapitalizationType.Sentences;

        var swatches = new WinoColorSwatchPicker { Colors = palette.Select(option => option.BackgroundColorHex).ToList(), SelectedHex = selected.BackgroundColorHex };
        WinoAccessibility.Label(swatches, Translator.MailCategoryDialog_Color);
        form.AddRow(Translator.MailCategoryDialog_Color, swatches);

        // A live chip shows the name in the chosen pair, since the text colour is part of the choice.
        var preview = new WinoSurfaceView { CornerRadius = WinoStyle.ControlRadius, StrokeWidth = 1, TranslatesAutoresizingMaskIntoConstraints = false };
        var previewLabel = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        WinoLayout.Fill(previewLabel, preview, 4, 12, 4, 12);
        form.AddRow(null, WinoLayout.HStack(0, preview, WinoLayout.Spacer()));

        void UpdatePreview()
        {
            preview.Fill = WinoStyle.FromHexString(selected.BackgroundColorHex) ?? WinoStyle.SubtleFill;
            preview.Stroke = WinoStyle.FromHexString(selected.TextColorHex) ?? WinoStyle.GroupStroke;
            previewLabel.TextColor = WinoStyle.FromHexString(selected.TextColorHex) ?? WinoStyle.PrimaryText;
            previewLabel.Text = string.IsNullOrWhiteSpace(name.Text) ? Translator.MailCategoryDialog_NamePlaceholder : name.Text.Trim();
        }

        swatches.SelectionChanged += (_, hex) =>
        {
            selected = palette.FirstOrDefault(option => option.BackgroundColorHex == hex) ?? selected;
            UpdatePreview();
            form.Refresh();
        };
        name.EditingChanged += (_, _) => UpdatePreview();
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.Text);
        UpdatePreview();

        if (!await form.PresentAsync(presenter)) return null;
        return new MailCategoryDialogResult((name.Text ?? string.Empty).Trim(), selected.BackgroundColorHex, selected.TextColorHex);
    }, null);

    /// <summary>An address field: email keyboard, no capitalisation or autocorrection.</summary>
    private static UITextField EmailField(UITextField field)
    {
        field.KeyboardType = UIKeyboardType.EmailAddress;
        field.AutocapitalizationType = UITextAutocapitalizationType.None;
        field.AutocorrectionType = UITextAutocorrectionType.No;
        field.SpellCheckingType = UITextSpellCheckingType.No;
        field.TextContentType = UITextContentType.EmailAddress;
        return field;
    }

    /// <summary>The alias dialog contract; the alias stays null when cancelled.</summary>
    private sealed class AliasDialogResult : ICreateAccountAliasDialog
    {
        public MailAccountAlias? CreatedAccountAlias { get; set; }
    }
}
