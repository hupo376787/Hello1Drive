using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Microsoft.Win32;

namespace Hello1Drive.Desktop.Services;

/// <summary>
/// Windows desktop file surface. The scroll/selection/hit-testing engine remains the native
/// SysListView32 control, while Hello1Drive paints the visible file cards itself.
/// </summary>
internal sealed class WindowsNativeDesktopFileListFactory : INativeDesktopFileListFactory
{
    private readonly Dictionary<nint, WindowsNativeDesktopFileListController> _controllers = [];

    public IPlatformHandle CreateControl(IPlatformHandle parent, NativeDesktopFileListHost host)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var controller = new WindowsNativeDesktopFileListController(parent.Handle, host);
        _controllers[controller.Handle] = controller;
        return new PlatformHandle(controller.Handle, "HWND");
    }

    public void DestroyControl(IPlatformHandle control)
    {
        if (_controllers.Remove(control.Handle, out var controller))
            controller.Dispose();
    }
}

internal sealed partial class WindowsNativeDesktopFileListController : IDisposable
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint LVS_REPORT = 0x0001;
    private const uint LVS_SHOWSELALWAYS = 0x0008;
    private const uint LVS_SHAREIMAGELISTS = 0x0040;
    private const uint LVS_NOLABELWRAP = 0x0080;
    private const uint LVS_NOCOLUMNHEADER = 0x4000;
    private const uint LVS_EX_FULLROWSELECT = 0x00000020;
    private const uint LVS_EX_DOUBLEBUFFER = 0x00010000;
    private const uint LVS_EX_LABELTIP = 0x00004000;

    private const int GWLP_WNDPROC = -4;
    private const int WM_SETREDRAW = 0x000B;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NOTIFY = 0x004E;
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_VSCROLL = 0x0115;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_PRINTCLIENT = 0x0318;
    private const uint ScrollIdleTimerId = 0x481D;
    private const uint TME_LEAVE = 0x00000002;

    private const int LVM_FIRST = 0x1000;
    private const int LVM_DELETEALLITEMS = LVM_FIRST + 9;
    private const int LVM_GETNEXTITEM = LVM_FIRST + 12;
    private const int LVM_GETITEMRECT = LVM_FIRST + 14;
    private const int LVM_ENSUREVISIBLE = LVM_FIRST + 19;
    private const int LVM_REDRAWITEMS = LVM_FIRST + 21;
    private const int LVM_DELETECOLUMN = LVM_FIRST + 28;
    private const int LVM_SETCOLUMNWIDTH = LVM_FIRST + 30;
    private const int LVM_GETTOPINDEX = LVM_FIRST + 39;
    private const int LVM_SETITEMSTATE = LVM_FIRST + 43;
    private const int LVM_GETITEMSTATE = LVM_FIRST + 44;
    private const int LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
    private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    private const int LVM_SETBKCOLOR = LVM_FIRST + 1;
    private const int LVM_SETTEXTCOLOR = LVM_FIRST + 36;
    private const int LVM_SETTEXTBKCOLOR = LVM_FIRST + 38;
    private const int LVM_SETIMAGELIST = LVM_FIRST + 3;
    private const int LVM_INSERTITEMW = LVM_FIRST + 77;
    private const int LVM_INSERTCOLUMNW = LVM_FIRST + 97;
    private const int LVM_SETITEMTEXTW = LVM_FIRST + 116;
    private const int LVM_SETVIEW = LVM_FIRST + 142;
    private const int LVM_SETICONSPACING = LVM_FIRST + 53;
    private const int LVM_HITTEST = LVM_FIRST + 18;

    private const int LVNI_SELECTED = 0x0002;
    private const int LVNI_VISIBLEONLY = 0x0040;
    private const int LVIR_BOUNDS = 0;
    private const uint LVIS_SELECTED = 0x0002;
    private const uint LVIF_TEXT = 0x0001;
    private const uint LVIF_IMAGE = 0x0002;
    private const uint LVCF_FMT = 0x0001;
    private const uint LVCF_WIDTH = 0x0002;
    private const uint LVCF_TEXT = 0x0004;
    private const uint LVCF_SUBITEM = 0x0008;
    private const int LVCFMT_LEFT = 0x0000;
    private const int LV_VIEW_ICON = 0x0000;
    private const int LV_VIEW_DETAILS = 0x0001;
    private const int LVSIL_NORMAL = 0;
    private const int LVSIL_SMALL = 1;
    private const uint ILC_MASK = 0x0001;
    private const uint ILC_COLOR32 = 0x0020;
    private const uint CLR_NONE = 0xFFFFFFFF;

    private const uint NM_CUSTOMDRAW = unchecked((uint)-12);
    private const uint CDDS_PREPAINT = 0x00000001;
    private const int CDRF_SKIPDEFAULT = 0x00000004;

    private const uint DT_LEFT = 0x00000000;
    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_NOPREFIX = 0x00000800;
    private const uint DT_END_ELLIPSIS = 0x00008000;
    private const int TRANSPARENT = 1;
    private const int NULL_PEN = 8;
    private const int FW_NORMAL = 400;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const int UnitPixel = 2;
    private const int CombineModeReplace = 0;
    private const int InterpolationModeHighQualityBilinear = 6;
    private const int MaxNativeThumbnailCache = 320;

    private const double DetailsRowHeight = 46;
    private const double GridSpacing = 4;
    private const double LargeWidth = 152;
    private const double LargeHeight = 162;
    private const double ExtraWidth = 220;
    private const double ExtraHeight = 212;
    private const double LargeArtwork = 94;
    private const double ExtraArtwork = 132;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private readonly NativeDesktopFileListHost _host;
    private readonly WndProc _parentWndProc;
    private readonly WndProc _listWndProc;
    private readonly nint _oldParentWndProc;
    private readonly nint _oldListWndProc;
    private readonly HashSet<VirtualDriveItemSlot> _subscribedSlots = [];
    private readonly Dictionary<string, NativeThumbnail> _thumbnailCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _thumbnailLru = [];

    private MainViewModel? _viewModel;
    private string _lastSignature = string.Empty;
    private bool _disposed;
    private bool _synchronizingSelection;
    private bool _trackingMouseLeave;
    private bool _scrolling;
    private int _hotIndex = -1;
    private int _lastIconLayoutColumns = -1;
    private int _lastIconLayoutItemCount = -1;
    private int _lastIconLayoutMode = -1;
    private uint _dpi = 96;
    private nint _detailsImageList;
    private nint _largeImageList;
    private nint _extraImageList;
    private nint _normalFont;
    private nint _mediumFont;
    private nint _smallFont;
    private nint _gdiPlusToken;
    private Palette _palette;

    public WindowsNativeDesktopFileListController(nint parent, NativeDesktopFileListHost host)
    {
        _host = host;
        InitCommonControls();
        _gdiPlusToken = StartGdiPlus();

        Handle = CreateWindowExW(
            0,
            "STATIC",
            string.Empty,
            WS_CHILD | WS_VISIBLE,
            0, 0, 100, 100,
            parent,
            0,
            GetModuleHandleW(null),
            0);
        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx(native desktop host) failed: {Marshal.GetLastWin32Error()}");

        _parentWndProc = ParentWindowProc;
        _oldParentWndProc = SetWindowLongPtr(Handle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_parentWndProc));
        if (_oldParentWndProc == 0)
            throw new InvalidOperationException($"Unable to subclass native desktop host: {Marshal.GetLastWin32Error()}");

        ListHandle = CreateWindowExW(
            0,
            "SysListView32",
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | LVS_REPORT | LVS_SHOWSELALWAYS |
            LVS_SHAREIMAGELISTS | LVS_NOLABELWRAP | LVS_NOCOLUMNHEADER,
            0, 0, 100, 100,
            Handle,
            0,
            GetModuleHandleW(null),
            0);
        if (ListHandle == 0)
            throw new InvalidOperationException($"CreateWindowEx(SysListView32) failed: {Marshal.GetLastWin32Error()}");

        _dpi = Math.Max(96u, GetDpiForWindow(ListHandle));
        RecreateNativeResources();
        SendMessage(ListHandle, LVM_SETEXTENDEDLISTVIEWSTYLE, 0,
            (nint)(LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_LABELTIP));
        SetWindowTheme(ListHandle, "Explorer", null);
        ConfigureColumns();
        ApplyPaletteToNativeWindow();

        _listWndProc = ListWindowProc;
        _oldListWndProc = SetWindowLongPtr(ListHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_listWndProc));
        if (_oldListWndProc == 0)
            throw new InvalidOperationException($"Unable to subclass native ListView: {Marshal.GetLastWin32Error()}");

        ResizeListToHost();
        _host.HostStateChanged += Host_HostStateChanged;
        _host.PropertyChanged += Host_PropertyChanged;
        AttachViewModel(_host.ViewModel);
        SyncPresentation(force: true);
        ShowWindow(Handle, _host.IsVisible ? SW_SHOW : SW_HIDE);
    }

    public nint Handle { get; }
    private nint ListHandle { get; }

    private void Host_HostStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        AttachViewModel(_host.ViewModel);
        SyncPresentation(force: false);
    }

    private void Host_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || Handle == 0 || e.Property.Name != nameof(NativeDesktopFileListHost.IsVisible))
            return;

        // NativeControlHost is an HWND airspace island. Avalonia ZIndex cannot cover it, so hide
        // the actual HWND immediately whenever MainView hides the host for Settings/Preview/modal UI.
        ShowWindow(Handle, _host.IsVisible ? SW_SHOW : SW_HIDE);
    }
}
