// Win32 surface for the control-panel preview. Raylib cannot draw into the HWND
// Windows hands to a screen saver, so ShowPreview renders off-screen and StretchBlts
// the picture in. LibraryImport generates the marshalling at compile time; bools are
// the 4-byte Win32 BOOL, not the 1-byte default.

using System.Runtime.InteropServices;

namespace GenesisTunnel;

static partial class NativeMethods
{
    internal const int StretchHalftone = 4;   // HALFTONE. Filters when the panel scales the preview.
    internal const int ColorOnColor = 3;      // COLORONCOLOR. Used when the DC rejects halftone.
    internal const int SrcCopy = 0x00CC0020;  // SRCCOPY
    internal const uint DibRgbColors = 0;     // DIB_RGB_COLORS

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetDC(nint hWnd);

    // 1 if the DC was released, 0 if it was not. Callers stop when this is 0 so a
    // stuck DC cannot leak once per frame for as long as the control panel is open.
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int ReleaseDC(nint hWnd, nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateDIBSection(nint hdc, ref BitmapInfo bmi, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    // The previous stretch mode, or 0 when the DC rejected the call.
    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int SetStretchBltMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, int rop);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
    }
}
