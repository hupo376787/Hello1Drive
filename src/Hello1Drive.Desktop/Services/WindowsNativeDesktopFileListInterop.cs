using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Microsoft.Win32;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INITCOMMONCONTROLSEX { public uint dwSize; public uint dwICC; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVCOLUMN
    {
        public uint mask;
        public int fmt;
        public int cx;
        public nint pszText;
        public int cchTextMax;
        public int iSubItem;
        public int iImage;
        public int iOrder;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public RECT(int left, int top, int right, int bottom)
        {
            this.left = left;
            this.top = top;
            this.right = right;
            this.bottom = bottom;
        }
        public int left;
        public int top;
        public int right;
        public int bottom;
        public readonly int Width => right - left;
        public readonly int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMHDR
    {
        public nint hwndFrom;
        public nuint idFrom;
        public uint code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMCUSTOMDRAW
    {
        public NMHDR hdr;
        public uint dwDrawStage;
        public nint hdc;
        public RECT rc;
        public nuint dwItemSpec;
        public uint uItemState;
        public nint lItemlParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMLVCUSTOMDRAW
    {
        public NMCUSTOMDRAW nmcd;
        public uint clrText;
        public uint clrTextBk;
        public int iSubItem;
        public uint dwItemType;
        public uint clrFace;
        public int iIconEffect;
        public int iIconPhase;
        public int iPartId;
        public int iStateId;
        public RECT rcText;
        public uint uAlign;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        [MarshalAs(UnmanagedType.Bool)] public bool SuppressBackgroundThread;
        [MarshalAs(UnmanagedType.Bool)] public bool SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupOutput
    {
        public nint NotificationHook;
        public nint NotificationUnhook;
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX lpInitCtrls);

    [DllImport("comctl32.dll")]
    private static extern nint ImageList_Create(int cx, int cy, uint flags, int cInitial, int cGrow);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImageList_Destroy(nint himl);

    [DllImport("comctl32.dll")]
    private static extern int ImageList_ReplaceIcon(nint himl, int i, nint hicon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcW(nint previous, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int newValue);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint newValue) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, newValue) : (nint)SetWindowLong32(hwnd, index, (int)newValue);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(nint hwnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint elapse, nint timerProc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint hwnd, nuint id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tracking);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);

    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeParentBackground(nint hwnd, nint hdc, ref RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint hInstance, nint iconName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);

    [DllImport("gdi32.dll")]
    private static extern int SaveDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RestoreDC(nint hdc, int savedDc);

    [DllImport("gdi32.dll")]
    private static extern int SelectClipRgn(nint hdc, nint region);

    [DllImport("gdi32.dll")]
    private static extern int IntersectClipRect(nint hdc, int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rect, nint brush);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Polygon(nint hdc, [In] POINT[] points, int count);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint hdc, string text, int count, ref RECT rect, uint format);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint globalMemory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint globalMemory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint globalMemory);

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(nint globalMemory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out nint token, ref GdiplusStartupInput input, out GdiplusStartupOutput output);

    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(nint token);

    [DllImport("gdiplus.dll")]
    private static extern int GdipLoadImageFromStream([MarshalAs(UnmanagedType.Interface)] IStream stream, out nint image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(nint image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(nint image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(nint image, out uint height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateFromHDC(nint hdc, out nint graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteGraphics(nint graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetInterpolationMode(nint graphics, int interpolationMode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetClipHrgn(nint graphics, nint region, int combineMode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawImageRectRectI(
        nint graphics,
        nint image,
        int dstX,
        int dstY,
        int dstWidth,
        int dstHeight,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        int srcUnit,
        nint imageAttributes,
        nint callback,
        nint callbackData);
}
