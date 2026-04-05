using Microsoft.Web.WebView2.Core;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Mail.WinUI.Extensions;

/// <summary>
/// Extension methods and utilities for converting between Domain print models and CoreWebView2 print settings.
/// </summary>
public static class PrintSettingsExtensions
{
    /// <summary>
    /// Converts a Domain PrintOrientation to CoreWebView2PrintOrientation.
    /// </summary>
    public static CoreWebView2PrintOrientation ToCoreWebView2Orientation(this PrintOrientation orientation)
    {
        return orientation switch
        {
            PrintOrientation.Portrait => CoreWebView2PrintOrientation.Portrait,
            PrintOrientation.Landscape => CoreWebView2PrintOrientation.Landscape,
            _ => CoreWebView2PrintOrientation.Portrait
        };
    }

    /// <summary>
    /// Converts a CoreWebView2PrintOrientation to Domain PrintOrientation.
    /// </summary>
    public static PrintOrientation ToDomainOrientation(this CoreWebView2PrintOrientation orientation)
    {
        return orientation switch
        {
            CoreWebView2PrintOrientation.Portrait => PrintOrientation.Portrait,
            CoreWebView2PrintOrientation.Landscape => PrintOrientation.Landscape,
            _ => PrintOrientation.Portrait
        };
    }

    /// <summary>
    /// Converts a Domain PrintColorMode to CoreWebView2PrintColorMode.
    /// </summary>
    public static CoreWebView2PrintColorMode ToCoreWebView2ColorMode(this PrintColorMode colorMode)
    {
        return colorMode switch
        {
            PrintColorMode.Default => CoreWebView2PrintColorMode.Default,
            PrintColorMode.Color => CoreWebView2PrintColorMode.Color,
            PrintColorMode.Grayscale => CoreWebView2PrintColorMode.Grayscale,
            _ => CoreWebView2PrintColorMode.Default
        };
    }

    /// <summary>
    /// Converts a CoreWebView2PrintColorMode to Domain PrintColorMode.
    /// </summary>
    public static PrintColorMode ToDomainColorMode(this CoreWebView2PrintColorMode colorMode)
    {
        return colorMode switch
        {
            CoreWebView2PrintColorMode.Default => PrintColorMode.Default,
            CoreWebView2PrintColorMode.Color => PrintColorMode.Color,
            CoreWebView2PrintColorMode.Grayscale => PrintColorMode.Grayscale,
            _ => PrintColorMode.Default
        };
    }

    /// <summary>
    /// Converts a Domain PrintCollation to CoreWebView2PrintCollation.
    /// </summary>
    public static CoreWebView2PrintCollation ToCoreWebView2Collation(this PrintCollation collation)
    {
        return collation switch
        {
            PrintCollation.Default => CoreWebView2PrintCollation.Default,
            PrintCollation.Collated => CoreWebView2PrintCollation.Collated,
            PrintCollation.Uncollated => CoreWebView2PrintCollation.Uncollated,
            _ => CoreWebView2PrintCollation.Default
        };
    }

    /// <summary>
    /// Converts a CoreWebView2PrintCollation to Domain PrintCollation.
    /// </summary>
    public static PrintCollation ToDomainCollation(this CoreWebView2PrintCollation collation)
    {
        return collation switch
        {
            CoreWebView2PrintCollation.Default => PrintCollation.Default,
            CoreWebView2PrintCollation.Collated => PrintCollation.Collated,
            CoreWebView2PrintCollation.Uncollated => PrintCollation.Uncollated,
            _ => PrintCollation.Default
        };
    }

    /// <summary>
    /// Converts a Domain PrintDuplex to CoreWebView2PrintDuplex.
    /// </summary>
    public static CoreWebView2PrintDuplex ToCoreWebView2Duplex(this PrintDuplex duplex)
    {
        // Note: Simplified mapping due to enum value differences
        return duplex switch
        {
            PrintDuplex.Default => CoreWebView2PrintDuplex.Default,
            _ => CoreWebView2PrintDuplex.Default
        };
    }

    /// <summary>
    /// Converts a CoreWebView2PrintDuplex to Domain PrintDuplex.
    /// </summary>
    public static PrintDuplex ToDomainDuplex(this CoreWebView2PrintDuplex duplex)
    {
        // Note: Simplified mapping due to enum value differences
        return duplex switch
        {
            CoreWebView2PrintDuplex.Default => PrintDuplex.Default,
            _ => PrintDuplex.Default
        };
    }

    /// <summary>
    /// Converts a Domain PrintMediaSize to CoreWebView2PrintMediaSize.
    /// </summary>
    public static CoreWebView2PrintMediaSize ToCoreWebView2MediaSize(this PrintMediaSize mediaSize)
    {
        // Note: Simplified mapping due to enum value differences
        return mediaSize switch
        {
            PrintMediaSize.Default => CoreWebView2PrintMediaSize.Default,
            _ => CoreWebView2PrintMediaSize.Default
        };
    }

    /// <summary>
    /// Converts a CoreWebView2PrintMediaSize to Domain PrintMediaSize.
    /// </summary>
    public static PrintMediaSize ToDomainMediaSize(this CoreWebView2PrintMediaSize mediaSize)
    {
        // Note: Simplified mapping due to enum value differences
        return mediaSize switch
        {
            CoreWebView2PrintMediaSize.Default => PrintMediaSize.Default,
            _ => PrintMediaSize.Default
        };
    }

    /// <summary>
    /// Creates a CoreWebView2PrintSettings object from a WebView2PrintSettingsModel.
    /// </summary>
    /// <param name="model">The domain model containing the print settings.</param>
    /// <param name="environment">The CoreWebView2Environment to create the settings object.</param>
    /// <returns>A configured CoreWebView2PrintSettings object.</returns>
    public static CoreWebView2PrintSettings ToCoreWebView2PrintSettings(
        this WebView2PrintSettingsModel model, 
        CoreWebView2Environment environment)
    {
        var settings = environment.CreatePrintSettings();

        settings.PrinterName = model.PrinterName;
        settings.Orientation = model.Orientation.ToCoreWebView2Orientation();
        settings.ColorMode = model.ColorMode.ToCoreWebView2ColorMode();
        settings.Collation = model.Collation.ToCoreWebView2Collation();
        settings.Duplex = model.Duplex.ToCoreWebView2Duplex();
        settings.MediaSize = model.MediaSize.ToCoreWebView2MediaSize();
        settings.Copies = model.Copies;
        settings.MarginTop = model.MarginTop;
        settings.MarginBottom = model.MarginBottom;
        settings.MarginLeft = model.MarginLeft;
        settings.MarginRight = model.MarginRight;
        settings.ShouldPrintBackgrounds = model.ShouldPrintBackgrounds;
        settings.ShouldPrintSelectionOnly = model.ShouldPrintSelectionOnly;
        settings.ShouldPrintHeaderAndFooter = model.ShouldPrintHeaderAndFooter;
        settings.HeaderTitle = model.HeaderTitle;
        settings.FooterUri = model.FooterUri;
        settings.ScaleFactor = model.ScaleFactor;
        settings.PagesPerSide = model.PagesPerSide;
        settings.PageRanges = model.PageRanges;

        return settings;
    }

    /// <summary>
    /// Updates a WebView2PrintSettingsModel from a CoreWebView2PrintSettings object.
    /// </summary>
    /// <param name="model">The domain model to update.</param>
    /// <param name="settings">The source CoreWebView2PrintSettings.</param>
    public static void FromCoreWebView2PrintSettings(
        this WebView2PrintSettingsModel model,
        CoreWebView2PrintSettings settings)
    {
        if (settings == null) return;

        model.PrinterName = settings.PrinterName ?? string.Empty;
        model.Orientation = settings.Orientation.ToDomainOrientation();
        model.ColorMode = settings.ColorMode.ToDomainColorMode();
        model.Collation = settings.Collation.ToDomainCollation();
        model.Duplex = settings.Duplex.ToDomainDuplex();
        model.MediaSize = settings.MediaSize.ToDomainMediaSize();
        model.Copies = settings.Copies;
        model.MarginTop = settings.MarginTop;
        model.MarginBottom = settings.MarginBottom;
        model.MarginLeft = settings.MarginLeft;
        model.MarginRight = settings.MarginRight;
        model.ShouldPrintBackgrounds = settings.ShouldPrintBackgrounds;
        model.ShouldPrintSelectionOnly = settings.ShouldPrintSelectionOnly;
        model.ShouldPrintHeaderAndFooter = settings.ShouldPrintHeaderAndFooter;
        model.HeaderTitle = settings.HeaderTitle ?? string.Empty;
        model.FooterUri = settings.FooterUri ?? string.Empty;
        model.ScaleFactor = settings.ScaleFactor;
        model.PagesPerSide = settings.PagesPerSide;
        model.PageRanges = settings.PageRanges ?? string.Empty;
    }
}