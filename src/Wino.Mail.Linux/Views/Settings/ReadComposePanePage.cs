using System.Globalization;
using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>Reader and composer: fonts, message colours, spell check and rendering options (Windows ReadComposePanePage).</summary>
internal sealed class ReadComposePanePage(ReadComposePanePageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ReadComposePanePageViewModel>(viewModel, services)
{
    // English literal used only for the font preview sample (Windows and macOS show the same fixed sample).
    private const string PreviewSample = "The quick brown fox jumps over the lazy dog.";

    private static readonly string[] FallbackFonts =
        ["Arial", "Calibri", "Cantarell", "Courier New", "DejaVu Sans", "Georgia", "Noto Sans", "Noto Serif", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana"];

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        var fonts = AvailableFonts(vm.CurrentReaderFont, vm.CurrentComposerFont);

        var readerPreview = WinoSettingsRows.Caption(null);
        readerPreview.RemoveCssClass("dim-label");
        void UpdateReader() => readerPreview.SetMarkup(PreviewMarkup(vm.CurrentReaderFont, vm.CurrentReaderFontSize));
        Settings.Bind(vm, nameof(vm.CurrentReaderFont), static s => s.CurrentReaderFont, _ => UpdateReader());
        Settings.Bind(vm, nameof(vm.CurrentReaderFontSize), static s => s.CurrentReaderFontSize, _ => UpdateReader());

        var composerPreview = WinoSettingsRows.Caption(null);
        composerPreview.RemoveCssClass("dim-label");
        void UpdateComposer() => composerPreview.SetMarkup(PreviewMarkup(vm.CurrentComposerFont, vm.CurrentComposerFontSize));
        Settings.Bind(vm, nameof(vm.CurrentComposerFont), static s => s.CurrentComposerFont, _ => UpdateComposer());
        Settings.Bind(vm, nameof(vm.CurrentComposerFontSize), static s => s.CurrentComposerFontSize, _ => UpdateComposer());

        AddGroup(null,
            WinoSettingsRows.Expander(Translator.SettingsReaderFont_Title, Translator.SettingsReaderFontFamily_Description, WinoIconGlyph.TextFont, false,
                Settings.Choice<ReadComposePanePageViewModel, string>(vm, Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    _ => fonts, static font => font,
                    nameof(vm.CurrentReaderFont), static s => s.CurrentReaderFont, static (s, v) => s.CurrentReaderFont = v),
                Settings.Number(vm, Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    nameof(vm.CurrentReaderFontSize), static s => s.CurrentReaderFontSize, static (s, v) => s.CurrentReaderFontSize = v, 8, 48),
                PreviewRow(readerPreview)),
            WinoSettingsRows.Expander(Translator.SettingsReaderDarkMode_Title, Translator.SettingsReaderDarkMode_Description, WinoIconGlyph.DarkEditor, false,
                Settings.Choice(vm, Translator.SettingsReaderMessageColors_Title, Translator.SettingsReaderMessageColors_Description, WinoIconGlyph.None, [.. vm.ReaderContentThemeOptions],
                    nameof(vm.SelectedReaderContentThemeIndex), static s => s.SelectedReaderContentThemeIndex, static (s, v) => s.SelectedReaderContentThemeIndex = v),
                Settings.Choice(vm, Translator.SettingsReaderDarkModeMethod_Title, Translator.SettingsReaderDarkModeMethod_Description, WinoIconGlyph.None,
                    [Translator.SettingsReaderDarkModeMethod_Adaptive, Translator.SettingsReaderDarkModeMethod_Inverted],
                    nameof(vm.SelectedReaderDarkModeMethodIndex), static s => s.SelectedReaderDarkModeMethodIndex, static (s, v) => s.SelectedReaderDarkModeMethodIndex = v)),
            WinoSettingsRows.Expander(Translator.SettingsComposerFont_Title, Translator.SettingsComposerFontFamily_Description, WinoIconGlyph.TextFont, false,
                Settings.Choice<ReadComposePanePageViewModel, string>(vm, Translator.SettingsFontFamily_Title, null, WinoIconGlyph.None,
                    _ => fonts, static font => font,
                    nameof(vm.CurrentComposerFont), static s => s.CurrentComposerFont, static (s, v) => s.CurrentComposerFont = v),
                Settings.Number(vm, Translator.SettingsFontSize_Title, null, WinoIconGlyph.None,
                    nameof(vm.CurrentComposerFontSize), static s => s.CurrentComposerFontSize, static (s, v) => s.CurrentComposerFontSize = v, 8, 48),
                PreviewRow(composerPreview)),
            // Auto-correct is left out: WebKitGTK has no auto-correct.
            WinoSettingsRows.Expander(Translator.SettingsComposerSpellCheck_Title, Translator.SettingsComposerSpellCheck_Description, WinoIconGlyph.TextProofingTools, false,
                Settings.Switch(p, Translator.SettingsComposerSpellCheckEnabled_Title, null, WinoIconGlyph.None,
                    nameof(p.IsComposerSpellCheckEnabled), static s => s.IsComposerSpellCheckEnabled, static (s, v) => s.IsComposerSpellCheckEnabled = v),
                Settings.Choice<ReadComposePanePageViewModel, AppLanguageModel>(vm, Translator.SettingsComposerSpellCheckLanguage_Title, null, WinoIconGlyph.None,
                    static s => s.AvailableSpellCheckLanguages, static language => language.DisplayName,
                    nameof(vm.CurrentSpellCheckLanguage), static s => s.CurrentSpellCheckLanguage, static (s, v) => s.CurrentSpellCheckLanguage = v)));

        AddGroup(null,
            Settings.Command(Translator.ReaderActions_Title, Translator.ReaderActions_EntryDescription, WinoIconGlyph.ReOrderDotsVertical, vm.OpenReaderActionsCommand),
            Settings.Switch(p, Translator.SettingsLoadPlaintextLinks_Title, null, WinoIconGlyph.Link,
                nameof(p.RenderPlaintextLinks), static s => s.RenderPlaintextLinks, static (s, v) => s.RenderPlaintextLinks = v));
    }

    private static Adw.PreferencesRow PreviewRow(Gtk.Label preview)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        var title = Gtk.Label.New(Translator.SettingsFontPreview_Title);
        title.SetXalign(0);
        title.AddCssClass("dim-label");
        box.Append(title);
        box.Append(preview);
        return WinoSettingsRows.Custom(box, Translator.SettingsFontPreview_Title);
    }

    /// <summary>
    /// The shared list comes from SkiaSharp, which on Linux sees almost no system families (no fontconfig), so common
    /// Linux families are always added; a missing native library leaves the fixed list alone.
    /// </summary>
    private List<string> AvailableFonts(params string?[] stored)
    {
        List<string> fonts;
        try
        {
            fonts = [.. ViewModel.AvailableFonts];
            Serilog.Log.Information("Settings fonts: {Count} families from SkiaSharp.", fonts.Count);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Settings fonts: SkiaSharp failed, using the fixed list.");
            fonts = [];
        }
        foreach (var font in FallbackFonts.Concat(stored))
        {
            if (!string.IsNullOrWhiteSpace(font) && !fonts.Contains(font)) fonts.Add(font);
        }
        fonts.Sort(StringComparer.CurrentCultureIgnoreCase);
        return fonts;
    }

    private static string PreviewMarkup(string? family, int size)
    {
        var points = Math.Clamp(size <= 0 ? 13 : size, 8, 48);
        var font = string.IsNullOrWhiteSpace(family) ? string.Empty : $" font_family='{Escape(family)}'";
        return string.Create(CultureInfo.InvariantCulture, $"<span{font} size='{points}pt'>{Escape(PreviewSample)}</span>");
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '\'' => "&apos;",
                '"' => "&quot;",
                _ => character.ToString(),
            });
        }
        return builder.ToString();
    }
}
