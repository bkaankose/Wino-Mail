using System;
using System.Drawing;

namespace Wino.Core.Misc
{
    public static class RandomFlatColorGenerator
    {
        public static Color Generate()
        {
            Random random = new();
            int hue = random.Next(0, 360);      // Full hue range
            int saturation = 70 + random.Next(30); // High saturation (70-100%)
            int lightness = 50 + random.Next(20);  // Bright colors (50-70%)

            return FromHsl(hue, saturation, lightness);
        }

        private static Color FromHsl(int h, int s, int l)
        {
            double hue = h / 360.0;
            double saturation = s / 100.0;
            double lightness = l / 100.0;

            // Conversion from HSL to RGB
            var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var x = chroma * (1 - Math.Abs((hue * 6) % 2 - 1));
            var m = lightness - chroma / 2;

            double r = 0, g = 0, b = 0;

            if (hue < 1.0 / 6.0) { r = chroma; g = x; b = 0; }
            else if (hue < 2.0 / 6.0) { r = x; g = chroma; b = 0; }
            else if (hue < 3.0 / 6.0) { r = 0; g = chroma; b = x; }
            else if (hue < 4.0 / 6.0) { r = 0; g = x; b = chroma; }
            else if (hue < 5.0 / 6.0) { r = x; g = 0; b = chroma; }
            else { r = chroma; g = 0; b = x; }

            return Color.FromArgb(
                (int)((r + m) * 255),
                (int)((g + m) * 255),
                (int)((b + m) * 255));
        }
    }
}
