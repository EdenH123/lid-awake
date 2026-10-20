using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LidAwake;

// The tray icon's pictures (TrayIconState), drawn here in one colour like the Mac's menu bar
// symbols: white on a dark taskbar, near-black on a light one. Each is drawn on a 16-unit grid at
// exactly the size Windows shows tray icons at (16, 20, 24 or 32 pixels for 100 to 200 % scaling),
// so it is never stretched.
static class TrayIcons
{
    public static readonly int[] Sizes = { 16, 20, 24, 32 };

    static readonly Color OnDark = Color.White;
    static readonly Color OnLight = Color.FromArgb(28, 28, 28);

    // The drawn size for Windows' small-icon size (SystemInformation.SmallIconSize): the first that
    // is at least as big, at most 32.
    public static int SizeFor(int smallIcon) => Sizes.FirstOrDefault(s => s >= smallIcon, Sizes[^1]);

    // The taskbar is light (Settings → Personalization → Colors → "Choose your default Windows
    // mode": Light). Not set, or unreadable: dark, the Windows default.
    public static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch (Exception) { return false; }
    }

    // The picture, size × size, transparent around the symbol.
    public static Bitmap Draw(TrayIconState state, int size, bool lightTaskbar)
    {
        var color = lightTaskbar ? OnLight : OnDark;
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        switch (state)
        {
            case TrayIconState.Off: Moon(bmp, color); break;
            case TrayIconState.On: Cup(bmp, color); break;
            case TrayIconState.Viewed: Eye(bmp, color); break;
            case TrayIconState.Controlled: Hand(bmp, color); break;
            case TrayIconState.Camera: VideoCamera(bmp, color); break;
        }
        return bmp;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(IntPtr handle);

    // An Icon that owns its own handle (the one GetHicon made is copied, then destroyed).
    public static Icon Make(TrayIconState state, int size, bool lightTaskbar)
    {
        using var bmp = Draw(state, size, lightTaskbar);
        var handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    // Lid Awake off ("moon.zzz"): a crescent and a small z.
    static void Moon(Bitmap bmp, Color color)
    {
        Paint(bmp, color, (g, ink, _) => g.FillEllipse(ink, 1f, 3.5f, 11.5f, 11.5f));
        Cut(bmp, (g, all) => g.FillEllipse(all, 5f, 1f, 10.5f, 10.5f));
        Paint(bmp, color, (g, _, line) =>
        {
            line.Width = 1.25f;
            g.DrawLines(line, new[] { new PointF(11f, 1.25f), new PointF(14.75f, 1.25f), new PointF(11f, 5.25f), new PointF(14.75f, 5.25f) });
        });
    }

    // Lid Awake on ("cup.and.saucer.fill"): a cup with a handle, on a saucer.
    static void Cup(Bitmap bmp, Color color) => Paint(bmp, color, (g, ink, line) =>
    {
        using (var body = new GraphicsPath())
        {
            body.AddLine(2f, 4f, 11.5f, 4f);
            body.AddLine(11.5f, 4f, 11.5f, 8f);
            body.AddArc(2f, 3.5f, 9.5f, 9f, 0f, 180f);
            body.CloseFigure();
            g.FillPath(ink, body);
        }
        g.DrawArc(line, 9.5f, 5.25f, 4.5f, 4.25f, -90f, 180f);
        g.DrawLine(line, 1.25f, 14.25f, 14.75f, 14.25f);
    });

    // The screen is watched ("eye.fill"): a filled eye with a ring around the pupil.
    static void Eye(Bitmap bmp, Color color)
    {
        Paint(bmp, color, (g, ink, _) =>
        {
            using var lids = new GraphicsPath();
            lids.AddBezier(0.75f, 8f, 4f, 2f, 12f, 2f, 15.25f, 8f);
            lids.AddBezier(15.25f, 8f, 12f, 14f, 4f, 14f, 0.75f, 8f);
            lids.CloseFigure();
            g.FillPath(ink, lids);
        });
        Cut(bmp, (g, all) => g.FillEllipse(all, 4.75f, 4.75f, 6.5f, 6.5f));
        Paint(bmp, color, (g, ink, _) => g.FillEllipse(ink, 6.25f, 6.25f, 3.5f, 3.5f));
    }

    // The phone clicks or types ("hand.point.up.left.fill"): a hand pointing up. The pieces overlap
    // and have the same colour, so they read as one shape.
    static void Hand(Bitmap bmp, Color color) => Paint(bmp, color, (g, ink, _) =>
    {
        Fill(g, ink, 5.5f, 0.75f, 3.25f, 9.5f, 1.6f);   // the pointing finger
        Fill(g, ink, 8.25f, 5.75f, 2.75f, 5f, 1.35f);   // the curled fingers
        Fill(g, ink, 10.5f, 6.5f, 2.75f, 4.75f, 1.35f);
        Fill(g, ink, 4f, 8f, 9.25f, 7.25f, 2.75f);      // the palm
        Fill(g, ink, 1.75f, 8.25f, 3.5f, 5.5f, 1.75f);  // the thumb
    });

    // The camera is watched ("video.fill"): a camera body and its lens.
    static void VideoCamera(Bitmap bmp, Color color) => Paint(bmp, color, (g, ink, _) =>
    {
        Fill(g, ink, 0.75f, 3.75f, 10.25f, 8.5f, 2f);
        g.FillPolygon(ink, new[] { new PointF(11.75f, 7f), new PointF(15.25f, 4.5f), new PointF(15.25f, 11.5f), new PointF(11.75f, 9f) });
    });

    // Draws on the 16-unit grid, smoothed, with a brush and a round 1.5-unit pen in `color`.
    static void Paint(Bitmap bmp, Color color, Action<Graphics, Brush, Pen> draw)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(bmp.Width / 16f, bmp.Height / 16f);
        using var ink = new SolidBrush(color);
        using var line = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        draw(g, ink, line);
    }

    // Makes what `shape` covers transparent again (the crescent's bite, the eye's ring), with smooth
    // edges: the shape is drawn on its own layer and taken out of each pixel's opacity.
    static void Cut(Bitmap bmp, Action<Graphics, Brush> shape)
    {
        using var mask = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Paint(mask, Color.White, (g, all, _) => shape(g, all));
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int covered = mask.GetPixel(x, y).A;
                if (covered == 0) continue;
                var c = bmp.GetPixel(x, y);
                bmp.SetPixel(x, y, Color.FromArgb(c.A * (255 - covered) / 255, c.R, c.G, c.B));
            }
    }

    static void Fill(Graphics g, Brush ink, float x, float y, float w, float h, float r)
    {
        using var p = new GraphicsPath();
        float d = Math.Min(2 * r, Math.Min(w, h));
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        g.FillPath(ink, p);
    }
}
