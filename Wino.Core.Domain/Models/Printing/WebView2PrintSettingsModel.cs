using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Printing;

/// <summary>
/// Wrapper model for CoreWebView2PrintSettings that provides bindable properties for UI controls.
/// </summary>
public partial class WebView2PrintSettingsModel : ObservableObject
{
    [ObservableProperty]
    public partial string PrinterName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PrintOrientation Orientation { get; set; } = PrintOrientation.Portrait;

    [ObservableProperty]
    public partial PrintColorMode ColorMode { get; set; } = PrintColorMode.Color;

    [ObservableProperty]
    public partial PrintCollation Collation { get; set; } = PrintCollation.Default;

    [ObservableProperty]
    public partial PrintDuplex Duplex { get; set; } = PrintDuplex.Default;

    [ObservableProperty]
    public partial PrintMediaSize MediaSize { get; set; } = PrintMediaSize.Default;

    [ObservableProperty]
    public partial int Copies { get; set; } = 1;

    [ObservableProperty]
    public partial double MarginTop { get; set; } = 1.0;

    [ObservableProperty]
    public partial double MarginBottom { get; set; } = 1.0;

    [ObservableProperty]
    public partial double MarginLeft { get; set; } = 1.0;

    [ObservableProperty]
    public partial double MarginRight { get; set; } = 1.0;

    [ObservableProperty]
    public partial bool ShouldPrintBackgrounds { get; set; } = false;

    [ObservableProperty]
    public partial bool ShouldPrintSelectionOnly { get; set; } = false;

    [ObservableProperty]
    public partial bool ShouldPrintHeaderAndFooter { get; set; } = true;

    [ObservableProperty]
    public partial string HeaderTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FooterUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ScaleFactor { get; set; } = 1.0;

    [ObservableProperty]
    public partial int PagesPerSide { get; set; } = 1;

    [ObservableProperty]
    public partial string PageRanges { get; set; } = string.Empty;

    /// <summary>
    /// Partial method for validation when Copies property changes.
    /// </summary>
    partial void OnCopiesChanged(int value)
    {
        if (value <= 0)
        {
            Copies = 1; // Reset to minimum valid value
        }
    }

    /// <summary>
    /// Partial method for validation when ScaleFactor property changes.
    /// </summary>
    partial void OnScaleFactorChanged(double value)
    {
        if (value < 0.1 || value > 2.0)
        {
            ScaleFactor = Math.Clamp(value, 0.1, 2.0);
        }
    }

    /// <summary>
    /// Partial method for validation when PagesPerSide property changes.
    /// </summary>
    partial void OnPagesPerSideChanged(int value)
    {
        var validValues = new[] { 1, 2, 4, 6, 9, 16 };
        if (System.Array.IndexOf(validValues, value) < 0)
        {
            PagesPerSide = 1; // Reset to default valid value
        }
    }

    /// <summary>
    /// Partial method for validation when margin properties change.
    /// </summary>
    partial void OnMarginTopChanged(double value)
    {
        if (value < 0)
        {
            MarginTop = 0;
        }
    }

    partial void OnMarginBottomChanged(double value)
    {
        if (value < 0)
        {
            MarginBottom = 0;
        }
    }

    partial void OnMarginLeftChanged(double value)
    {
        if (value < 0)
        {
            MarginLeft = 0;
        }
    }

    partial void OnMarginRightChanged(double value)
    {
        if (value < 0)
        {
            MarginRight = 0;
        }
    }
}
