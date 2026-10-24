using Android.Graphics;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Read and compose pane: reader and composer fonts, spell check and rendering options (Windows ReadComposePanePage,
/// iOS ReadComposePanePageViewController). A phone has no reading pane to lay out and no detached compose window, so
/// only the font, spell check and rendering options apply, as on iOS.
/// </summary>
public sealed class ReadComposePanePageFragment : SettingsPageFragment<ReadComposePanePageViewModel>
{
    // English literal used only for the font preview sample (Windows shows a fixed sample too).
    private const string PreviewSample = "The quick brown fox jumps over the lazy dog.";

    /// <summary>The Android system font families, used when the shared font list is unavailable.</summary>
    private static readonly List<string> SystemFamilies =
        ["sans-serif", "sans-serif-condensed", "sans-serif-light", "sans-serif-medium", "serif", "monospace", "serif-monospace", "casual", "cursive"];

    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;
        var fonts = AvailableFonts();

        var readerPreview = Text(PreviewSample, WinoTextStyle.Body);
        void UpdateReader() => ApplyPreviewFont(readerPreview, vm.CurrentReaderFont, vm.CurrentReaderFontSize);
        Bind.Bind(vm, nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, _ => UpdateReader());
        Bind.Bind(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, _ => UpdateReader());

        var composerPreview = Text(PreviewSample, WinoTextStyle.Body);
        void UpdateComposer() => ApplyPreviewFont(composerPreview, vm.CurrentComposerFont, vm.CurrentComposerFontSize);
        Bind.Bind(vm, nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, _ => UpdateComposer());
        Bind.Bind(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, _ => UpdateComposer());

        AddGroup(null,
            Expander(Translator.SettingsReaderFont_Title, Translator.SettingsReaderFontFamily_Description, WinoIconGlyph.TextFont, null,
                StackedCard(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, (s, v) => s.CurrentReaderFont = v)),
                StackedCard(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, (s, v) => s.CurrentReaderFontSize = v, 8, 48,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                StackedCard(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, readerPreview)),
            Expander(Translator.SettingsComposerFont_Title, Translator.SettingsComposerFontFamily_Description, WinoIconGlyph.TextFont, null,
                StackedCard(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, (s, v) => s.CurrentComposerFont = v)),
                StackedCard(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, (s, v) => s.CurrentComposerFontSize = v, 8, 48,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                StackedCard(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, composerPreview)),
            Expander(Translator.SettingsComposerSpellCheck_Title, Translator.SettingsComposerSpellCheck_Description, WinoIconGlyph.TextProofingTools, null,
                Card(Translator.SettingsComposerSpellCheckEnabled_Title, null, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled, (s, v) => s.IsComposerSpellCheckEnabled = v, Translator.SettingsComposerSpellCheckEnabled_Title)),
                Card(Translator.SettingsComposerAutoCorrect_Title, Translator.SettingsComposerAutoCorrect_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsComposerAutoCorrectEnabled), s => s.IsComposerAutoCorrectEnabled, (s, v) => s.IsComposerAutoCorrectEnabled = v, Translator.SettingsComposerAutoCorrect_Title)),
                StackedCard(Translator.SettingsComposerSpellCheckLanguage_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, AppLanguageModel>(vm, s => s.AvailableSpellCheckLanguages, language => language.DisplayName,
                        nameof(vm.CurrentSpellCheckLanguage), s => s.CurrentSpellCheckLanguage, (s, v) => s.CurrentSpellCheckLanguage = v))));

        AddGroup(null,
            NavigationCard(Translator.ReaderActions_Title, Translator.ReaderActions_EntryDescription, WinoIconGlyph.ReOrderDotsVertical,
                () => vm.OpenReaderActionsCommand.Execute(null)),
            Card(Translator.SettingsLoadImages_Title, null, WinoIconGlyph.Image,
                Bind.Switch(p, nameof(p.RenderImages), s => s.RenderImages, (s, v) => s.RenderImages = v, Translator.SettingsLoadImages_Title)),
            Card(Translator.SettingsLoadStyles_Title, null, WinoIconGlyph.Color,
                Bind.Switch(p, nameof(p.RenderStyles), s => s.RenderStyles, (s, v) => s.RenderStyles = v, Translator.SettingsLoadStyles_Title)),
            Card(Translator.SettingsLoadPlaintextLinks_Title, null, WinoIconGlyph.Link,
                Bind.Switch(p, nameof(p.RenderPlaintextLinks), s => s.RenderPlaintextLinks, (s, v) => s.RenderPlaintextLinks = v, Translator.SettingsLoadPlaintextLinks_Title)));
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
