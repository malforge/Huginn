using System;
using System.Runtime.InteropServices;

namespace Huginn.Services;

/// <summary>
/// Renders a numeric badge overlay on the app's taskbar icon using ITaskbarList3.
/// Uses raw vtable calls instead of built-in COM interop, making it fully trim-safe.
/// </summary>
public sealed class TaskbarBadgeService : IBadgeService
{
    private IntPtr _taskbar;   // ITaskbarList3*
    private IntPtr _hwnd;
    private IntPtr _currentIcon;

    // Vtable indices (IUnknown=0-2, ITaskbarList=3-7, ITaskbarList2=8, ITaskbarList3=9-18)
    private const int VtRelease = 2;
    private const int VtHrInit = 3;
    private const int VtSetOverlayIcon = 18;

    public bool Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            var clsid = new Guid("56fdf344-fd6d-11d0-958a-006097c9a090");
            var iid = new Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */ | 4 /* CLSCTX_LOCAL_SERVER */, ref iid, out _taskbar);
            if (hr < 0 || _taskbar == IntPtr.Zero)
            {
                Log.Error($"TaskbarBadge CoCreateInstance failed: 0x{hr:X8}");
                return false;
            }

            hr = VtableCall_HrInit(_taskbar);
            if (hr < 0)
            {
                Log.Error($"TaskbarBadge HrInit failed: 0x{hr:X8}");
                Release();
                return false;
            }

            Log.Info($"TaskbarBadge initialized: hwnd={hwnd}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"TaskbarBadge init failed: {ex.Message}");
            return false;
        }
    }

    public void SetBadge(int count)
    {
        if (_taskbar == IntPtr.Zero || _hwnd == IntPtr.Zero) return;

        ClearCurrentIcon();

        if (count <= 0)
        {
            VtableCall_SetOverlayIcon(_taskbar, _hwnd, IntPtr.Zero, null);
            return;
        }

        _currentIcon = CreateBadgeIcon(count);
        if (_currentIcon != IntPtr.Zero)
            VtableCall_SetOverlayIcon(_taskbar, _hwnd, _currentIcon, $"{count} item(s) need attention");
    }

    public void Dispose()
    {
        ClearCurrentIcon();
        if (_taskbar != IntPtr.Zero && _hwnd != IntPtr.Zero)
        {
            try { VtableCall_SetOverlayIcon(_taskbar, _hwnd, IntPtr.Zero, null); } catch { }
        }
        Release();
    }

    private void Release()
    {
        if (_taskbar != IntPtr.Zero)
        {
            VtableCall_Release(_taskbar);
            _taskbar = IntPtr.Zero;
        }
    }

    private void ClearCurrentIcon()
    {
        if (_currentIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentIcon);
            _currentIcon = IntPtr.Zero;
        }
    }

    // ── Raw vtable calls (trim-safe, no COM interop runtime needed) ────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DRelease(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DHrInit(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int DSetOverlayIcon(IntPtr self, IntPtr hwnd, IntPtr hIcon, string? description);

    private static IntPtr GetVtableSlot(IntPtr comObj, int slot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObj);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static int VtableCall_Release(IntPtr comObj)
        => Marshal.GetDelegateForFunctionPointer<DRelease>(GetVtableSlot(comObj, VtRelease))(comObj);

    private static int VtableCall_HrInit(IntPtr comObj)
        => Marshal.GetDelegateForFunctionPointer<DHrInit>(GetVtableSlot(comObj, VtHrInit))(comObj);

    private static int VtableCall_SetOverlayIcon(IntPtr comObj, IntPtr hwnd, IntPtr hIcon, string? desc)
        => Marshal.GetDelegateForFunctionPointer<DSetOverlayIcon>(GetVtableSlot(comObj, VtSetOverlayIcon))(comObj, hwnd, hIcon, desc);

    // ── Badge rendering (pixel-art, no GDI text) ───────────────────────

    private static IntPtr CreateBadgeIcon(int count)
    {
        const int size = 16;
        var pixels = new byte[size * size * 4]; // BGRA

        // Draw filled red circle with anti-aliased edge
        const double cx = 7.5, cy = 7.5, r = 7.0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x - cx, dy = y - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= r + 0.8)
            {
                double alpha = Math.Clamp((r + 0.8 - dist) / 1.0, 0, 1);
                int i = (y * size + x) * 4;
                pixels[i + 0] = 0x11; // B
                pixels[i + 1] = 0x11; // G
                pixels[i + 2] = 0xCC; // R
                pixels[i + 3] = (byte)(alpha * 255);
            }
        }

        // Render digit(s) in white
        var text = count > 9 ? "9+" : count.ToString();
        RenderDigits(pixels, size, text);

        return PixelsToHIcon(pixels, size);
    }

    // 5×7 bold pixel font for digits 0–9 and '+'. Each byte's lower 5 bits = columns (MSB=left).
    private const int GlyphW = 5, GlyphH = 7;
    private static readonly byte[][] MiniFont =
    [
        [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110], // 0
        [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110], // 1
        [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111], // 2
        [0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110], // 3
        [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010], // 4
        [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110], // 5
        [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110], // 6
        [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000], // 7
        [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110], // 8
        [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100], // 9
        [0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000], // + (index 10)
    ];

    private static void RenderDigits(byte[] pixels, int canvasSize, string text)
    {
        int totalWidth = text.Length * GlyphW + (text.Length - 1); // 5px per char + 1px gap
        int startX = (canvasSize - totalWidth) / 2;
        int startY = (canvasSize - GlyphH) / 2;

        int curX = startX;
        foreach (char c in text)
        {
            int idx = c == '+' ? 10 : (c - '0');
            if (idx < 0 || idx >= MiniFont.Length) continue;

            var glyph = MiniFont[idx];
            for (int row = 0; row < GlyphH; row++)
            for (int col = 0; col < GlyphW; col++)
            {
                if ((glyph[row] & (0b10000 >> col)) != 0)
                {
                    int px = curX + col, py = startY + row;
                    if (px >= 0 && px < canvasSize && py >= 0 && py < canvasSize)
                    {
                        int i = (py * canvasSize + px) * 4;
                        pixels[i + 0] = 0xFF; // B
                        pixels[i + 1] = 0xFF; // G
                        pixels[i + 2] = 0xFF; // R
                        pixels[i + 3] = 0xFF; // A
                    }
                }
            }
            curX += GlyphW + 1; // 5px char + 1px gap
        }
    }

    private static IntPtr PixelsToHIcon(byte[] pixels, int size)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            }
        };

        var hdc = GetDC(IntPtr.Zero);
        var hBitmap = CreateDIBSection(hdc, ref bmi, 0, out var ppvBits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, hdc);

        if (hBitmap == IntPtr.Zero || ppvBits == IntPtr.Zero)
            return IntPtr.Zero;

        Marshal.Copy(pixels, 0, ppvBits, pixels.Length);

        // Monochrome mask — all zeros means "use the alpha in the color bitmap"
        int maskStride = ((size + 15) / 16) * 2;
        var maskBits = new byte[maskStride * size];
        var hMask = CreateBitmap(size, size, 1, 1, maskBits);

        var iconInfo = new ICONINFO { fIcon = true, hbmMask = hMask, hbmColor = hBitmap };
        var hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(hBitmap);
        DeleteObject(hMask);

        return hIcon;
    }

    // ── P/Invoke ────────────────────────────────────────────────────────

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr ppv);

    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[]? bits);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }
}
