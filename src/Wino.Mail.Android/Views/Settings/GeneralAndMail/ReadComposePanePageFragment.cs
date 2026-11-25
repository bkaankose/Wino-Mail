using Android.Graphics;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// The Rendering page (Windows ReadComposePanePage, iOS ReadComposePanePageViewController): reading, smart content,
/// writing, the checks before sending and the related pages, in the Windows order. A phone has no reading pane to lay
/// out and no detached compose window, so the widths and the pane choices do not apply.
/// </summary>
public sealed class ReadComposePanePageFragment : SettingsPageFragment<ReadComposePanePageViewModel>
{
    /// <summary>The Android system font families, used when the shared font list is unavailable.</summary>
    private static readonly List<string> SystemFamilies =
        ["sans-serif", "sans-serif-condensed", "sans-serif-light", "sans-serif-medium", "serif", "monospace", "serif-monospace", "casual", "cursive"];

    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;
        var fonts = AvailableFonts();
        var darkModeMethods = new string[] { Translator.SettingsReaderDarkModeMethod_Adaptive, Translator.SettingsReaderDarkModeMethod_Inverted };

        var readerPreview = Text(Translator.SettingsRendering_FontPreviewText, WinoTextStyle.Body);
        void UpdateReader() => ApplyPreviewFont(readerPreview, vm.CurrentReaderFont, vm.CurrentReaderFontSize);
        Bind.Bind(vm, nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, _ => UpdateReader());
        Bind.Bind(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, _ => UpdateReader());

        var composerPreview = Text(Translator.SettingsRendering_ComposerFontPreviewText, WinoTextStyle.Body);
        void UpdateComposer() => ApplyPreviewFont(composerPreview, vm.CurrentComposerFont, vm.CurrentComposerFontSize);
        Bind.Bind(vm, nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, _ => UpdateComposer());
        Bind.Bind(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, _ => UpdateComposer());

        AddGroup(Translator.SettingsRendering_ReadingHeader,
            Expander(Translator.SettingsRendering_ReaderFont_Title, Translator.SettingsRendering_ReaderFont_Description, WinoIconGlyph.TextFont, null,
                StackedCard(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, (s, v) => s.CurrentReaderFont = v)),
                StackedCard(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, (s, v) => s.CurrentReaderFontSize = v, 8, 48,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                StackedCard(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, readerPreview)),
            Expander(Translator.SettingsReaderDarkMode_Title, Translator.SettingsReaderDarkMode_Description, WinoIconGlyph.DarkEditor, null,
                Card(Translator.SettingsReaderMessageColors_Title, Translator.SettingsReaderMessageColors_Description, WinoIconGlyph.None,
                    Bind.PopUp(vm, vm.ReaderContentThemeOptions, nameof(vm.SelectedReaderContentThemeIndex),
                        s => s.SelectedReaderContentThemeIndex, (s, v) => s.SelectedReaderContentThemeIndex = v)),
                Card(Translator.SettingsReaderDarkModeMethod_Title, Translator.SettingsReaderDarkModeMethod_Description, WinoIconGlyph.None,
                    Bind.PopUp(vm, darkModeMethods, nameof(vm.SelectedReaderDarkModeMethodIndex),
                        s => s.SelectedReaderDarkModeMethodIndex, (s, v) => s.SelectedReaderDarkModeMethodIndex = v))),
            FoldingExpander(p),
            Card(Translator.SettingsRendering_Linkify_Title, Translator.SettingsRendering_Linkify_Description, WinoIconGlyph.Link,
                Bind.Switch(p, nameof(p.RenderPlaintextLinks), s => s.RenderPlaintextLinks, (s, v) => s.RenderPlaintextLinks = v, Translator.SettingsRendering_Linkify_Title)));

        AddGroup(Translator.SettingsRendering_SmartContentHeader,
            CardsExpander(p),
            Card(Translator.SettingsRendering_Dates_Title, Translator.SettingsRendering_Dates_Description, WinoIconGlyph.Calendar,
                Bind.Switch(p, nameof(p.IsDateDetectionEnabled), s => s.IsDateDetectionEnabled, (s, v) => s.IsDateDetectionEnabled = v, Translator.SettingsRendering_Dates_Title)));

        AddGroup(Translator.SettingsRendering_WritingHeader,
            Expander(Translator.SettingsRendering_ComposerFont_Title, Translator.SettingsRendering_ComposerFont_Description, WinoIconGlyph.TextFont, null,
                StackedCard(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, (s, v) => s.CurrentComposerFont = v)),
                StackedCard(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, (s, v) => s.CurrentComposerFontSize = v, 8, 48,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                StackedCard(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, composerPreview)),
            SpellCheckExpander(vm, p),
            Card(Translator.SettingsRendering_PasteFormatting_Title, Translator.SettingsRendering_PasteFormatting_Description, WinoIconGlyph.ClipboardPaste,
                Bind.Switch(p, nameof(p.IsComposerPasteFormattingEnabled), s => s.IsComposerPasteFormattingEnabled, (s, v) => s.IsComposerPasteFormattingEnabled = v,
                    Translator.SettingsRendering_PasteFormatting_Title)));

        AddGroup(Translator.SettingsRendering_DraftChecks_Title, DraftChecksExpander(p));

        AddGroup(Translator.SettingsRendering_RelatedHeader,
            NavigationCard(Translator.PrivacySecurity_Title, Translator.SettingsRendering_RelatedPrivacy_Description, WinoIconGlyph.Shield,
                () => vm.OpenPrivacySettingsCommand.Execute(null)),
            NavigationCard(Translator.ReaderActions_Title, Translator.SettingsRendering_RelatedReaderActions_Description, WinoIconGlyph.ReOrderDotsVertical,
                () => vm.OpenReaderActionsCommand.Execute(null)));
    }

    private WinoSettingsExpander FoldingExpander(IPreferencesService p)
    {
        var folding = Bind.Switch(p, nameof(p.IsReaderFoldingEnabled), s => s.IsReaderFoldingEnabled, (s, v) => s.IsReaderFoldingEnabled = v,
            Translator.SettingsRendering_Folding_Title);
        var conversation = Bind.Switch(p, nameof(p.IsConversationFoldingEnabled), s => s.IsConversationFoldingEnabled, (s, v) => s.IsConversationFoldingEnabled = v,
            Translator.SettingsRendering_ConversationFolding_Title);
        var conversationCard = Card(Translator.SettingsRendering_ConversationFolding_Title, Translator.SettingsRendering_ConversationFolding_Description,
            WinoIconGlyph.None, conversation);
        Bind.Enabled(conversationCard, p, nameof(p.IsReaderFoldingEnabled), s => s.IsReaderFoldingEnabled);
        return Expander(Translator.SettingsRendering_Folding_Title, Translator.SettingsRendering_Folding_Description, WinoIconGlyph.TextQuote, folding, conversationCard);
    }

    private WinoSettingsExpander CardsExpander(IPreferencesService p)
    {
        var cards = Bind.Switch(p, nameof(p.IsActionCardsEnabled), s => s.IsActionCardsEnabled, (s, v) => s.IsActionCardsEnabled = v,
            Translator.SettingsRendering_ActionCards_Title);
        var confirmation = Bind.Switch(p, nameof(p.IsActionCardConfirmationEnabled), s => s.IsActionCardConfirmationEnabled, (s, v) => s.IsActionCardConfirmationEnabled = v,
            Translator.SettingsRendering_ActionCardConfirmation_Title);
        var confirmationCard = Card(Translator.SettingsRendering_ActionCardConfirmation_Title, Translator.SettingsRendering_ActionCardConfirmation_Description,
            WinoIconGlyph.None, confirmation);
        Bind.Enabled(confirmationCard, p, nameof(p.IsActionCardsEnabled), s => s.IsActionCardsEnabled);
        return Expander(Translator.SettingsRendering_ActionCards_Title, Translator.SettingsRendering_ActionCards_Description, WinoIconGlyph.Ticket, cards, confirmationCard);
    }

    private WinoSettingsExpander SpellCheckExpander(ReadComposePanePageViewModel vm, IPreferencesService p)
    {
        var spellCheck = Bind.Switch(p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled, (s, v) => s.IsComposerSpellCheckEnabled = v,
            Translator.SettingsComposerSpellCheckEnabled_Title);
        var autoCorrect = Card(Translator.SettingsComposerAutoCorrect_Title, Translator.SettingsComposerAutoCorrect_Description, WinoIconGlyph.None,
            Bind.Switch(p, nameof(p.IsComposerAutoCorrectEnabled), s => s.IsComposerAutoCorrectEnabled, (s, v) => s.IsComposerAutoCorrectEnabled = v,
                Translator.SettingsComposerAutoCorrect_Title));
        var language = StackedCard(Translator.SettingsComposerSpellCheckLanguage_Title, null, WinoIconGlyph.None,
            Bind.PopUp<ReadComposePanePageViewModel, AppLanguageModel>(vm, s => s.AvailableSpellCheckLanguages, item => item.DisplayName,
                nameof(vm.CurrentSpellCheckLanguage), s => s.CurrentSpellCheckLanguage, (s, v) => s.CurrentSpellCheckLanguage = v));
        Bind.Enabled(autoCorrect, p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled);
        Bind.Enabled(language, p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled);
        return Expander(Translator.SettingsComposerSpellCheck_Title, Translator.SettingsComposerSpellCheck_Description, WinoIconGlyph.TextProofingTools, spellCheck,
            autoCorrect, language);
    }

    private WinoSettingsExpander DraftChecksExpander(IPreferencesService p)
    {
        var checks = Bind.Switch(p, nameof(p.IsDraftCheckEnabled), s => s.IsDraftCheckEnabled, (s, v) => s.IsDraftCheckEnabled = v,
            Translator.SettingsRendering_DraftChecks_Title);
        var pictures = DraftCheckCard(p, Translator.SettingsRendering_DraftCheckImages, nameof(p.IsDraftImageDescriptionCheckEnabled),
            s => s.IsDraftImageDescriptionCheckEnabled, (s, v) => s.IsDraftImageDescriptionCheckEnabled = v);
        var readable = DraftCheckCard(p, Translator.SettingsRendering_DraftCheckReadableText, nameof(p.IsDraftReadableTextCheckEnabled),
            s => s.IsDraftReadableTextCheckEnabled, (s, v) => s.IsDraftReadableTextCheckEnabled = v);
        var size = DraftCheckCard(p, Translator.SettingsRendering_DraftCheckSize, nameof(p.IsDraftMessageSizeCheckEnabled),
            s => s.IsDraftMessageSizeCheckEnabled, (s, v) => s.IsDraftMessageSizeCheckEnabled = v);
        return Expander(Translator.SettingsRendering_DraftChecks_Title, Translator.SettingsRendering_DraftChecks_Description, WinoIconGlyph.Send, checks,
            pictures, readable, size);
    }

    private WinoSettingsCard DraftCheckCard(IPreferencesService p, string title, string property,
        Func<IPreferencesService, bool> read, Action<IPreferencesService, bool> write)
    {
        var card = Card(title, null, WinoIconGlyph.None, Bind.Switch(p, property, read, write, title));
        Bind.Enabled(card, p, nameof(p.IsDraftCheckEnabled), s => s.IsDraftCheckEnabled);
        return card;
    }

    /// <summary>The shared font list; the Android system families when it is unavailable (as iOS falls back to UIFont).</summary>
    private List<string> AvailableFonts()
    {
        try
        {
            var fonts = VM.AvailableFonts;
            if (fonts is { Count: > 0 }) return fonts;
        }
        catch (Exception exception) { ReportError(exception); }
        return SystemFamilies;
    }

    private static void ApplyPreviewFont(TextView label, string? family, int size)
    {
        var points = Math.Clamp(size <= 0 ? 13 : size, 8, 48);
        label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, points);
        label.Typeface = string.IsNullOrWhiteSpace(family) ? Typeface.Default : Typeface.Create(family, TypefaceStyle.Normal);
    }
}
