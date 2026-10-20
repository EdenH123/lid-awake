using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace LidAwake;

// Launch screens for the home-screen app, as the Mac's Splash.swift draws them: iPhones show one
// while the page loads, but only if it matches the phone's exact pixel size, so each supported size
// is drawn here (a closed laptop on black, where the page's opening animation begins) and kept in
// memory after the first request.
public static class Splash
{
    // iPhone screens in points and their scale (portrait), newest first: the Mac's list.
    public static readonly (int W, int H, int Scale)[] Sizes =
    {
        (440, 956, 3), (402, 874, 3), (420, 912, 3), (430, 932, 3), (393, 852, 3), (428, 926, 3),
        (390, 844, 3), (414, 896, 3), (375, 812, 3), (414, 896, 2), (375, 667, 2),
    };

    // The <link> tags that replace <!--SPLASH--> in the page head, one per size, as on the Mac.
    public static readonly string LinkTags = string.Join("\n", Sizes.Select(s =>
        $"<link rel=\"apple-touch-startup-image\" media=\"(device-width: {s.W}px) and (device-height: {s.H}px) and "
        + $"(-webkit-device-pixel-ratio: {s.Scale}) and (orientation: portrait)\" href=\"{PathOf(s)}\">"));

    static readonly Dictionary<string, byte[]> cache = new();
    static readonly object gate = new();

    // "/splash/1206x2622.png" → the PNG, for listed sizes only, spelled exactly as the link tags
    // spell them (null otherwise: 404). Anything else, "/splash/01206x2622.png" too, is not drawn,
    // so the cache holds at most one picture per listed size.
    public static byte[]? Png(string path)
    {
        var (w, h, scale) = Sizes.FirstOrDefault(s => path == PathOf(s));
        if (scale == 0) return null;
        lock (gate)
        {
            if (cache.TryGetValue(path, out var cached)) return cached;
            var png = Draw(w * scale, h * scale, scale);
            cache[path] = png;
            return png;
        }
    }

    // How many pictures are kept (for the tests).
    internal static int Cached
    {
        get { lock (gate) return cache.Count; }
    }

    static string PathOf((int W, int H, int Scale) s) =>
        string.Create(CultureInfo.InvariantCulture, $"/splash/{s.W * s.Scale}x{s.H * s.Scale}.png");

    // A closed laptop on black, exactly where the page's opening animation starts (#intro in the
    // page: the computer is min(58% of the width, 300 pt) wide, centred), so the lid opens
    // seamlessly once the page has loaded. The Mac's drawing, in top-down coordinates.
    static byte[] Draw(int width, int height, int scale)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);
            float w = width, h = height;
            float macW = Math.Min(w * 0.58f, 300f * scale), macH = macW * 170 / 220;
            float baseH = macH * 0.07f;
            // The base's lower edge is macH / 2 below the middle.
            var bottomPart = new RectangleF((w - macW) / 2, h / 2 + macH / 2 - baseH, macW, baseH);
            using (var shape = Rounded(bottomPart, baseH * 0.6f))
            // Light at the top, darker at the bottom. A pixel taller than the shape on both sides, so
            // GDI+ never wraps the gradient's end colour onto its first row.
            using (var fill = new LinearGradientBrush(RectangleF.Inflate(bottomPart, 0, 1), Gray(0.82), Gray(0.56), LinearGradientMode.Vertical))
                g.FillPath(fill, shape);
            // The closed lid: a thin dark edge resting on the base.
            float lidH = Math.Max(2, baseH * 0.25f);
            var lid = new RectangleF(bottomPart.X + macW * 0.045f, bottomPart.Y - lidH, macW * 0.91f, lidH);
            using (var shape = Rounded(lid, 2))
            using (var fill = new SolidBrush(Gray(0.23)))
                g.FillPath(fill, shape);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    static Color Gray(double white)
    {
        int v = (int)Math.Round(white * 255);
        return Color.FromArgb(255, v, v, v);
    }

    // A rounded rectangle. A radius over half the height or width is cut to it, as NSBezierPath does.
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var path = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
