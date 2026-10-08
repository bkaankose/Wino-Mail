using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account alias and mail category editors. Owned by the core dialogs work (WS4).</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>
    /// Windows CreateAccountAliasDialog. Always returns a dialog result; <see cref="ICreateAccountAliasDialog.CreatedAccountAlias"/>
    /// is null when the person cancels, which is what the alias page checks.
    /// </summary>
    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync() => PresentAsync<ICreateAccountAliasDialog>(async window =>
    {
        var form = new FormSheet(Translator.CreateAccountAliasDialog_Title, Translator.CreateAccountAliasDialog_Description, Translator.Buttons_Create);
        var alias = form.AddTextField(Translator.CreateAccountAliasDialog_AliasAddress, string.Empty, Translator.CreateAccountAliasDialog_AliasAddressPlaceholder);
        var replyTo = form.AddTextField(Translator.CreateAccountAliasDialog_ReplyToAddress, string.Empty, Translator.CreateAccountAliasDialog_ReplyToAddressPlaceholder);
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(alias.StringValue);

        var result = new MacCreateAccountAliasDialog();
        if (await form.PresentAsync(window))
        {
            result.CreatedAccountAlias = new MailAccountAlias
            {
                AliasAddress = alias.StringValue.Trim(),
                ReplyToAddress = replyTo.StringValue.Trim(),
                Id = Guid.NewGuid(),
                IsPrimary = false,
                IsVerified = false,
                Source = AliasSource.Manual,
                SendCapability = AliasSendCapability.Unknown
            };
        }
        return result;
    });

    /// <summary>Windows EditMailCategoryDialog: name and one of the shared palette colours; null when cancelled.</summary>
    public Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null!) => PresentAsync<MailCategoryDialogResult>(async window =>
    {
        var palette = MailCategoryPalette.DefaultOptions;
        var selected = palette.FirstOrDefault(option =>
            string.Equals(option.BackgroundColorHex, category?.BackgroundColorHex, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.TextColorHex, category?.TextColorHex, StringComparison.OrdinalIgnoreCase)) ?? palette[0];

        var form = new FormSheet(category is null ? Translator.MailCategoryDialog_CreateTitle : Translator.MailCategoryDialog_EditTitle, null, Translator.Buttons_Save);
        var name = form.AddTextField(Translator.MailCategoryDialog_Name, category?.Name, Translator.MailCategoryDialog_NamePlaceholder);

        var swatches = new WinoColorSwatchPicker { Colors = palette.Select(option => option.BackgroundColorHex).ToList(), SelectedHex = selected.BackgroundColorHex };
        swatches.WidthAnchor.ConstraintEqualTo(260).Active = true;
        WinoAccessibility.Label(swatches, Translator.MailCategoryDialog_Color);
        form.AddRow(Translator.MailCategoryDialog_Color, swatches);

        // A live chip shows the name in the chosen pair, since the text colour is part of the choice.
        var preview = new WinoSurfaceView { CornerRadius = WinoStyle.ControlRadius, StrokeWidth = 1 };
        var previewLabel = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        WinoLayout.Fill(previewLabel, preview, 3, 10, 3, 10);
        form.AddRow(null, WinoLayout.HStack(0, preview, WinoLayout.Spacer()));

        void UpdatePreview()
        {
            preview.Fill = WinoStyle.FromHexString(selected.BackgroundColorHex) ?? WinoStyle.SubtleFill;
            preview.Stroke = WinoStyle.FromHexString(selected.TextColorHex) ?? WinoStyle.GroupStroke;
            previewLabel.TextColor = WinoStyle.FromHexString(selected.TextColorHex) ?? WinoStyle.PrimaryText;
            previewLabel.StringValue = string.IsNullOrWhiteSpace(name.StringValue) ? Translator.MailCategoryDialog_NamePlaceholder : name.StringValue.Trim();
        }

        swatches.SelectionChanged += (_, hex) =>
        {
            selected = palette.FirstOrDefault(option => option.BackgroundColorHex == hex) ?? selected;
            UpdatePreview();
            form.Refresh();
        };
        name.Changed += (_, _) => UpdatePreview();
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.StringValue);
        UpdatePreview();

        if (!await form.PresentAsync(window)) return null!;
        return new MailCategoryDialogResult(name.StringValue.Trim(), selected.BackgroundColorHex, selected.TextColorHex);
    });

    /// <summary>Mac result for the alias dialog contract; the alias is null when cancelled.</summary>
    private sealed class MacCreateAccountAliasDialog : ICreateAccountAliasDialog
    {
        public MailAccountAlias CreatedAccountAlias { get; set; } = null!;
    }
}
