using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// The Windows CalendarTitleBarContent with native AppKit controls, laid out like Apple Calendar:
/// previous/next as a momentary segmented control, the visible range text at 18pt, a Today button and
/// the WinoCalendarTypeSelectorControl as a select-one segmented control (Day / Week / Work week / Month).
/// AppKit owns pressing, keyboard focus, selection drawing and accessibility. Margin 4,0,7,8 like Windows.
/// The page wires the events to the calendar shell commands.
/// </summary>
internal sealed class CalendarToolbarView : NSView
{
    private static readonly CalendarDisplayType[] DisplayTypes =
        [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month];

    private readonly NSTextField _rangeText;
    private readonly NSSegmentedControl _navigation;
    private readonly NSSegmentedControl _displayType;

    public CalendarToolbarView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;

        // No translation keys exist for the navigation button names (Mac-only accessibility labels).
        _navigation = new NSSegmentedControl
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TrackingMode = NSSegmentSwitchTracking.Momentary,
            SegmentCount = 2,
            ControlSize = NSControlSize.Large
        };
        _navigation.SetImage(WinoIcons.Image(WinoIconGlyph.ArrowLeft, 12, null, "Previous"), 0);
        _navigation.SetImage(WinoIcons.Image(WinoIconGlyph.ArrowRight, 12, null, "Next"), 1);
        _navigation.SetToolTip("Previous", 0);
        _navigation.SetToolTip("Next", 1);
        _navigation.SetWidth(40, 0);
        _navigation.SetWidth(40, 1);
        _navigation.Activated += (_, _) =>
        {
            if (_navigation.SelectedSegment == 0) PreviousRequested?.Invoke(this, EventArgs.Empty);
            else if (_navigation.SelectedSegment == 1) NextRequested?.Invoke(this, EventArgs.Empty);
        };

        _rangeText = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold));
        _rangeText.SetContentCompressionResistancePriority(260, NSLayoutConstraintOrientation.Horizontal);
        _rangeText.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);

        var today = new NSButton
        {
            Title = Translator.Today,
            BezelStyle = NSBezelStyle.Push,
            ControlSize = NSControlSize.Large,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        today.Activated += (_, _) => TodayRequested?.Invoke(this, EventArgs.Empty);

        _displayType = new NSSegmentedControl
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TrackingMode = NSSegmentSwitchTracking.SelectOne,
            SegmentCount = DisplayTypes.Length,
            ControlSize = NSControlSize.Large
        };
        string[] titles = [Translator.CalendarTypeSelector_Day, Translator.CalendarTypeSelector_Week, Translator.CalendarTypeSelector_WorkWeek, Translator.CalendarTypeSelector_Month];
        for (int index = 0; index < titles.Length; index++) _displayType.SetLabel(titles[index], index);
        _displayType.Activated += (_, _) =>
        {
            var index = (int)_displayType.SelectedSegment;
            if (index >= 0 && index < DisplayTypes.Length) DisplayTypeRequested?.Invoke(this, DisplayTypes[index]);
        };

        _navigation.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        _displayType.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(16, _navigation, _rangeText, WinoLayout.Spacer(), today, _displayType);
        row.Alignment = NSLayoutAttribute.CenterY;
        row.EdgeInsets = new NSEdgeInsets(0, 4, 8, 7);
        WinoLayout.Fill(row, this);
    }

    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? TodayRequested;
    public event EventHandler<CalendarDisplayType>? DisplayTypeRequested;

    public string RangeText
    {
        get => _rangeText.StringValue;
        set => _rangeText.StringValue = value ?? string.Empty;
    }

    /// <summary>The display type the page reports; other values (such as Year) leave no segment selected.</summary>
    public CalendarDisplayType SelectedType
    {
        set => _displayType.SelectedSegment = Array.IndexOf(DisplayTypes, value);
    }
}
