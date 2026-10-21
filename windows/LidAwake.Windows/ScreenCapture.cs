using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LidAwake;

// A picture of the primary screen in memory: 32-bit pixels (blue, green, red, unused), top row
// first, `Stride` bytes from one row to the next. It belongs to the grabber that made it and
// is only valid until that grabber's next Grab.
public readonly record struct ScreenImage(IntPtr Pixels, int Width, int Height, int Stride);

// Takes pictures of the primary screen for the phone (the Mac uses ScreenCaptureKit). The mouse
// pointer is drawn in, as on the Mac. Grab may only be called from one thread at a time.
public interface IScreenGrabber : IDisposable
{
    string Kind { get; }

    // Waits up to `waitMs` for the screen to change, and returns it scaled down to at most
    // `maxWidth` pixels wide (even sizes, for the video encoder). null when nothing changed in
    // that time; with `force` the current picture comes back even then. Throws when capturing
    // stopped working (the caller opens a new grabber).
    ScreenImage? Grab(int maxWidth, int waitMs, bool force);
}

public static class ScreenSize
{
    // The screen scaled down (never up) to fit `maxWidth`, as even sizes: the H.264 encoder needs
    // them (FrameStream.swift does the same).
    public static (int Width, int Height) Fit(int width, int height, int maxWidth)
    {
        width = Math.Max(width, 1);
        int w = Math.Min(width, Math.Max(2, maxWidth));
        int h = (int)((long)height * w / width);
        return (Math.Max(2, w / 2 * 2), Math.Max(2, h / 2 * 2));
    }
}

// The grabbers there are, best first: DXGI Desktop Duplication (it hands over a frame only when
// the screen changed, from the graphics card), then GDI (a copy of the screen each time).
public sealed class ScreenGrabbers
{
    public Action<string>? Note { get; set; }

    // `level` 0 tries Desktop Duplication first; a higher level (after it kept failing) goes
    // straight to GDI. null when no way of capturing works.
    public IScreenGrabber? Open(int level)
    {
        if (level == 0)
        {
            try { return DxgiGrabber.Open(); }
            catch (Exception e) { Note?.Invoke("Desktop Duplication unavailable: " + e.Message); }
        }
        try { return new GdiGrabber(); }
        catch (Exception e) { Note?.Invoke("GDI screen capture unavailable: " + e.Message); }
        return null;
    }
}

// The screen through GDI: StretchBlt from the screen's device context into a picture of the
// wanted size (HALFTONE scaling), then the pointer. Works wherever there is a desktop.
public sealed class GdiGrabber : IScreenGrabber
{
    Dib? target;

    public GdiGrabber()
    {
        if (Gdi.GetSystemMetrics(Gdi.SM_CXSCREEN) <= 0 || Gdi.GetSystemMetrics(Gdi.SM_CYSCREEN) <= 0)
            throw new InvalidOperationException("No screen size.");
        var dc = Gdi.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException("No screen device context.");
        Gdi.ReleaseDC(IntPtr.Zero, dc);
    }

    public string Kind => "GDI";

    public ScreenImage? Grab(int maxWidth, int waitMs, bool force)
    {
        int sw = Gdi.GetSystemMetrics(Gdi.SM_CXSCREEN), sh = Gdi.GetSystemMetrics(Gdi.SM_CYSCREEN);
        if (sw <= 0 || sh <= 0) throw new InvalidOperationException("No screen size.");
        var (w, h) = ScreenSize.Fit(sw, sh, maxWidth);
        if (target == null || target.Width != w || target.Height != h)
        {
            target?.Dispose();
            target = null;
            target = new Dib(w, h);
        }
        var screen = Gdi.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("No screen device context.");
        try
        {
            Gdi.SetStretchBltMode(target.Dc, Gdi.HALFTONE);
            Gdi.SetBrushOrgEx(target.Dc, 0, 0, IntPtr.Zero);
            // CAPTUREBLT includes layered windows; without it as a second try.
            if (!Gdi.StretchBlt(target.Dc, 0, 0, w, h, screen, 0, 0, sw, sh, Gdi.SRCCOPY | Gdi.CAPTUREBLT)
                && !Gdi.StretchBlt(target.Dc, 0, 0, w, h, screen, 0, 0, sw, sh, Gdi.SRCCOPY))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StretchBlt failed.");
        }
        finally { Gdi.ReleaseDC(IntPtr.Zero, screen); }
        Gdi.DrawPointer(target, sw, sh, 0, 0);
        Gdi.GdiFlush();
        return target.Image;
    }

    public void Dispose()
    {
        target?.Dispose();
        target = null;
    }
}

// The primary screen through DXGI Desktop Duplication (IDXGIOutputDuplication). The desktop
// image arrives on the graphics card only when something changed; it is copied into memory,
// scaled with GDI and the pointer drawn in (duplication leaves the pointer out).
public sealed unsafe class DxgiGrabber : IScreenGrabber
{
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    const int DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91;

    IntPtr device, context, duplication, staging;
    int stagingWidth, stagingHeight;
    int screenWidth, screenHeight, originX, originY;
    Dib? full, target;
    bool haveImage;

    DxgiGrabber() { }

    public string Kind => "Desktop Duplication";

    public static DxgiGrabber Open()
    {
        var g = new DxgiGrabber();
        try
        {
            g.Init();
            return g;
        }
        catch
        {
            g.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CreateDXGIFactory1(Guid* iid, IntPtr* factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
                                        uint featureLevelCount, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* context);

    // The primary output (the one at desktop position 0,0), on whichever graphics card drives it.
    void Init()
    {
        var iid = IID_IDXGIFactory1;
        IntPtr factory;
        Com.Check(CreateDXGIFactory1(&iid, &factory), "CreateDXGIFactory1");
        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                // IDXGIFactory::EnumAdapters
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(factory, 7))(factory, a, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Com.Check(hr, "EnumAdapters");
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        // IDXGIAdapter::EnumOutputs
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(adapter, 7))(adapter, o, &output);
                        if (hr == DXGI_ERROR_NOT_FOUND) break;
                        Com.Check(hr, "EnumOutputs");
                        try
                        {
                            OutputDesc desc;
                            // IDXGIOutput::GetDesc
                            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)Com.Slot(output, 7))(output, &desc), "GetDesc");
                            if (desc.AttachedToDesktop != 0 && desc.Left == 0 && desc.Top == 0)
                            {
                                OpenOutput(adapter, output, desc);
                                return;
                            }
                        }
                        finally { Com.Release(ref output); }
                    }
                }
                finally { Com.Release(ref adapter); }
            }
        }
        finally { Com.Release(ref factory); }
        throw new InvalidOperationException("No primary display output.");
    }

    void OpenOutput(IntPtr adapter, IntPtr output, OutputDesc desc)
    {
        // A turned screen comes out of duplication unturned: GDI copes with it instead.
        if (desc.Rotation is 2 or 3 or 4) throw new NotSupportedException("The screen is rotated.");
        screenWidth = desc.Right - desc.Left;
        screenHeight = desc.Bottom - desc.Top;
        originX = desc.Left;
        originY = desc.Top;
        IntPtr dev, ctx;
        int level;
        // D3D_DRIVER_TYPE_UNKNOWN with an adapter; BGRA support; the default feature levels; SDK version 7.
        Com.Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice");
        device = dev;
        context = ctx;
        var iid = IID_IDXGIOutput1;
        IntPtr output1;
        Com.Check(Com.QueryInterface(output, &iid, &output1), "IDXGIOutput1");
        try
        {
            IntPtr dup;
            // IDXGIOutput1::DuplicateOutput
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Com.Slot(output1, 22))(output1, device, &dup), "DuplicateOutput");
            duplication = dup;
        }
        finally { Com.Release(ref output1); }
    }

    public ScreenImage? Grab(int maxWidth, int waitMs, bool force)
    {
        FrameInfo info;
        IntPtr resource;
        bool fresh = false;
        // IDXGIOutputDuplication::AcquireNextFrame
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)Com.Slot(duplication, 8))(
            duplication, (uint)Math.Max(0, waitMs), &info, &resource);
        if (hr != DXGI_ERROR_WAIT_TIMEOUT)
        {
            // DXGI_ERROR_ACCESS_LOST (the desktop switched, a full-screen app, a mode change) also
            // throws: the caller opens a new duplication.
            Com.Check(hr, "AcquireNextFrame");
            try
            {
                if (info.LastPresentTime != 0 || !haveImage)
                {
                    CopyDesktop(resource);
                    haveImage = true;
                    fresh = true;
                }
                else if (info.LastMouseUpdateTime != 0) fresh = true;  // only the pointer moved
            }
            finally
            {
                Com.Release(ref resource);
                // IDXGIOutputDuplication::ReleaseFrame
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(duplication, 14))(duplication);
            }
        }
        if (!haveImage || full == null || (!fresh && !force)) return null;
        var (w, h) = ScreenSize.Fit(full.Width, full.Height, maxWidth);
        if (target == null || target.Width != w || target.Height != h)
        {
            target?.Dispose();
            target = null;
            target = new Dib(w, h);
        }
        if (w == full.Width && h == full.Height)
            Gdi.BitBlt(target.Dc, 0, 0, w, h, full.Dc, 0, 0, Gdi.SRCCOPY);
        else
        {
            Gdi.SetStretchBltMode(target.Dc, Gdi.HALFTONE);
            Gdi.SetBrushOrgEx(target.Dc, 0, 0, IntPtr.Zero);
            Gdi.StretchBlt(target.Dc, 0, 0, w, h, full.Dc, 0, 0, full.Width, full.Height, Gdi.SRCCOPY);
        }
        Gdi.DrawPointer(target, screenWidth, screenHeight, originX, originY);
        Gdi.GdiFlush();
        return target.Image;
    }

    // The desktop texture → a staging texture the processor can read → `full`.
    void CopyDesktop(IntPtr resource)
    {
        var iid = IID_ID3D11Texture2D;
        IntPtr texture;
        Com.Check(Com.QueryInterface(resource, &iid, &texture), "ID3D11Texture2D");
        try
        {
            Texture2DDesc desc;
            // ID3D11Texture2D::GetDesc
            ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Com.Slot(texture, 10))(texture, &desc);
            if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
                throw new NotSupportedException($"The screen's pixel format ({desc.Format}) isn't 8-bit BGRA.");
            int width = (int)desc.Width, height = (int)desc.Height;
            if (staging == IntPtr.Zero || stagingWidth != width || stagingHeight != height)
            {
                Com.Release(ref staging);
                var s = desc;
                s.MipLevels = 1;
                s.ArraySize = 1;
                s.SampleCount = 1;
                s.SampleQuality = 0;
                s.Usage = 3;                 // D3D11_USAGE_STAGING
                s.BindFlags = 0;
                s.CpuAccessFlags = 0x20000;  // D3D11_CPU_ACCESS_READ
                s.MiscFlags = 0;
                IntPtr made;
                // ID3D11Device::CreateTexture2D
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Com.Slot(device, 5))(device, &s, IntPtr.Zero, &made), "CreateTexture2D");
                staging = made;
                stagingWidth = width;
                stagingHeight = height;
            }
            // ID3D11DeviceContext::CopyResource
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Com.Slot(context, 47))(context, staging, texture);
            Mapped mapped;
            // ID3D11DeviceContext::Map (D3D11_MAP_READ)
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)Com.Slot(context, 14))(context, staging, 0, 1, 0, &mapped), "Map");
            try
            {
                if (full == null || full.Width != width || full.Height != height)
                {
                    full?.Dispose();
                    full = null;
                    full = new Dib(width, height);
                }
                long row = (long)width * 4;
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy((byte*)mapped.Data + (long)y * mapped.RowPitch, (byte*)full.Bits + y * row, row, row);
            }
            finally
            {
                // ID3D11DeviceContext::Unmap
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Com.Slot(context, 15))(context, staging, 0);
            }
        }
        finally { Com.Release(ref texture); }
    }

    public void Dispose()
    {
        target?.Dispose();
        target = null;
        full?.Dispose();
        full = null;
        Com.Release(ref staging);
        Com.Release(ref duplication);
        Com.Release(ref context);
        Com.Release(ref device);
    }
}

// Calls into COM objects by their vtable (the slot numbers are from the Windows SDK headers:
// IUnknown is 0 to 2, then each interface's methods in declaration order after its base's).
internal static unsafe class Com
{
    public static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    public static int QueryInterface(IntPtr obj, Guid* iid, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(obj, 0))(obj, iid, result);

    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        var p = obj;
        obj = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, 2))(p);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8}).", hr);
    }
}

// A 32-bit top-down GDI bitmap in memory, selected into its own device context.
internal sealed class Dib : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public IntPtr Dc { get; private set; }
    public IntPtr Bits { get; }
    IntPtr bitmap, previous;

    public Dib(int width, int height)
    {
        Width = width;
        Height = height;
        var header = new Gdi.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        Dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (Dc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");
        bitmap = Gdi.CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Gdi.DeleteDC(Dc);
            Dc = IntPtr.Zero;
            throw new InvalidOperationException($"CreateDIBSection failed for {width}x{height}.");
        }
        Bits = bits;
        previous = Gdi.SelectObject(Dc, bitmap);
    }

    public ScreenImage Image => new(Bits, Width, Height, Width * 4);

    public void Dispose()
    {
        if (Dc == IntPtr.Zero) return;
        Gdi.SelectObject(Dc, previous);
        Gdi.DeleteObject(bitmap);
        Gdi.DeleteDC(Dc);
        Dc = IntPtr.Zero;
        bitmap = IntPtr.Zero;
    }
}

internal static class Gdi
{
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    public const int HALFTONE = 4;
    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const uint CURSOR_SHOWING = 1, DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorInfo
    {
        public uint Size, Flags;
        public IntPtr Cursor;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo
    {
        public int IsIcon;
        public int HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GdiFlush();

    // The capture thread counts in real pixels, whatever the display scaling, so the GDI copy,
    // the screen size and the pointer position all match Desktop Duplication's picture.
    public static void UseRealPixels()
    {
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); }  // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        catch (Exception) { }
    }

    // The mouse pointer onto `to`, which shows the screen area of size screenW × screenH whose
    // top-left is at (originX, originY). Drawn at its own size, so it stays easy to see.
    public static void DrawPointer(Dib to, int screenW, int screenH, int originX, int originY)
    {
        try
        {
            var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info) || (info.Flags & CURSOR_SHOWING) == 0 || info.Cursor == IntPtr.Zero) return;
            int hx = 0, hy = 0;
            if (GetIconInfo(info.Cursor, out var icon))
            {
                hx = icon.HotspotX;
                hy = icon.HotspotY;
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            }
            double sx = to.Width / (double)Math.Max(1, screenW), sy = to.Height / (double)Math.Max(1, screenH);
            int x = (int)((info.X - originX) * sx) - hx, y = (int)((info.Y - originY) * sy) - hy;
            DrawIconEx(to.Dc, x, y, info.Cursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        catch (Exception) { }  // no pointer is better than no picture
    }
}
