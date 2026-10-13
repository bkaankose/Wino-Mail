using UIKit;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// Per-mode shell policy (macOS design Revision 2 and the Windows mode layouts), so mode pages never resize or replace
/// shell columns themselves:
/// <list type="bullet">
/// <item>Columns: Mail, Contacts and To Do keep the 360pt list column beside the reader column. Calendar widens the
/// content column to everything the sidebar leaves except <see cref="DetailsWidth"/>, which the reader column keeps for
/// the event details.</item>
/// <item>Sidebar content slot: a mode page may show its own sidebar content in place of the menu rows while its mode is
/// active (Calendar: New event, the month picker and the calendars); the shell drops it when the mode changes.</item>
/// <item>The reader column's idle state shows the mode's glyph.</item>
/// <item>Toolbar row at narrow widths: when the content column is narrower than <see cref="CompactContentWidth"/>, a
/// content page keeps the shell's sidebar toggle and sync plus at most its date or item navigation in the row; mode
/// controls (the calendar period) move into the page's own header, so nothing folds into the overflow menu. The search
/// field, briefing, What's New and the avatar stay over the reader column.</item>
/// </list>
/// </summary>
public sealed partial class PadShellViewController
{
    /// <summary>The reader column's width in Calendar mode (the Mac details zone is 320pt plus gutters).</summary>
    public const double DetailsWidth = 340;

    /// <summary>Below this content-column width a page keeps its toolbar row short (see the class remarks).</summary>
    public const double CompactContentWidth = 640;

    private const double MinimumListWidth = 320;
    private const double MaximumListWidth = 480;

    private (WinoApplicationMode Mode, UIViewController Content)? _sidebarContent;
    // The mode the columns are laid out for. ModeChanged runs before ActiveMode reflects the new mode.
    private WinoApplicationMode _columnMode = WinoApplicationMode.Mail;

    /// <summary>
    /// The shell's leading toolbar items (sidebar toggle, sync). A content page that builds its own leading items starts
    /// from these rather than from its navigation item, which a re-navigated page may not have received yet.
    /// </summary>
    public UIBarButtonItem[] LeadingItems => _listRoot.NavigationItem.LeftBarButtonItems ?? [];

    /// <summary>True when a content column of <paramref name="width"/> takes the narrow toolbar rule.</summary>
    public static bool IsCompactContentWidth(nfloat width) => width > 0 && width < CompactContentWidth;

    /// <summary>
    /// Shows <paramref name="content"/> in the sidebar in place of the menu rows while <paramref name="mode"/> is active.
    /// The page owns the controller and calls <see cref="ClearSidebarContent"/> when it leaves.
    /// </summary>
    public void SetSidebarContent(WinoApplicationMode mode, UIViewController content)
    {
        _ = View;
        if (_columnMode != mode && ActiveMode != mode) return;
        _sidebarContent = (mode, content);
        _sidebar.SetModeContent(content);
    }

    /// <summary>Restores the menu rows when <paramref name="content"/> is still the sidebar content.</summary>
    public void ClearSidebarContent(UIViewController content)
    {
        if (_sidebarContent is not { } current || !ReferenceEquals(current.Content, content)) return;
        _sidebarContent = null;
        _sidebar.SetModeContent(null);
    }

    /// <summary>The mode's column layout, sidebar slot and idle glyph. Settings is a sheet over the current mode.</summary>
    private void ApplyModePolicy(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings) return;
        if (_sidebarContent is { } slot && slot.Mode != mode)
        {
            _sidebarContent = null;
            _sidebar.SetModeContent(null);
        }
        ReaderFrame.SetIdleGlyph(IdleGlyph(mode));
        _columnMode = mode;
        ApplyColumns();
    }

    private static WinoIconGlyph IdleGlyph(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => WinoIconGlyph.CalendarFilled,
        WinoApplicationMode.Contacts => WinoIconGlyph.PeopleFilled,
        WinoApplicationMode.Tasks => WinoIconGlyph.CheckmarkCircleFilled,
        _ => WinoIconGlyph.MailFilled
    };

    /// <summary>
    /// Sets the content column for the active mode. Calendar pins minimum, maximum and preferred to the same width,
    /// because UIKit keeps the supplementary column near its minimum while the secondary column competes for space.
    /// </summary>
    private void ApplyColumns()
    {
        if (!IsViewLoaded || _split.View is not { } splitView) return;
        if (_columnMode != WinoApplicationMode.Calendar)
        {
            SetSupplementary(MinimumListWidth, MaximumListWidth, ListWidth);
            return;
        }
        var width = splitView.Bounds.Width;
        if (width <= 0) return;
        bool sidebarTiled = _split.DisplayMode is UISplitViewControllerDisplayMode.TwoBesideSecondary;
        var available = (double)(width - (sidebarTiled ? _split.PrimaryColumnWidth : 0)) - DetailsWidth;
        var target = Math.Max(ListWidth, available);
        SetSupplementary(target, target, target);
    }

    private void SetSupplementary(double minimum, double maximum, double preferred)
    {
        if (Math.Abs(_split.MinimumSupplementaryColumnWidth - minimum) <= 1 && Math.Abs(_split.MaximumSupplementaryColumnWidth - maximum) <= 1
            && Math.Abs(_split.PreferredSupplementaryColumnWidth - preferred) <= 1) return;
        // Widen the maximum first and narrow it last so minimum <= maximum holds at every step.
        if (_split.MaximumSupplementaryColumnWidth < maximum) _split.MaximumSupplementaryColumnWidth = (nfloat)maximum;
        _split.MinimumSupplementaryColumnWidth = (nfloat)minimum;
        _split.PreferredSupplementaryColumnWidth = (nfloat)preferred;
        _split.MaximumSupplementaryColumnWidth = (nfloat)maximum;
    }

#if DEBUG
    /// <summary>Debug: the split columns, the sidebar slot and the idle glyph.</summary>
    internal string DescribeColumns()
        => $"columns={_columnMode} split={_split.DisplayMode} primary={_split.PrimaryColumnWidth:0} supplementary={_split.SupplementaryColumnWidth:0} " +
           $"sidebar={(_sidebarContent is { } slot ? slot.Content.GetType().Name : "menu")} idle={ReaderFrame.IdleGlyph}";
#endif
}
