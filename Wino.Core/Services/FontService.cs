using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using SkiaSharp;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

public class FontService(IPreferencesService preferencesService) : IFontService
{
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly ILogger _logger = Log.ForContext<FontService>();

    private static readonly Lazy<List<string>> _availableFonts = new(InitializeFonts);
    private static readonly List<string> _defaultFonts = ["Arial", "Calibri", "Trebuchet MS", "Tahoma", "Verdana", "Courier New", "Georgia", "Times New Roman"];

    private const string FallbackFont = "Arial";

    private static List<string> InitializeFonts()
    {
        // TODO: Skia used to get system fonts. This is a temporary solution to support UWP and WinUI together.
        // After migration to WinUI, this code should be replaced with lightweight solution.
        var fontFamilies = SKFontManager.Default.FontFamilies;

        List<string> combinedFonts = [.. fontFamilies, .. _defaultFonts];

        return [.. combinedFonts.Distinct().OrderBy(x => x)];
    }

    public List<string> GetFonts() => _availableFonts.Value;

    public string GetCurrentReaderFont() => _availableFonts.Value.Find(f => f == _preferencesService.ReaderFont) ?? FallbackFont;
    public int GetCurrentReaderFontSize() => _preferencesService.ReaderFontSize;

    public void SetReaderFont(string font)
    {
        _preferencesService.ReaderFont = font;

        _logger.Information("Default reader font is changed to {Font}", font);
    }

    public void SetReaderFontSize(int size)
    {
        _preferencesService.ReaderFontSize = size;

        _logger.Information("Default reader font size is changed to {Size}", size);
    }

    public string GetCurrentComposerFont() => _availableFonts.Value.Find(f => f == _preferencesService.ComposerFont) ?? FallbackFont;
    public int GetCurrentComposerFontSize() => _preferencesService.ComposerFontSize;

    public void SetComposerFont(string font)
    {
        _preferencesService.ComposerFont = font;

        _logger.Information("Default composer font is changed to {Font}", font);
    }

    public void SetComposerFontSize(int size)
    {
        _preferencesService.ComposerFontSize = size;

        _logger.Information("Default composer font size is changed to {Size}", size);
    }
}
