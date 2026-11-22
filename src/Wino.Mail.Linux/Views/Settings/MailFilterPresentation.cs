using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>Filter tiles, tones and the row meta line, shared by the filter list and editor.</summary>
internal static class MailFilterPresentation
{
    public const string TileClass = "wino-filter-tile";

    public static string ToneClass(MailFilterActionType? action) => MailFilterDisplay.GetActionTone(action) switch
    {
        MailFilterActionTone.Success => "success",
        MailFilterActionTone.Caution => "warning",
        MailFilterActionTone.Critical => "error",
        _ => "accent",
    };

    /// <summary>A 32 px rounded tile with the glyph in the tone colour (the Windows filter row tile).</summary>
    public static Gtk.Widget Tile(WinoIconGlyph glyph, string toneClass)
    {
        var tile = Adw.Bin.New();
        tile.AddCssClass(TileClass);
        tile.AddCssClass(toneClass);
        tile.SetValign(Gtk.Align.Center);
        var icon = WinoIcons.Label(glyph, 16);
        icon.SetHalign(Gtk.Align.Center);
        icon.SetValign(Gtk.Align.Center);
        tile.SetChild(icon);
        return tile;
    }

    /// <summary>"management · source folder · status", skipping empty parts (the macOS meta line).</summary>
    public static string MetaText(MailFilterListItemViewModel item, string? status = null)
        => string.Join(" · ", new[] { item.ManagementText, item.Filter.SourceFolderName, status ?? item.StatusText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
}
