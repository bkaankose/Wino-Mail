using Android.Content;
using Android.Content.Res;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;
using Wino.Renderer.Primitives;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Rendering;

/// <summary>One row of an engine menu. A null row in the list is a separator.</summary>
internal sealed record EngineMenuEntry(string Text, WinoIconGlyph? Icon, Action Run, bool IsEnabled = true, string? AutomationId = null);

/// <summary>
/// The menu over a mail view in the reader and the composer (Windows EngineContextMenu): a popup drawn in the theme of
/// the message, which can differ from the app's, and anchored where the user pressed. The anchor is a view placed in the
/// host at the point, so the popup follows the content when it scrolls.
/// </summary>
internal static class EngineContextMenu
{
    public static void Show(FrameLayout host, PxPoint point, float density, bool dark, IReadOnlyList<EngineMenuEntry?> entries)
    {
        var rows = Trimmed(entries);
        if (rows.Count == 0 || host.Context is not { } context) return;

        var themed = ThemedContext(context, dark);
        var anchor = new View(context)
        {
            LayoutParameters = new FrameLayout.LayoutParams(1, 1) { LeftMargin = (int)(point.X * density), TopMargin = (int)(point.Y * density) },
        };
        host.AddView(anchor);

        var popup = new PopupMenu(themed, anchor);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not { } row) continue;
            var item = popup.Menu.Add(0, index, index, row.Text);
            item.SetEnabled(row.IsEnabled);
            if (row.Icon is { } glyph) item.SetIcon(WinoIcons.Drawable(themed, glyph, 20, WinoColors.PrimaryText(themed)));
        }

        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId is int id && id >= 0 && id < rows.Count && rows[id] is { } chosen) chosen.Run();
        };
        popup.DismissEvent += (_, _) => host.RemoveView(anchor);
        popup.Show();
    }

    private static List<EngineMenuEntry?> Trimmed(IReadOnlyList<EngineMenuEntry?> entries)
    {
        var rows = new List<EngineMenuEntry?>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                if (rows.Count > 0 && rows[^1] is not null) rows.Add(null);
                continue;
            }
            rows.Add(entry);
        }
        while (rows.Count > 0 && rows[^1] is null) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static Context ThemedContext(Context context, bool dark)
    {
        var configuration = new Configuration(context.Resources?.Configuration);
        var mode = (int)configuration.UiMode & ~(int)UiMode.NightMask;
        configuration.UiMode = (UiMode)(mode | (int)(dark ? UiMode.NightYes : UiMode.NightNo));
        return new ContextThemeWrapper(context.CreateConfigurationContext(configuration), Resource.Style.Theme_Wino);
    }
}
