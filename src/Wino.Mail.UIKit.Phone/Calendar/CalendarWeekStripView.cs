using System.Globalization;
using CoreGraphics;
using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The Day view's week strip (iPhone calendar, option B of 15 Oct 2026): the seven days of the shown day's week, the
/// weekday over the day number, the shown day in an accent circle and today in the accent colour. A tap picks the day; a
/// horizontal swipe moves a week. Each day is its own 44pt-wide accessibility button.
/// </summary>
public sealed class CalendarWeekStripView : UIView
{
    public const double StripHeight = 52;

    private const double Circle = 32;

    private readonly UIButton[] _days = new UIButton[7];
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Today);
    private DayOfWeek _firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public event EventHandler<DateOnly>? DateClicked;

    public CalendarWeekStripView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        var row = WinoLayout.HStack(0);
        row.Distribution = UIStackViewDistribution.FillEqually;
        for (int index = 0; index < _days.Length; index++)
        {
            var button = new UIButton(UIButtonType.Custom) { TranslatesAutoresizingMaskIntoConstraints = false };
            int column = index;
            button.PrimaryActionTriggered += (_, _) => DateClicked?.Invoke(this, WeekStart.AddDays(column));
            _days[index] = button;
            row.AddArrangedSubview(button);
        }
        WinoLayout.Fill(row, this);
        HeightAnchor.ConstraintEqualTo((nfloat)StripHeight).Active = true;
        AddGestureRecognizer(new UISwipeGestureRecognizer(() => DateClicked?.Invoke(this, _selected.AddDays(7))) { Direction = UISwipeGestureRecognizerDirection.Left });
        AddGestureRecognizer(new UISwipeGestureRecognizer(() => DateClicked?.Invoke(this, _selected.AddDays(-7))) { Direction = UISwipeGestureRecognizerDirection.Right });
        WinoTraits.OnAppearanceChanged(this, static view => view.Refresh());
        Refresh();
    }

    /// <summary>The day the surface shows; the strip shows its week.</summary>
    public DateOnly SelectedDate
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Refresh(); } }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set { if (_firstDayOfWeek != value) { _firstDayOfWeek = value; Refresh(); } }
    }

    private DateOnly WeekStart => _selected.AddDays(-(((int)_selected.DayOfWeek - (int)_firstDayOfWeek + 7) % 7));

    private void Refresh()
    {
        var culture = CultureInfo.CurrentCulture;
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (int index = 0; index < _days.Length; index++)
        {
            var date = WeekStart.AddDays(index);
            bool selected = date == _selected;
            bool isToday = date == today;
            var configuration = UIButtonConfiguration.PlainButtonConfiguration;
            configuration.Image = DayImage(culture.DateTimeFormat.AbbreviatedDayNames[(int)date.DayOfWeek], date.Day.ToString(culture), selected, isToday);
            configuration.ContentInsets = NSDirectionalEdgeInsets.Zero;
            _days[index].Configuration = configuration;
            _days[index].AccessibilityLabel = date.ToDateTime(TimeOnly.MinValue).ToString("D", culture);
            _days[index].AccessibilityTraits = selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }

    /// <summary>The weekday over the number, drawn as one image so the button keeps a single hit area.</summary>
    private UIImage DayImage(string weekday, string number, bool selected, bool isToday)
    {
        var size = new CGSize(Circle + 8, StripHeight - 4);
        var format = new UIGraphicsImageRendererFormat { Scale = UIScreen.MainScreen.Scale };
        using var renderer = new UIGraphicsImageRenderer(size, format);
        var traits = TraitCollection;
        UIColor Resolve(UIColor color) => color.GetResolvedColor(traits);
        var image = renderer.CreateImage(_ =>
        {
            var nameFont = WinoStyle.Fixed(11, UIFontWeight.Semibold);
            var nameText = new Foundation.NSAttributedString(weekday.ToUpper(CultureInfo.CurrentCulture), new UIStringAttributes
            {
                Font = nameFont,
                ForegroundColor = Resolve(isToday && !selected ? WinoStyle.Accent : WinoStyle.SecondaryText)
            });
            var nameSize = nameText.Size;
            nameText.DrawString(new CGPoint((size.Width - nameSize.Width) / 2, 0));
            var circle = new CGRect((size.Width - Circle) / 2, size.Height - Circle, Circle, Circle);
            if (selected)
            {
                Resolve(WinoStyle.Accent).SetFill();
                UIBezierPath.FromOval(circle).Fill();
            }
            var numberText = new Foundation.NSAttributedString(number, new UIStringAttributes
            {
                Font = WinoStyle.Fixed(16, UIFontWeight.Semibold),
                ForegroundColor = Resolve(selected ? UIColor.White : isToday ? WinoStyle.Accent : WinoStyle.PrimaryText)
            });
            var numberSize = numberText.Size;
            numberText.DrawString(new CGPoint(circle.X + (Circle - numberSize.Width) / 2, circle.Y + (Circle - numberSize.Height) / 2));
        });
        return image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }
}
