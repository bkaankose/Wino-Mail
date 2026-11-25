namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>
/// The Wino Intelligence brand gradient (macOS WinoIntelligenceBrand, Windows WinoIllustrationBrandGradient): four stops
/// from the top-left to the bottom-right, one palette per scheme. Main thread only.
/// </summary>
public static class IntelligencePaint
{
    private static readonly double[] Locations = [0, 0.42, 0.66, 1];
    private static readonly uint[] Light = [0x075FCC, 0x58BFFF, 0x7C5CFC, 0x1685EA];
    private static readonly uint[] Dark = [0x5FA8FF, 0x72D1FF, 0xB59BFF, 0x3298F4];

    public static bool IsDark => Adw.StyleManager.GetDefault().GetDark();

    /// <summary>The flat brand colour (WinoIllustrationBrandSolidBrush).</summary>
    public static (double R, double G, double B) Solid(bool dark) => Rgb(dark ? 0x7FBAFFu : 0x3B84E8u);

    public static (double R, double G, double B) Rgb(uint hex) => (((hex >> 16) & 0xFF) / 255.0, ((hex >> 8) & 0xFF) / 255.0, (hex & 0xFF) / 255.0);

    /// <summary>Sets the gradient over the box as the source of <paramref name="cr"/>.</summary>
    public static void SetGradient(Cairo.Context cr, double x, double y, double width, double height, bool dark, double alpha = 1)
    {
        var gradient = new Cairo.LinearGradient(x, y, x + width, y + height);
        var stops = dark ? Dark : Light;
        for (var index = 0; index < stops.Length; index++)
        {
            var (r, g, b) = Rgb(stops[index]);
            gradient.AddColorStopRgba(Locations[index], r, g, b, alpha);
        }
        cr.SetSource(gradient);
    }

    /// <summary>The colour of the gradient at <paramref name="position"/> (0..1), for strokes that cannot take a pattern per segment.</summary>
    public static (double R, double G, double B) At(double position, bool dark)
    {
        var stops = dark ? Dark : Light;
        position = Math.Clamp(position, 0, 1);
        for (var index = 1; index < stops.Length; index++)
        {
            if (position > Locations[index]) continue;
            var t = (position - Locations[index - 1]) / (Locations[index] - Locations[index - 1]);
            var (r0, g0, b0) = Rgb(stops[index - 1]);
            var (r1, g1, b1) = Rgb(stops[index]);
            return (r0 + (r1 - r0) * t, g0 + (g1 - g0) * t, b0 + (b1 - b0) * t);
        }
        return Rgb(stops[^1]);
    }
}
