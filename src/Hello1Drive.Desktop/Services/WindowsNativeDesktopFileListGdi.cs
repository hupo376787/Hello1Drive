using System.Runtime.InteropServices.ComTypes;
using Avalonia.Media.Imaging;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private int ScaleInt(double value) => Math.Max(1, (int)Math.Round(value * _dpi / 96d));

    private static RECT Inset(RECT rect, int x, int y) => new(
        rect.left + x,
        rect.top + y,
        Math.Max(rect.left + x + 1, rect.right - x),
        Math.Max(rect.top + y + 1, rect.bottom - y));

    private static void DrawTextLine(nint hdc, string? text, RECT rect, nint font, uint color, bool center)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 1 || rect.Height <= 1)
            return;

        var oldFont = font != 0 ? SelectObject(hdc, font) : 0;
        var previousColor = SetTextColor(hdc, color);
        SetBkMode(hdc, TRANSPARENT);
        var flags = DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX | (center ? DT_CENTER : DT_LEFT);
        DrawTextW(hdc, text, text.Length, ref rect, flags);
        SetTextColor(hdc, previousColor);
        if (oldFont != 0)
            SelectObject(hdc, oldFont);
    }

    private static void FillRectColor(nint hdc, RECT rect, uint color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var brush = CreateSolidBrush(color);
        if (brush == 0)
            return;
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }

    private static void FillRoundRect(nint hdc, RECT rect, int radius, uint color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var brush = CreateSolidBrush(color);
        if (brush == 0)
            return;
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
        RoundRect(hdc, rect.left, rect.top, rect.right, rect.bottom, Math.Max(2, radius * 2), Math.Max(2, radius * 2));
        if (oldPen != 0) SelectObject(hdc, oldPen);
        if (oldBrush != 0) SelectObject(hdc, oldBrush);
        DeleteObject(brush);
    }

    private static void FillEllipse(nint hdc, RECT rect, uint color)
    {
        var brush = CreateSolidBrush(color);
        if (brush == 0)
            return;
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
        Ellipse(hdc, rect.left, rect.top, rect.right, rect.bottom);
        if (oldPen != 0) SelectObject(hdc, oldPen);
        if (oldBrush != 0) SelectObject(hdc, oldBrush);
        DeleteObject(brush);
    }

    private static void FillPolygon(nint hdc, POINT[] points, uint color)
    {
        var brush = CreateSolidBrush(color);
        if (brush == 0)
            return;
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
        Polygon(hdc, points, points.Length);
        if (oldPen != 0) SelectObject(hdc, oldPen);
        if (oldBrush != 0) SelectObject(hdc, oldBrush);
        DeleteObject(brush);
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        KillTimer(ListHandle, (nuint)ScrollIdleTimerId);
        if (_viewModel is not null)
            _viewModel.SetDesktopListScrolling(false);
        _host.HostStateChanged -= Host_HostStateChanged;
        _host.PropertyChanged -= Host_PropertyChanged;
        AttachViewModel(null);
        ClearThumbnailCache();
        DisposeNativeBackdrop();
        if (ListHandle != 0)
        {
            SendMessage(ListHandle, LVM_SETIMAGELIST, LVSIL_SMALL, 0);
            SendMessage(ListHandle, LVM_SETIMAGELIST, LVSIL_NORMAL, 0);
        }
        DestroyNativeResources();

        if (ListHandle != 0 && _oldListWndProc != 0)
            SetWindowLongPtr(ListHandle, GWLP_WNDPROC, _oldListWndProc);
        if (Handle != 0 && _oldParentWndProc != 0)
            SetWindowLongPtr(Handle, GWLP_WNDPROC, _oldParentWndProc);
        if (Handle != 0)
            DestroyWindow(Handle);

        if (_gdiPlusToken != 0)
        {
            GdiplusShutdown(_gdiPlusToken);
            _gdiPlusToken = 0;
        }

        GC.KeepAlive(_parentWndProc);
        GC.KeepAlive(_listWndProc);
    }

    private static nint StartGdiPlus()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        return GdiplusStartup(out var token, ref input, out _) == 0 ? token : 0;
    }

    private static nint MakeLParam(int low, int high) => (nint)((high << 16) | (low & 0xFFFF));

    private static void InitCommonControls()
    {
        var data = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = 0x00000001
        };
        InitCommonControlsEx(ref data);
    }

    private sealed class NativeThumbnail : IDisposable
    {
        public NativeThumbnail(Bitmap source, string versionToken, nint image, uint width, uint height, IStream stream)
        {
            Source = source;
            VersionToken = versionToken;
            Image = image;
            Width = width;
            Height = height;
            Stream = stream;
        }

        public Bitmap Source { get; }
        public string VersionToken { get; }
        public nint Image { get; }
        public uint Width { get; }
        public uint Height { get; }
        public IStream Stream { get; }
        public LinkedListNode<string>? LruNode { get; set; }

        public void Dispose()
        {
            if (Image != 0)
                GdipDisposeImage(Image);
            ReleaseComStream(Stream);
        }
    }

    private readonly record struct Palette(
        uint Background,
        uint Surface,
        uint Hover,
        uint Selection,
        uint Text,
        uint MutedText,
        uint Placeholder,
        uint FileBody,
        uint ThumbnailBackground,
        uint VideoBadge);

}
