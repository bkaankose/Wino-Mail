using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The "Pick a calendar" list (Windows calendar picker dialog): each account is a section header that cannot be picked or
/// collapsed, followed by its calendars with a small colour dot. Read-only calendars stay listed with a supporting line.
/// </summary>
internal sealed class CalendarPickerAdapter : BaseAdapter
{
    private const int HeaderType = 0;
    private const int CalendarType = 1;

    private readonly Context _context;
    private readonly List<(string? Header, AccountCalendar? Calendar)> _rows = [];

    public CalendarPickerAdapter(Context context, IReadOnlyList<(string Account, IReadOnlyList<AccountCalendar> Calendars)> groups)
    {
        _context = context;
        foreach (var (account, calendars) in groups)
        {
            _rows.Add((account, null));
            foreach (var calendar in calendars) _rows.Add((null, calendar));
        }
    }

    public AccountCalendar? CalendarAt(int position) => position >= 0 && position < _rows.Count ? _rows[position].Calendar : null;

    public override int Count => _rows.Count;

    public override Java.Lang.Object? GetItem(int position) => null;

    public override long GetItemId(int position) => position;

    public override int ViewTypeCount => 2;

    public override int GetItemViewType(int position) => _rows[position].Calendar is null ? HeaderType : CalendarType;

    public override bool AreAllItemsEnabled() => false;

    public override bool IsEnabled(int position) => _rows[position].Calendar is not null;

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        var (header, calendar) = _rows[position];
        return calendar is null ? HeaderView(convertView as TextView, header ?? string.Empty) : CalendarView(convertView as LinearLayout, calendar);
    }

    private TextView HeaderView(TextView? view, string title)
    {
        if (view is null)
        {
            view = WinoTokens.Style(new TextView(_context), WinoTextStyle.Caption);
            view.SetTextColor(WinoColors.SecondaryText(_context));
            view.SetMaxLines(1);
            view.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            view.SetPadding(WinoUnits.Dp(_context, 24), WinoUnits.Dp(_context, 16), WinoUnits.Dp(_context, 24), WinoUnits.Dp(_context, 4));
            AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(view, true);
        }
        view.Text = title;
        return view;
    }

    private LinearLayout CalendarView(LinearLayout? view, AccountCalendar calendar)
    {
        if (view is null)
        {
            view = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
            view.SetGravity(GravityFlags.CenterVertical);
            view.SetMinimumHeight(WinoUnits.Dp(_context, 48));
            view.SetPadding(WinoUnits.Dp(_context, 24), WinoUnits.Dp(_context, 6), WinoUnits.Dp(_context, 24), WinoUnits.Dp(_context, 6));
            int dot = WinoUnits.Dp(_context, 12);
            view.AddView(new View(_context), new LinearLayout.LayoutParams(dot, dot) { MarginEnd = WinoUnits.Dp(_context, 16) });
            var text = new LinearLayout(_context) { Orientation = Orientation.Vertical };
            var name = WinoTokens.Style(new TextView(_context), WinoTextStyle.Body);
            name.SetMaxLines(1);
            name.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            text.AddView(name);
            text.AddView(WinoTokens.Style(new TextView(_context) { Text = Translator.CalendarPicker_ReadOnly }, WinoTextStyle.Caption));
            view.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        }
        var fill = new GradientDrawable();
        fill.SetShape(ShapeType.Oval);
        fill.SetColor(WinoColors.FromHexString(calendar.BackgroundColorHex) ?? WinoColors.Accent(_context));
        view.GetChildAt(0)!.Background = fill;
        var lines = (LinearLayout)view.GetChildAt(1)!;
        ((TextView)lines.GetChildAt(0)!).Text = calendar.Name;
        lines.GetChildAt(1)!.Visibility = calendar.IsReadOnly ? ViewStates.Visible : ViewStates.Gone;
        view.Alpha = calendar.IsReadOnly ? 0.6f : 1f;
        view.ContentDescription = calendar.IsReadOnly ? $"{calendar.Name}, {Translator.CalendarPicker_ReadOnly}" : calendar.Name;
        return view;
    }
}
