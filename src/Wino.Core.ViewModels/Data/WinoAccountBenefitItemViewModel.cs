#nullable enable
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// One selectable offer on the signed-out Wino Account page. Everything the tile and the
/// detail panel below it render is resolved once at construction; nothing here changes
/// while the page is open.
/// </summary>
public partial class WinoAccountBenefitItemViewModel : ObservableObject
{
    public WinoAccountBenefitType Type { get; }

    public string Title { get; }

    /// <summary>
    /// The single line under the title inside the tile.
    /// </summary>
    public string Caption { get; }

    public string BadgeText { get; }

    /// <summary>
    /// True for the offers included with any account, false for the paid add-ons. Only
    /// tints the badge; it does not gate anything.
    /// </summary>
    public bool IsFreeBadge { get; }

    public string Lede { get; }

    public IReadOnlyList<string> Points { get; }

    public string CtaText { get; }

    /// <summary>
    /// Filled path markup authored on the same 20x20 grid and at the same optical weight
    /// as every other benefit glyph, which is what keeps the four tile icons reading as
    /// one set. Rendered through XamlHelpers.GetPathGeometry so a single DataTemplate can
    /// serve all four tiles.
    /// </summary>
    public string GlyphPathData { get; }

    /// <summary>
    /// Automation identity for the tile. Derived from the type so UI tests do not depend
    /// on localized text.
    /// </summary>
    public string AutomationId => $"WinoAccountBenefit{Type}";

    public ICommand? CtaCommand { get; set; }

    public WinoAccountBenefitItemViewModel(WinoAccountBenefitType type,
                                          string title,
                                          string caption,
                                          string badgeText,
                                          bool isFreeBadge,
                                          string lede,
                                          IReadOnlyList<string> points,
                                          string ctaText,
                                          string glyphPathData)
    {
        Type = type;
        Title = title;
        Caption = caption;
        BadgeText = badgeText;
        IsFreeBadge = isFreeBadge;
        Lede = lede;
        Points = points;
        CtaText = ctaText;
        GlyphPathData = glyphPathData;
    }
}
