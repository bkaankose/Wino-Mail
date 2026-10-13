using System.ComponentModel;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Calendar;

/// <summary>
/// The iPad sidebar content for Calendar mode (the Mac calendar pane: ShellDatePickerCell and ShellCalendarGroupCell):
/// New event, the month picker with the visible range highlighted (its toggle collapses it) and the accounts' calendars
/// with colour toggles. The calendar page puts it in the pad shell sidebar's content slot, above the shell's mode
/// switcher and over the sidebar's own backdrop.
/// </summary>
public sealed class PadCalendarSidebarViewController : UIViewController
{
    private readonly CalendarAppShellViewModel _shell;
    private readonly Action<DateOnly> _dateClicked;
    private readonly Action _newEvent;
    private readonly WinoMiniCalendarView _calendar = new();
    private CalendarListView? _list;
    private bool _expanded = true;

    public PadCalendarSidebarViewController(CalendarAppShellViewModel shell, Action<DateOnly> dateClicked, Action newEvent)
    {
        _shell = shell;
        _dateClicked = dateClicked;
        _newEvent = newEvent;
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };

        var newEvent = NewEventButton();
        _calendar.DateClicked += (_, date) => _dateClicked(date);
        _calendar.ToggleClicked += (_, _) => SetExpanded(!_expanded);
        SetExpanded(true);
        _list = new CalendarListView(_shell.AccountCalendarStateService);

        var column = WinoLayout.VStack(8, newEvent, _calendar, Header(Translator.IOS_Calendar_Calendars), _list);
        column.Alignment = UIStackViewAlignment.Fill;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true, BackgroundColor = UIColor.Clear };
        scroll.AddSubview(column);
        root.AddSubview(scroll);

        var guide = root.SafeAreaLayoutGuide;
        var content = scroll.ContentLayoutGuide;
        var frame = scroll.FrameLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            column.TopAnchor.ConstraintEqualTo(content.TopAnchor, 4),
            column.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -8),
            column.LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor, 8),
            column.TrailingAnchor.ConstraintEqualTo(frame.TrailingAnchor, -8)
        ]);
        View = root;
    }

    /// <summary>Highlights the visible range in the month picker.</summary>
    public void SetRange(VisibleDateRange? range)
    {
        if (range is null) { _calendar.SelectedDates = new HashSet<DateOnly>(); return; }
        _calendar.SelectedDates = new HashSet<DateOnly>(range.Dates);
        _calendar.DisplayMonth = range.AnchorDate;
    }

    public void SetFirstDayOfWeek(DayOfWeek day) => _calendar.FirstDayOfWeek = day;

    /// <summary>Calendar rows on screen (debug).</summary>
    public int CalendarRowCount => _list?.CalendarRowCount ?? 0;

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _calendar.Collapsed = !expanded;
        _calendar.SetToggle(expanded ? WinoIconGlyph.PanelLeftContract : WinoIconGlyph.PanelLeftExpand, Translator.KeyboardShortcuts_ModeCalendar);
    }

    private UIButton NewEventButton()
    {
        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.Title = Translator.CalendarEventCompose_NewEventButton;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.Add, 16, UIColor.White);
        configuration.ImagePadding = 8;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Large;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintEqualTo(44).Active = true;
        button.PrimaryActionTriggered += (_, _) => _newEvent();
        return button;
    }

    private static UIView Header(string title)
    {
        var label = WinoStyle.Label(title, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        label.AccessibilityTraits = UIAccessibilityTrait.Header;
        return WinoLayout.Insets(WinoLayout.HStack(0, label), 8, 8, 0, 8);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _list?.Dispose();
        base.Dispose(disposing);
    }
}
