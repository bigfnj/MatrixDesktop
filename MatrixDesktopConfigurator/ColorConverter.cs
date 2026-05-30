namespace MatrixDesktopConfigurator;

// HSL ↔ RGB conversion used by the color/palette/stripes import paths in
// ArgumentImporter. Extracted from ArgumentImporter.cs so the conversion is
// independently testable and ArgumentImporter no longer mixes color math with
// command-line parsing.
internal static class ColorConverter
{
    public static double[] HslToRgb(double h, double s, double l)
    {
        h = ((h % 1) + 1) % 1;
        s = Clamp01(s);
        l = Clamp01(l);

        if (s == 0)
        {
            return [l, l, l];
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return
        [
            HueToRgb(p, q, h + 1.0 / 3.0),
            HueToRgb(p, q, h),
            HueToRgb(p, q, h - 1.0 / 3.0),
        ];
    }

    public static double Clamp01(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static double HueToRgb(double p, double q, double t)
    {
        t = ((t % 1) + 1) % 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
