using System;
using System.Collections.Generic;
using System.Linq;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>Rendering page: reader and composer fonts, dark mode, folding, smart content, writing and checks before sending (Windows ReadComposePanePage).</summary>
public sealed class ReadComposePanePageViewController(ReadComposePanePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ReadComposePanePageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        var fonts = AvailableFonts();

        var readerPreview = WinoStyle.Label(Translator.SettingsRendering_FontPreviewText, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        void UpdateReader() => readerPreview.Font = PreviewFont(vm.CurrentReaderFont, vm.CurrentReaderFontSize);
        Bind.Bind(vm, nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, _ => UpdateReader());
        Bind.Bind(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, _ => UpdateReader());

        var composerPreview = WinoStyle.Label(Translator.SettingsRendering_ComposerFontPreviewText, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        void UpdateComposer() => composerPreview.Font = PreviewFont(vm.CurrentComposerFont, vm.CurrentComposerFontSize);
        Bind.Bind(vm, nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, _ => UpdateComposer());
        Bind.Bind(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, _ => UpdateComposer());

        var foldingSwitch = Bind.Switch(p, nameof(p.IsReaderFoldingEnabled), s => s.IsReaderFoldingEnabled, (s, v) => s.IsReaderFoldingEnabled = v, Translator.SettingsRendering_Folding_Title);
        var conversationFolding = Bind.Enabled(
            Card(Translator.SettingsRendering_ConversationFolding_Title, Translator.SettingsRendering_ConversationFolding_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.IsConversationFoldingEnabled), s => s.IsConversationFoldingEnabled, (s, v) => s.IsConversationFoldingEnabled = v, Translator.SettingsRendering_ConversationFolding_Title)),
            p, nameof(p.IsReaderFoldingEnabled), s => s.IsReaderFoldingEnabled);

        var actionCardsSwitch = Bind.Switch(p, nameof(p.IsActionCardsEnabled), s => s.IsActionCardsEnabled, (s, v) => s.IsActionCardsEnabled = v, Translator.SettingsRendering_ActionCards_Title);
        var actionCardConfirmation = Bind.Enabled(
            Card(Translator.SettingsRendering_ActionCardConfirmation_Title, Translator.SettingsRendering_ActionCardConfirmation_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.IsActionCardConfirmationEnabled), s => s.IsActionCardConfirmationEnabled, (s, v) => s.IsActionCardConfirmationEnabled = v, Translator.SettingsRendering_ActionCardConfirmation_Title)),
            p, nameof(p.IsActionCardsEnabled), s => s.IsActionCardsEnabled);

        var spellCheckSwitch = Bind.Switch(p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled, (s, v) => s.IsComposerSpellCheckEnabled = v, Translator.SettingsComposerSpellCheck_Title);
        var autoCorrect = Bind.Enabled(
            Card(Translator.SettingsComposerAutoCorrect_Title, Translator.SettingsComposerAutoCorrect_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.IsComposerAutoCorrectEnabled), s => s.IsComposerAutoCorrectEnabled, (s, v) => s.IsComposerAutoCorrectEnabled = v, Translator.SettingsComposerAutoCorrect_Title)),
            p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled);
        var spellCheckLanguage = Bind.Enabled(
            StackedCard(Translator.SettingsComposerSpellCheckLanguage_Title, null, WinoIconGlyph.None,
                Bind.PopUp<ReadComposePanePageViewModel, AppLanguageModel>(vm, s => s.AvailableSpellCheckLanguages, language => language.DisplayName,
                    nameof(vm.CurrentSpellCheckLanguage), s => s.CurrentSpellCheckLanguage, (s, v) => s.CurrentSpellCheckLanguage = v)),
            p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled);

        var draftChecksSwitch = Bind.Switch(p, nameof(p.IsDraftCheckEnabled), s => s.IsDraftCheckEnabled, (s, v) => s.IsDraftCheckEnabled = v, Translator.SettingsRendering_DraftChecks_Title);
        var draftCheckOptions = Bind.Enabled(WinoLayout.VStack(WinoStyle.Space2,
            Bind.Checkbox(p, Translator.SettingsRendering_DraftCheckImages, nameof(p.IsDraftImageDescriptionCheckEnabled), s => s.IsDraftImageDescriptionCheckEnabled, (s, v) => s.IsDraftImageDescriptionCheckEnabled = v),
            Bind.Checkbox(p, Translator.SettingsRendering_DraftCheckReadableText, nameof(p.IsDraftReadableTextCheckEnabled), s => s.IsDraftReadableTextCheckEnabled, (s, v) => s.IsDraftReadableTextCheckEnabled = v),
            Bind.Checkbox(p, Translator.SettingsRendering_DraftCheckSize, nameof(p.IsDraftMessageSizeCheckEnabled), s => s.IsDraftMessageSizeCheckEnabled, (s, v) => s.IsDraftMessageSizeCheckEnabled = v)),
            p, nameof(p.IsDraftCheckEnabled), s => s.IsDraftCheckEnabled);

        AddSection(Translator.SettingsRendering_ReadingHeader, null,
            Expander(Translator.SettingsRendering_ReaderFont_Title, Translator.SettingsRendering_ReaderFont_Description, WinoIconGlyph.TextFont, null,
                StackedCard(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, (s, v) => s.CurrentReaderFont = v)),
                StackedCard(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, (s, v) => s.CurrentReaderFontSize = v, 8, 72,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                StackedCard(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, readerPreview)),
            Expander(Translator.SettingsReaderDarkMode_Title, Translator.SettingsReaderDarkMode_Description, WinoIconGlyph.DarkEditor, null,
                StackedCard(Translator.SettingsReaderMessageColors_Title, Translator.SettingsReaderMessageColors_Description, WinoIconGlyph.None,
                    Bind.PopUp(vm, vm.ReaderContentThemeOptions, nameof(vm.SelectedReaderContentThemeIndex),
                        s => s.SelectedReaderContentThemeIndex, (s, v) => s.SelectedReaderContentThemeIndex = v, width: 180)),
                StackedCard(Translator.SettingsReaderDarkModeMethod_Title, Translator.SettingsReaderDarkModeMethod_Description, WinoIconGlyph.None,
                    Bind.Segmented(vm, new[] { Translator.SettingsReaderDarkModeMethod_Adaptive, Translator.SettingsReaderDarkModeMethod_Inverted },
                        nameof(vm.SelectedReaderDarkModeMethodIndex), s => s.SelectedReaderDarkModeMethodIndex, (s, v) => s.SelectedReaderDarkModeMethodIndex = v))),
            Expander(Translator.SettingsRendering_Folding_Title, Translator.SettingsRendering_Folding_Description, WinoIconGlyph.TextQuote, foldingSwitch,
                conversationFolding),
            Card(Translator.SettingsRendering_Linkify_Title, Translator.SettingsRendering_Linkify_Description, WinoIconGlyph.Link,
                Bind.Switch(p, nameof(p.RenderPlaintextLinks), s => s.RenderPlaintextLinks, (s, v) => s.RenderPlaintextLinks = v, Translator.SettingsRendering_Linkify_Title)));

        AddSection(Translator.SettingsRendering_SmartContentHeader, null,
            Expander(Translator.SettingsRendering_ActionCards_Title, Translator.SettingsRendering_ActionCards_Description, WinoIconGlyph.Ticket, actionCardsSwitch,
                actionCardConfirmation),
            Card(Translator.SettingsRendering_Dates_Title, Translator.SettingsRendering_Dates_Description, WinoIconGlyph.Calendar,
                Bind.Switch(p, nameof(p.IsDateDetectionEnabled), s => s.IsDateDetectionEnabled, (s, v) => s.IsDateDetectionEnabled = v, Translator.SettingsRendering_Dates_Title)));

        AddSection(Translator.SettingsRendering_WritingHeader, null,
            Expander(Translator.SettingsRendering_ComposerFont_Title, Translator.SettingsRendering_ComposerFont_Description, WinoIconGlyph.TextFont, null,
                StackedCard(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, (s, v) => s.CurrentComposerFont = v)),
                StackedCard(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, (s, v) => s.CurrentComposerFontSize = v, 8, 72,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                StackedCard(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, composerPreview)),
            Expander(Translator.SettingsComposerSpellCheck_Title, Translator.SettingsComposerSpellCheck_Description, WinoIconGlyph.TextProofingTools, spellCheckSwitch,
                autoCorrect, spellCheckLanguage),
            Card(Translator.SettingsRendering_PasteFormatting_Title, Translator.SettingsRendering_PasteFormatting_Description, WinoIconGlyph.ClipboardPaste,
                Bind.Switch(p, nameof(p.IsComposerPasteFormattingEnabled), s => s.IsComposerPasteFormattingEnabled, (s, v) => s.IsComposerPasteFormattingEnabled = v, Translator.SettingsRendering_PasteFormatting_Title)),
            Expander(Translator.SettingsRendering_DraftChecks_Title, Translator.SettingsRendering_DraftChecks_Description, WinoIconGlyph.Send, draftChecksSwitch,
                draftCheckOptions));

        AddSection(Translator.SettingsRendering_RelatedHeader, null,
            CommandCard(Translator.PrivacySecurity_Title, Translator.SettingsRendering_RelatedPrivacy_Description, WinoIconGlyph.Shield, vm.OpenPrivacySettingsCommand),
            CommandCard(Translator.ReaderActions_Title, Translator.SettingsRendering_RelatedReaderActions_Description, WinoIconGlyph.ReOrderDotsVertical, vm.OpenReaderActionsCommand));
    }

    /// <summary>The shared font list uses Skia; fall back to the native font families when it is unavailable.</summary>
    private List<string> AvailableFonts()
    {
        try
        {
            var fonts = ViewModel.AvailableFonts;
            if (fonts is { Count: > 0 }) return fonts;
        }
        catch (Exception exception) { Logger.CaptureException(exception, nameof(ReadComposePanePageViewController)); }
        return UIFont.FamilyNames.OrderBy(name => name).ToList();
    }

    private static UIFont PreviewFont(string? family, int size)
    {
        var pointSize = (nfloat)Math.Clamp(size <= 0 ? 13 : size, 8, 48);
        if (string.IsNullOrWhiteSpace(family)) return UIFont.SystemFontOfSize(pointSize);

        // The list holds family names; a family descriptor resolves them, a font name is the fallback.
        if (UIFont.FamilyNames.Contains(family))
            return UIFont.FromDescriptor(new UIFontDescriptor(new UIFontAttributes { Family = family }), pointSize);
        return UIFont.FromName(family, pointSize) ?? UIFont.SystemFontOfSize(pointSize);
    }
}
