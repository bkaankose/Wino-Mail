using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

public class FontService() : IFontService
{
    private static readonly Lazy<List<string>> _availableFonts = new(InitializeFonts);
    private static readonly List<string> _defaultFonts = ["Arial", "Calibri", "Trebuchet MS", "Tahoma", "Verdana", "Courier New", "Georgia", "Times New Roman"];

    private static List<string> InitializeFonts()
    {
        // TODO: Skia used to get system fonts. This is a temporary solution to support UWP and WinUI together.
        // After migration to WinUI, this code should be replaced with lightweight solution.
        var fontFamilies = SKFontManager.Default.FontFamilies;

        List<string> combinedFonts = [.. fontFamilies, .. _defaultFonts];

        return [.. combinedFonts.Distinct().OrderBy(x => x)];
    }

    public List<string> GetFonts() => _availableFonts.Value;
}
