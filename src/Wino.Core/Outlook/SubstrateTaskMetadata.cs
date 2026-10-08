using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Outlook;

/// <summary>
/// Translates the substrate API's presentation fields into Wino's own vocabulary.
///
/// Both maps are deliberately conservative: an unrecognized value returns false and the caller
/// keeps whatever it already had. This API is undocumented, so a value we have not actually
/// observed is a guess, and a wrong guess silently mis-renders or mis-sorts a user's list.
/// </summary>
public static class SubstrateTaskMetadata
{
    /// <summary>
    /// Microsoft To Do palette names mapped to hex. These are close matches to the To Do swatches
    /// rather than published brand values — the palette is not documented anywhere.
    /// </summary>
    private static readonly Dictionary<string, string> ThemeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "#E74856",
        ["dark_red"] = "#C42B1C",
        ["pink"] = "#EA5C9A",
        ["dark_pink"] = "#C2367A",
        ["orange"] = "#F7630C",
        ["dark_orange"] = "#CA5010",
        ["yellow"] = "#FFB900",
        ["dark_yellow"] = "#C19C00",
        ["green"] = "#4CA82F",
        ["dark_green"] = "#0B6A0B",
        ["teal"] = "#00B7C3",
        ["dark_teal"] = "#038387",
        ["blue"] = "#0078D4",
        ["dark_blue"] = "#003E92",
        ["purple"] = "#8764B8",
        ["dark_purple"] = "#5C2E91",
        ["olive"] = "#7A7574",
        ["dark_olive"] = "#565049",
        ["brown"] = "#8E562E",
        ["dark_brown"] = "#5D3A1A",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["dark_gray"] = "#5D5A58",
        ["dark_grey"] = "#5D5A58"
    };

    /// <summary>
    /// Substrate reports the per-list sort as an integer whose meaning is not published. Only
    /// values confirmed against a real mailbox belong here; everything else must stay unmapped so
    /// Wino keeps its own default rather than sorting the list wrongly.
    ///
    /// Confirmed so far: 0, which is To Do's manual "my order" and has no Wino equivalent.
    /// </summary>
    private static readonly Dictionary<int, TaskSortKind> SortKinds = [];

    public static bool TryGetColorHex(string themeColor, out string colorHex)
    {
        colorHex = null;

        if (string.IsNullOrWhiteSpace(themeColor))
            return false;

        return ThemeColors.TryGetValue(themeColor.Trim(), out colorHex);
    }

    public static bool TryGetSortKind(int? sortType, out TaskSortKind sortKind)
    {
        sortKind = default;
        return sortType.HasValue && SortKinds.TryGetValue(sortType.Value, out sortKind);
    }
}
