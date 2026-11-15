// 32-bit DIB the control-panel preview copies into the HWND Windows gave us.
// Each Blit borrows that window's DC and releases it before returning. The screen
// DC used to size the DIB is released before Create returns.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GenesisTunnel;

sealed class PreviewBuffer : IDisposable
{
    readonly int width, height;
    readonly nint memory, dib, previous, bits;
    readonly byte[] bgra;
    bool disposed;

    PreviewBuffer(int width, int height, nint memory, nint dib, nint previous, nint bits)
    {
        this.width = width;
        this.height = height;
        this.memory = memory;
        this.dib = dib;
        this.previous = previous;
        this.bits = bits;
        bgra = new byte[width * height * 4];
    }

    public static PreviewBuffer Create(int width, int height)
    {
        var info = new NativeMethods.BitmapInfo();
        info.bmiHeader.biSize = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>();
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;

        nint screen = NativeMethods.GetDC(0);
        PreviewBuffer buffer = null;
        bool releaseFailed = false;
        int releaseError = 0;
        try
        {
            buffer = CreateCore(screen, ref info, width, height);
        }
        finally
        {
            // Record the failure here and throw below. Throwing in this block would
            // replace whatever CreateCore already raised, and a return inside try
            // would skip the check that follows the block.
            if (screen != 0 && NativeMethods.ReleaseDC(0, screen) == 0)
            {
                releaseFailed = true;
                releaseError = Marshal.GetLastWin32Error();
            }
        }
        if (releaseFailed)
        {
            buffer?.Dispose();
            throw new Win32Exception(releaseError);
        }
        return buffer;
    }

    static PreviewBuffer CreateCore(nint screen, ref NativeMethods.BitmapInfo info, int width, int height)
    {
        nint memory = NativeMethods.CreateCompatibleDC(screen);
        if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

        nint dib = NativeMethods.CreateDIBSection(screen, ref info, NativeMethods.DibRgbColors, out nint bits, 0, 0);
        if (dib == 0 || bits == 0)
        {
            int error = Marshal.GetLastWin32Error();
            NativeMethods.DeleteDC(memory);
            throw new Win32Exception(error);
        }

        nint previous = NativeMethods.SelectObject(memory, dib);
        if (previous == 0)
        {
            int error = Marshal.GetLastWin32Error();
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memory);
            throw new Win32Exception(error);
        }

        return new PreviewBuffer(width, height, memory, dib, previous, bits);
    }

    public void Load(byte[] rgba)
    {
        // Windows wants a bottom-up BGRA bitmap. The raylib picture is top-down RGBA.
        int w = width, h = height;
        for (int y = 0; y < h; y++)
        {
            int src = y * w * 4, dst = (h - 1 - y) * w * 4;
            for (int x = 0; x < w; x++, src += 4, dst += 4)
            {
                bgra[dst] = rgba[src + 2];
                bgra[dst + 1] = rgba[src + 1];
                bgra[dst + 2] = rgba[src];
                bgra[dst + 3] = 255;
            }
        }
        Marshal.Copy(bgra, 0, bits, bgra.Length);
    }

    public void Blit(nint hwnd, NativeMethods.Rect client)
    {
        nint dc = NativeMethods.GetDC(hwnd);
        if (dc == 0) return;

        // No return inside the try. A return there would run finally and then leave
        // the method, skipping the release check below.
        bool releaseFailed = false;
        int releaseError = 0;
        try
        {
            // Halftone keeps a scaled preview from looking harsh. Zero means the DC
            // rejected the mode; color-on-color is the unfiltered stretch. If both
            // fail the DC cannot stretch, and this frame is left as it was.
            bool canStretch = NativeMethods.SetStretchBltMode(dc, NativeMethods.StretchHalftone) != 0
                || NativeMethods.SetStretchBltMode(dc, NativeMethods.ColorOnColor) != 0;
            // A rejected blit is one missed frame, usually because the panel is resizing
            // the preview. The next tick paints again, so the BOOL is not fatal.
            if (canStretch)
                NativeMethods.StretchBlt(dc, 0, 0, client.Right - client.Left, client.Bottom - client.Top,
                    memory, 0, 0, width, height, NativeMethods.SrcCopy);
        }
        finally
        {
            if (NativeMethods.ReleaseDC(hwnd, dc) == 0)
            {
                releaseFailed = true;
                releaseError = Marshal.GetLastWin32Error();
            }
        }
        if (releaseFailed)
            throw new Win32Exception(releaseError);
    }

    public void Dispose()
    {
        // The process is leaving this preview. A failed delete does not change what
        // was already on screen, and Windows reclaims the GDI objects when we exit,
        // so the return values are intentionally unused.
        if (disposed) return;
        disposed = true;
        NativeMethods.SelectObject(memory, previous);
        NativeMethods.DeleteObject(dib);
        NativeMethods.DeleteDC(memory);
    }
}
