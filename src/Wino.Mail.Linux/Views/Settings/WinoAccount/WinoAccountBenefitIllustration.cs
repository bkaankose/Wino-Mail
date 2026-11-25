using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.WinoAccount;

/// <summary>
/// The four offer illustrations of the Windows page (macOS WinoAccountBenefitIllustrationView), drawn with Cairo from
/// the same 240x170 canvas shapes and scaled uniformly: device transfer, add-ons follow you, Wino Intelligence and
/// Unlimited Accounts. Colours come from the accent and the widget's foreground, so light and dark both work.
/// </summary>
internal sealed class WinoAccountBenefitIllustration : IDisposable
{
    private const double CanvasWidth = 240;
    private const double CanvasHeight = 170;

    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Box _self;
    private readonly IDisposable _styleChanged;
    private WinoAccountBenefitType? _kind;

    public WinoAccountBenefitIllustration()
    {
        WinoLiveObjects.Track(this);
        _self = new Box(this);
        var self = _self;
        _area.SetContentWidth(168);
        _area.SetContentHeight(119);
        _area.SetHalign(Gtk.Align.Center);
        _area.SetValign(Gtk.Align.Center);
        // A draw func is rooted until the area is destroyed: it reaches this object only through the box Dispose empties.
        _area.SetDrawFunc((area, cr, width, height) => self.Value?.Draw(area, cr, width, height));
        _styleChanged = GtkSignals.OnNotify(Adw.StyleManager.GetDefault(), "dark", () => self.Value?._area.QueueDraw());
    }

    public Gtk.Widget Root => _area;

    public WinoAccountBenefitType? Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            _area.QueueDraw();
        }
    }

    public void Dispose()
    {
        _self.Value = null;
        _styleChanged.Dispose();
    }

    private void Draw(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        if (_kind is not { } kind) return;
        area.GetColor(out var foreground);
        var accent = Adw.StyleManager.GetDefault().GetAccentColorRgba();
        var dark = Adw.StyleManager.GetDefault().GetDark();
        var palette = new Palette(
            (accent.Red, accent.Green, accent.Blue),
            (foreground.Red, foreground.Green, foreground.Blue),
            dark ? (1, 1, 1, 0.07) : (1, 1, 1, 1));
        var scale = Math.Min(width / CanvasWidth, height / CanvasHeight);
        cr.Save();
        cr.Translate((width - CanvasWidth * scale) / 2, (height - CanvasHeight * scale) / 2);
        cr.Scale(scale, scale);
        switch (kind)
        {
            case WinoAccountBenefitType.DeviceTransfer: DrawDeviceTransfer(cr, palette); break;
            case WinoAccountBenefitType.Entitlements: DrawEntitlements(cr, palette); break;
            case WinoAccountBenefitType.Intelligence: DrawIntelligence(cr, palette); break;
            case WinoAccountBenefitType.UnlimitedAccounts: DrawUnlimitedAccounts(cr, palette); break;
        }
        cr.Restore();
    }

    private readonly record struct Palette((double R, double G, double B) Accent, (double R, double G, double B) Foreground, (double R, double G, double B, double A) Card)
    {
        public Color AccentAt(double opacity = 1) => new(Accent.R, Accent.G, Accent.B, opacity);
        public Color Tertiary(double opacity) => new(Foreground.R, Foreground.G, Foreground.B, 0.45 * opacity);
        public Color Divider => new(Foreground.R, Foreground.G, Foreground.B, 0.16);
        public Color CardFill => new(Card.R, Card.G, Card.B, Card.A);
        public static Color White => new(1, 1, 1, 1);
    }

    private readonly record struct Color(double R, double G, double B, double A)
    {
        public void Use(Cairo.Context cr) => cr.SetSourceRgba(R, G, B, A);
    }

    private static void DrawDeviceTransfer(Cairo.Context cr, Palette p)
    {
        Rect(cr, 6, 34, 80, 98, 9, p.CardFill, p.Divider);
        Rect(cr, 16, 46, 46, 7, 3.5, p.Tertiary(0.5));
        Rect(cr, 16, 61, 60, 6, 3, p.Tertiary(0.28));
        Rect(cr, 16, 75, 60, 6, 3, p.Tertiary(0.28));
        Rect(cr, 16, 89, 40, 6, 3, p.Tertiary(0.28));
        Rect(cr, 16, 107, 34, 14, 7, p.AccentAt(0.8));

        Rect(cr, 154, 34, 80, 98, 9, p.CardFill, p.AccentAt());
        Rect(cr, 164, 46, 46, 7, 3.5, p.AccentAt(0.55));
        Rect(cr, 164, 61, 60, 6, 3, p.Tertiary(0.28));
        Rect(cr, 164, 75, 60, 6, 3, p.Tertiary(0.28));
        Rect(cr, 164, 89, 40, 6, 3, p.Tertiary(0.28));
        Rect(cr, 164, 107, 34, 14, 7, p.AccentAt(0.8));

        StrokePath(cr, "M88,100 H152", p.AccentAt(), 2, round: false, dash: [2, 6]);
        Ellipse(cr, 101, 81, 38, 38, p.AccentAt());
        StrokePath(cr, "M112,100 L118,106 L128,94", Palette.White, 2.4);
    }

    private static void DrawEntitlements(Cairo.Context cr, Palette p)
    {
        StrokePath(cr, "M43,92 V72 H120 V56 M120,72 H197 V92", p.Divider, 1.5, round: false);
        Rect(cr, 14, 92, 58, 48, 7, p.CardFill, p.Divider);
        Rect(cr, 91, 92, 58, 48, 7, p.CardFill, p.Divider);
        Rect(cr, 168, 92, 58, 48, 7, p.CardFill, p.Divider);

        const string shield = "M120,22 L152,36 V56 C152,73 139.2,86.4 120,94 C100.8,86.4 88,73 88,56 V36 Z";
        FillPath(cr, shield, p.AccentAt(0.16));
        StrokePath(cr, shield, p.AccentAt(), 1.8);
        StrokePath(cr, "M108,55.5 L116,63.5 L133,46.5", p.AccentAt(), 2.6);
        StrokePath(cr, "M35,112 L40,117 L50,107 M112,112 L117,117 L127,107 M189,112 L194,117 L204,107", p.AccentAt(), 2);
    }

    private static void DrawIntelligence(Cairo.Context cr, Palette p)
    {
        Rect(cr, 34, 22, 172, 126, 10, p.CardFill, p.Divider);
        Rect(cr, 50, 42, 66, 8, 4, p.Tertiary(0.45));
        Rect(cr, 50, 62, 140, 7, 3.5, p.Tertiary(0.25));
        Rect(cr, 50, 78, 140, 7, 3.5, p.Tertiary(0.25));
        Rect(cr, 50, 94, 96, 7, 3.5, p.Tertiary(0.25));
        Rect(cr, 44, 112, 152, 26, 7, p.AccentAt(0.15));
        Rect(cr, 56, 121, 104, 8, 4, p.AccentAt(0.7));
        FillPath(cr, "M183.3,16 L188.5,32 L204.5,37.2 L188.5,42.4 L183.3,58.4 L178.1,42.4 L162.1,37.2 L178.1,32 Z", p.AccentAt());
        FillPath(cr, "M206.9,44 L209.6,52 L217.6,54.7 L209.6,57.4 L206.9,65.4 L204.2,57.4 L196.2,54.7 L204.2,52 Z", p.AccentAt(0.65));
    }

    private static void DrawUnlimitedAccounts(Cairo.Context cr, Palette p)
    {
        Rect(cr, 26, 30, 128, 34, 8, p.CardFill, p.Divider);
        Ellipse(cr, 36, 37, 20, 20, p.AccentAt(0.55));
        Rect(cr, 64, 41, 62, 7, 3.5, p.Tertiary(0.4));
        Rect(cr, 64, 52, 40, 5, 2.5, p.Tertiary(0.22));

        Rect(cr, 40, 72, 128, 34, 8, p.CardFill, p.Divider);
        Ellipse(cr, 50, 79, 20, 20, p.AccentAt(0.4));
        Rect(cr, 78, 83, 62, 7, 3.5, p.Tertiary(0.4));
        Rect(cr, 78, 94, 40, 5, 2.5, p.Tertiary(0.22));

        Rect(cr, 54, 114, 128, 34, 8, p.CardFill, p.AccentAt());
        Ellipse(cr, 64, 121, 20, 20, p.AccentAt(0.28));
        Rect(cr, 92, 125, 62, 7, 3.5, p.Tertiary(0.4));
        Rect(cr, 92, 136, 40, 5, 2.5, p.Tertiary(0.22));

        // The right lobe runs the other way round, so the strokes cross and read as an infinity mark.
        StrokePath(cr, "M196,47 C186,31 176,27 168,31 C158,36 158,58 168,63 C176,67 186,63 196,47 C206,63 216,67 224,63 C234,58 234,36 224,31 C216,27 206,31 196,47 Z", p.AccentAt(), 3);
    }

    private static void Rect(Cairo.Context cr, double x, double y, double w, double h, double r, Color fill, Color? stroke = null, double strokeWidth = 1.5)
    {
        cr.NewSubPath();
        cr.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
        cr.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
        cr.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
        cr.Arc(x + r, y + r, r, Math.PI, 3 * Math.PI / 2);
        cr.ClosePath();
        fill.Use(cr);
        if (stroke is not { } line)
        {
            cr.Fill();
            return;
        }
        cr.FillPreserve();
        line.Use(cr);
        cr.LineWidth = strokeWidth;
        cr.Stroke();
    }

    private static void Ellipse(Cairo.Context cr, double x, double y, double w, double h, Color fill)
    {
        cr.Save();
        cr.Translate(x + w / 2, y + h / 2);
        cr.Scale(w / 2, h / 2);
        cr.NewSubPath();
        cr.Arc(0, 0, 1, 0, 2 * Math.PI);
        cr.Restore();
        fill.Use(cr);
        cr.Fill();
    }

    private static void StrokePath(Cairo.Context cr, string data, Color stroke, double width, bool round = true, double[]? dash = null)
    {
        Path(cr, data);
        cr.LineWidth = width;
        cr.LineJoin = round ? Cairo.LineJoin.Round : Cairo.LineJoin.Miter;
        if (dash is not null) cr.SetDash(dash, 0);
        stroke.Use(cr);
        cr.Stroke();
        if (dash is not null) cr.SetDash([], 0);
    }

    private static void FillPath(Cairo.Context cr, string data, Color fill)
    {
        Path(cr, data);
        fill.Use(cr);
        cr.Fill();
    }

    /// <summary>The absolute SVG/XAML path subset these drawings use: M, L, H, V, C and Z.</summary>
    private static void Path(Cairo.Context cr, string data)
    {
        var tokens = Tokenize(data);
        var index = 0;
        var command = 'M';
        double x = 0, y = 0;
        double Next() => double.Parse(tokens[index++], CultureInfo.InvariantCulture);
        bool HasNumber() => index < tokens.Count && !char.IsLetter(tokens[index][0]);

        cr.NewPath();
        while (index < tokens.Count)
        {
            if (char.IsLetter(tokens[index][0])) command = tokens[index++][0];
            switch (command)
            {
                case 'M':
                    x = Next();
                    y = Next();
                    cr.MoveTo(x, y);
                    command = 'L';
                    break;
                case 'L':
                    x = Next();
                    y = Next();
                    cr.LineTo(x, y);
                    break;
                case 'H':
                    x = Next();
                    cr.LineTo(x, y);
                    break;
                case 'V':
                    y = Next();
                    cr.LineTo(x, y);
                    break;
                case 'C':
                    var x1 = Next();
                    var y1 = Next();
                    var x2 = Next();
                    var y2 = Next();
                    x = Next();
                    y = Next();
                    cr.CurveTo(x1, y1, x2, y2, x, y);
                    break;
                case 'Z':
                    cr.ClosePath();
                    break;
                default:
                    throw new FormatException($"Unsupported path command '{command}'.");
            }
            if (command == 'Z' && HasNumber()) command = 'L';
        }
    }

    private static List<string> Tokenize(string data)
    {
        var tokens = new List<string>();
        var number = new System.Text.StringBuilder();
        void Flush()
        {
            if (number.Length == 0) return;
            tokens.Add(number.ToString());
            number.Clear();
        }
        foreach (var character in data)
        {
            if (char.IsLetter(character))
            {
                Flush();
                tokens.Add(character.ToString());
            }
            else if (character is ',' or ' ') Flush();
            else if (character == '-' && number.Length > 0)
            {
                Flush();
                number.Append(character);
            }
            else number.Append(character);
        }
        Flush();
        return tokens;
    }

    private sealed class Box(WinoAccountBenefitIllustration value)
    {
        public WinoAccountBenefitIllustration? Value { get; set; } = value;
    }
}
