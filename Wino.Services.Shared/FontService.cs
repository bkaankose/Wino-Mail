using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class FontService() : IFontService
{
    private static readonly Lazy<List<string>> _availableFonts = new(InitializeFonts);
    private static readonly List<string> _defaultFonts = ["Arial", "Calibri", "Trebuchet MS", "Tahoma", "Verdana", "Courier New", "Georgia", "Times New Roman"];

    private static List<string> InitializeFonts()
    {
        var fontFamilies = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsFontFamilies()
            : [];

        List<string> combinedFonts = [.. fontFamilies, .. _defaultFonts];

        return [.. combinedFonts.Distinct().OrderBy(x => x)];
    }

    public List<string> GetFonts() => _availableFonts.Value;

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetWindowsFontFamilies()
    {
        using var fontsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
        if (fontsKey is null)
        {
            yield break;
        }

        foreach (var valueName in fontsKey.GetValueNames())
        {
            var familyName = GetFontFamilyName(valueName);
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                yield return familyName;
            }
        }
    }

    private static string GetFontFamilyName(string registryValueName)
    {
        var familyName = registryValueName;
        var metadataStart = familyName.IndexOf(" (", StringComparison.Ordinal);
        if (metadataStart >= 0)
        {
            familyName = familyName[..metadataStart];
        }

        return familyName.Trim();
    }
}
