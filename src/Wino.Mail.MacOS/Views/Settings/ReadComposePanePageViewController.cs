using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Read and compose pane: reader and composer fonts, spell check and rendering options (Windows ReadComposePanePage).</summary>
public sealed class ReadComposePanePageViewController(ReadComposePanePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ReadComposePanePageViewModel>(viewModel, dispatcher, logger)
{
    // English literal used only for the font preview sample (Windows shows a fixed sample too).
    private const string PreviewSample = "The quick brown fox jumps over the lazy dog.";

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        var fonts = AvailableFonts();

        var readerPreview = WinoStyle.Label(PreviewSample, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        void UpdateReader() => readerPreview.Font = PreviewFont(vm.CurrentReaderFont, vm.CurrentReaderFontSize);
        Bind.Bind(vm, nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, _ => UpdateReader());
        Bind.Bind(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, _ => UpdateReader());

        var composerPreview = WinoStyle.Label(PreviewSample, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        void UpdateComposer() => composerPreview.Font = PreviewFont(vm.CurrentComposerFont, vm.CurrentComposerFontSize);
        Bind.Bind(vm, nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, _ => UpdateComposer());
        Bind.Bind(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, _ => UpdateComposer());

        AddGroup(null,
            Expander(Translator.SettingsReaderFont_Title, Translator.SettingsReaderFontFamily_Description, WinoIconGlyph.TextFont, null,
                Card(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentReaderFont), s => s.CurrentReaderFont, (s, v) => s.CurrentReaderFont = v, width: 180)),
                Card(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentReaderFontSize), s => s.CurrentReaderFontSize, (s, v) => s.CurrentReaderFontSize = v, 8, 48,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                Card(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, readerPreview)),
            Expander(Translator.SettingsComposerFont_Title, Translator.SettingsComposerFontFamily_Description, WinoIconGlyph.TextFont, null,
                Card(Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, string>(vm, _ => fonts, font => font,
                        nameof(vm.CurrentComposerFont), s => s.CurrentComposerFont, (s, v) => s.CurrentComposerFont = v, width: 180)),
                Card(Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    Bind.Stepper(vm, nameof(vm.CurrentComposerFontSize), s => s.CurrentComposerFontSize, (s, v) => s.CurrentComposerFontSize = v, 8, 48,
                        accessibilityLabel: Translator.SettingsFontSize_Title)),
                Card(Translator.SettingsFontPreview_Title, null, WinoIconGlyph.None, composerPreview)),
            Expander(Translator.SettingsComposerSpellCheck_Title, Translator.SettingsComposerSpellCheck_Description, WinoIconGlyph.TextProofingTools, null,
                Card(Translator.SettingsComposerSpellCheckEnabled_Title, null, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsComposerSpellCheckEnabled), s => s.IsComposerSpellCheckEnabled, (s, v) => s.IsComposerSpellCheckEnabled = v, Translator.SettingsComposerSpellCheckEnabled_Title)),
                Card(Translator.SettingsComposerAutoCorrect_Title, Translator.SettingsComposerAutoCorrect_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsComposerAutoCorrectEnabled), s => s.IsComposerAutoCorrectEnabled, (s, v) => s.IsComposerAutoCorrectEnabled = v, Translator.SettingsComposerAutoCorrect_Title)),
                Card(Translator.SettingsComposerSpellCheckLanguage_Title, null, WinoIconGlyph.None,
                    Bind.PopUp<ReadComposePanePageViewModel, AppLanguageModel>(vm, s => s.AvailableSpellCheckLanguages, language => language.DisplayName,
                        nameof(vm.CurrentSpellCheckLanguage), s => s.CurrentSpellCheckLanguage, (s, v) => s.CurrentSpellCheckLanguage = v, width: 180))));

        AddGroup(null,
            Card(Translator.SettingsMailRendering_ActionLabels_Title, Translator.SettingsMailRendering_ActionLabels_Description, WinoIconGlyph.ToggleLeft,
                Bind.Switch(p, nameof(p.IsShowActionLabelsEnabled), s => s.IsShowActionLabelsEnabled, (s, v) => s.IsShowActionLabelsEnabled = v, Translator.SettingsMailRendering_ActionLabels_Title)),
            Card(Translator.SettingsLoadImages_Title, null, WinoIconGlyph.Image,
                Bind.Switch(p, nameof(p.RenderImages), s => s.RenderImages, (s, v) => s.RenderImages = v, Translator.SettingsLoadImages_Title)),
            Card(Translator.SettingsLoadStyles_Title, null, WinoIconGlyph.Color,
                Bind.Switch(p, nameof(p.RenderStyles), s => s.RenderStyles, (s, v) => s.RenderStyles = v, Translator.SettingsLoadStyles_Title)),
            Card(Translator.SettingsLoadPlaintextLinks_Title, null, WinoIconGlyph.Link,
                Bind.Switch(p, nameof(p.RenderPlaintextLinks), s => s.RenderPlaintextLinks, (s, v) => s.RenderPlaintextLinks = v, Translator.SettingsLoadPlaintextLinks_Title)));
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
        return NSFontManager.SharedFontManager.AvailableFontFamilies.OrderBy(name => name).ToList();
    }

    private static NSFont PreviewFont(string? family, int size)
    {
        var pointSize = Math.Clamp(size <= 0 ? 13 : size, 8, 48);
        return (string.IsNullOrWhiteSpace(family) ? null : NSFont.FromFontName(family, pointSize)) ?? NSFont.SystemFontOfSize(pointSize);
    }
}
