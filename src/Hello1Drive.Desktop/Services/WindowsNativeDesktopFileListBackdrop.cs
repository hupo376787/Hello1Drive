using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private long _nativeBackdropContentVersion = long.MinValue;
    private long _nativeBackdropGeometryVersion = long.MinValue;
    private NativeBackdrop? _nativeBackdrop;

    private void DisableLegacyColorKeyLayering()
    {
        StripLayeredStyle(Handle);
        StripLayeredStyle(ListHandle);
    }

    private static void StripLayeredStyle(nint hwnd)
    {
        if (hwnd == 0)
            return;

        var style = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE);
        var cleaned = (nint)((long)style & ~((long)WS_EX_LAYERED | WS_EX_TRANSPARENT));
        if (cleaned != style)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, cleaned);
            SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    private void SyncBackdrop(bool force)
    {
        var contentChanged = force || _nativeBackdropContentVersion != _host.BackdropContentVersion;
        var geometryChanged = force || _nativeBackdropGeometryVersion != _host.BackdropGeometryVersion;
        if (!contentChanged && !geometryChanged)
            return;

        if (contentChanged)
        {
            _nativeBackdrop?.Dispose();
            _nativeBackdrop = CreateNativeBackdrop(_host.BackdropImageBytes);
            _nativeBackdropContentVersion = _host.BackdropContentVersion;
        }

        _nativeBackdropGeometryVersion = _host.BackdropGeometryVersion;
        if (Handle != 0)
            InvalidateRect(Handle, 0, true);
        if (ListHandle != 0)
            InvalidateRect(ListHandle, 0, true);
    }

    private NativeBackdrop? CreateNativeBackdrop(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || _gdiPlusToken == 0)
            return null;

        try
        {
            var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
            if (hGlobal == 0)
                return null;

            var memory = GlobalLock(hGlobal);
            if (memory == 0)
            {
                GlobalFree(hGlobal);
                return null;
            }

            Marshal.Copy(bytes, 0, memory, bytes.Length);
            GlobalUnlock(hGlobal);

            if (CreateStreamOnHGlobal(hGlobal, true, out var stream) != 0 || stream is null)
            {
                GlobalFree(hGlobal);
                return null;
            }

            if (GdipLoadImageFromStream(stream, out var image) != 0 || image == 0)
            {
                ReleaseComStream(stream);
                return null;
            }

            if (GdipGetImageWidth(image, out var width) != 0 ||
                GdipGetImageHeight(image, out var height) != 0 || width == 0 || height == 0)
            {
                GdipDisposeImage(image);
                ReleaseComStream(stream);
                return null;
            }

            return new NativeBackdrop(image, width, height, stream);
        }
        catch
        {
            return null;
        }
    }

    private void PaintNativeBackdrop(nint hdc, RECT client)
    {
        if (hdc == 0 || client.Width <= 0 || client.Height <= 0)
            return;

        // Always start from a deterministic opaque color. Unlike the previous chroma-key HWND,
        // this makes ClearType text and Common Controls view switches stable.
        FillRectColor(hdc, client, _palette.Background);

        var backdrop = _nativeBackdrop;
        var viewport = _host.BackdropViewportSize;
        if (backdrop is null || viewport.Width <= 1 || viewport.Height <= 1 || _gdiPlusToken == 0)
            return;

        var scaleX = backdrop.Width / viewport.Width;
        var scaleY = backdrop.Height / viewport.Height;
        var nativeScale = Math.Max(0.01, _dpi / 96d);

        var sourceX = (int)Math.Round(_host.BackdropOrigin.X * scaleX);
        var sourceY = (int)Math.Round(_host.BackdropOrigin.Y * scaleY);
        var sourceWidth = Math.Max(1, (int)Math.Round((client.Width / nativeScale) * scaleX));
        var sourceHeight = Math.Max(1, (int)Math.Round((client.Height / nativeScale) * scaleY));

        var imageWidth = checked((int)backdrop.Width);
        var imageHeight = checked((int)backdrop.Height);
        sourceX = Math.Clamp(sourceX, 0, Math.Max(0, imageWidth - 1));
        sourceY = Math.Clamp(sourceY, 0, Math.Max(0, imageHeight - 1));
        sourceWidth = Math.Clamp(sourceWidth, 1, Math.Max(1, imageWidth - sourceX));
        sourceHeight = Math.Clamp(sourceHeight, 1, Math.Max(1, imageHeight - sourceY));

        if (GdipCreateFromHDC(hdc, out var graphics) != 0 || graphics == 0)
            return;

        try
        {
            GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBilinear);
            GdipDrawImageRectRectI(
                graphics,
                backdrop.Image,
                client.left,
                client.top,
                client.Width,
                client.Height,
                sourceX,
                sourceY,
                sourceWidth,
                sourceHeight,
                UnitPixel,
                0,
                0,
                0);
        }
        finally
        {
            GdipDeleteGraphics(graphics);
        }
    }

    private void DisposeNativeBackdrop()
    {
        _nativeBackdrop?.Dispose();
        _nativeBackdrop = null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64Compat(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32Compat(nint hwnd, int index);

    private static nint GetWindowLongPtrCompat(nint hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64Compat(hwnd, index) : (nint)GetWindowLong32Compat(hwnd, index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed class NativeBackdrop : IDisposable
    {
        public NativeBackdrop(nint image, uint width, uint height, IStream stream)
        {
            Image = image;
            Width = width;
            Height = height;
            Stream = stream;
        }

        public nint Image { get; }
        public uint Width { get; }
        public uint Height { get; }
        public IStream Stream { get; }

        public void Dispose()
        {
            if (Image != 0)
                GdipDisposeImage(Image);
            ReleaseComStream(Stream);
        }
    }
}
