using System.Runtime.InteropServices;
using Avalonia.Platform;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Desktop.Services;

/// <summary>
/// Windows file surface backed by the shell's native SysListView32. Scrolling, selection,
/// hit-testing and item virtualization are handled by USER32/comctl32 rather than Avalonia's
/// retained scene. This removes the managed ScrollViewer/render work from the wheel hot path.
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

internal sealed class WindowsNativeDesktopFileListController : IDisposable
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint LVS_REPORT = 0x0001;
    private const uint LVS_SHOWSELALWAYS = 0x0008;
    private const uint LVS_SHAREIMAGELISTS = 0x0040;
    private const uint LVS_NOCOLUMNHEADER = 0x4000;
    private const uint LVS_EX_FULLROWSELECT = 0x00000020;
    private const uint LVS_EX_DOUBLEBUFFER = 0x00010000;
    private const uint LVS_EX_LABELTIP = 0x00004000;

    private const int GWL_STYLE = -16;
    private const int GWLP_WNDPROC = -4;
    private const int WM_SETREDRAW = 0x000B;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_VSCROLL = 0x0115;
    private const uint WM_KEYUP = 0x0101;

    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    private const int LVM_DELETEALLITEMS = LVM_FIRST + 9;
    private const int LVM_GETNEXTITEM = LVM_FIRST + 12;
    private const int LVM_ENSUREVISIBLE = LVM_FIRST + 19;
    private const int LVM_DELETECOLUMN = LVM_FIRST + 28;
    private const int LVM_GETTOPINDEX = LVM_FIRST + 39;
    private const int LVM_GETCOUNTPERPAGE = LVM_FIRST + 40;
    private const int LVM_SETITEMSTATE = LVM_FIRST + 43;
    private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    private const int LVM_SETIMAGELIST = LVM_FIRST + 3;
    private const int LVM_INSERTITEMW = LVM_FIRST + 77;
    private const int LVM_INSERTCOLUMNW = LVM_FIRST + 97;
    private const int LVM_SETITEMTEXTW = LVM_FIRST + 116;
    private const int LVM_SETVIEW = LVM_FIRST + 142;
    private const int LVM_SETICONSPACING = LVM_FIRST + 53;
    private const int LVM_HITTEST = LVM_FIRST + 18;

    private const int LVNI_SELECTED = 0x0002;
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

    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private readonly NativeDesktopFileListHost _host;
    private readonly WndProc _wndProc;
    private readonly nint _oldWndProc;
    private MainViewModel? _viewModel;
    private string _lastSignature = string.Empty;
    private bool _disposed;
    private bool _synchronizingSelection;

    public WindowsNativeDesktopFileListController(nint parent, NativeDesktopFileListHost host)
    {
        _host = host;
        InitCommonControls();

        Handle = CreateWindowExW(
            0,
            "SysListView32",
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | LVS_REPORT | LVS_SHOWSELALWAYS | LVS_SHAREIMAGELISTS | LVS_NOCOLUMNHEADER,
            0, 0, 100, 100,
            parent,
            0,
            GetModuleHandleW(null),
            0);

        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx(SysListView32) failed: {Marshal.GetLastWin32Error()}");

        SendMessage(Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, 0,
            (nint)(LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_LABELTIP));
        ConfigureColumns();
        ConfigureShellImageLists();

        _wndProc = WindowProc;
        var procPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        _oldWndProc = SetWindowLongPtr(Handle, GWLP_WNDPROC, procPtr);
        if (_oldWndProc == 0)
            throw new InvalidOperationException($"Unable to subclass native ListView: {Marshal.GetLastWin32Error()}");

        _host.HostStateChanged += Host_HostStateChanged;
        AttachViewModel(_host.ViewModel);
        SyncPresentation(force: true);
    }

    public nint Handle { get; }

    private void Host_HostStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        AttachViewModel(_host.ViewModel);
        SyncPresentation(force: false);
    }

    private void AttachViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_viewModel, vm))
            return;
        _viewModel = vm;
        _lastSignature = string.Empty;
    }

    private void SyncPresentation(bool force)
    {
        if (_disposed || _viewModel is null)
            return;

        var slots = _viewModel.VirtualItems;
        var mode = _viewModel.ViewMode;
        var signature = BuildSignature(slots, mode);
        if (!force && string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            RestoreSelection();
            return;
        }

        _lastSignature = signature;
        SendMessage(Handle, WM_SETREDRAW, 0, 0);
        try
        {
            ApplyNativeView(mode);
            SendMessage(Handle, LVM_DELETEALLITEMS, 0, 0);

            for (var index = 0; index < slots.Count; index++)
            {
                var item = slots[index].Item;
                InsertItem(index, item);
            }

            RestoreSelection();
        }
        finally
        {
            SendMessage(Handle, WM_SETREDRAW, 1, 0);
            InvalidateRect(Handle, 0, true);
        }
        ReportScrollPosition();
    }

    private static string BuildSignature(IReadOnlyList<VirtualDriveItemSlot> slots, FileViewMode mode)
    {
        var hash = new HashCode();
        hash.Add((int)mode);
        hash.Add(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var item = slots[i].Item;
            if (item is null)
                continue;
            hash.Add(item.Id, StringComparer.Ordinal);
            hash.Add(item.Name, StringComparer.Ordinal);
            hash.Add(item.Size);
            hash.Add(item.LastModifiedDateTime);
        }
        return hash.ToHashCode().ToString("X8");
    }

    private void ApplyNativeView(FileViewMode mode)
    {
        var nativeView = mode == FileViewMode.Details ? LV_VIEW_DETAILS : LV_VIEW_ICON;
        SendMessage(Handle, LVM_SETVIEW, (nint)nativeView, 0);
        if (mode == FileViewMode.LargeIcons)
            SendMessage(Handle, LVM_SETICONSPACING, 0, MakeLParam(150, 86));
        else if (mode == FileViewMode.ExtraLargeIcons)
            SendMessage(Handle, LVM_SETICONSPACING, 0, MakeLParam(210, 118));
    }

    private void InsertItem(int index, DriveItemModel? item)
    {
        var name = item?.Name ?? string.Empty;
        var imageIndex = item is null ? -1 : GetShellIconIndex(item, large: _viewModel?.ViewMode != FileViewMode.Details);
        var namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var lvItem = new LVITEM
            {
                mask = LVIF_TEXT | (imageIndex >= 0 ? LVIF_IMAGE : 0),
                iItem = index,
                iSubItem = 0,
                pszText = namePtr,
                iImage = imageIndex
            };
            var itemPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEM>());
            try
            {
                Marshal.StructureToPtr(lvItem, itemPtr, false);
                SendMessage(Handle, LVM_INSERTITEMW, 0, itemPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(itemPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }

        if (item is null || _viewModel?.ViewMode != FileViewMode.Details)
            return;
        SetSubItem(index, 1, item.TypeDisplay);
        SetSubItem(index, 2, item.SizeDisplay);
        SetSubItem(index, 3, item.ModifiedDisplay);
    }

    private void SetSubItem(int itemIndex, int subItem, string text)
    {
        var textPtr = Marshal.StringToHGlobalUni(text ?? string.Empty);
        var lvItem = new LVITEM { iSubItem = subItem, pszText = textPtr };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEM>());
        try
        {
            Marshal.StructureToPtr(lvItem, ptr, false);
            SendMessage(Handle, LVM_SETITEMTEXTW, (nint)itemIndex, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(textPtr);
        }
    }

    private void RestoreSelection()
    {
        if (_viewModel is null)
            return;

        var selectedIds = _viewModel.SelectedItemsSnapshot
            .Select(static x => x.Id)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        _synchronizingSelection = true;
        try
        {
            for (var i = 0; i < _viewModel.VirtualItems.Count; i++)
            {
                var item = _viewModel.VirtualItems[i].Item;
                SetItemSelected(i, item is not null && selectedIds.Contains(item.Id));
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void SetItemSelected(int index, bool selected)
    {
        var state = new LVITEM
        {
            stateMask = LVIS_SELECTED,
            state = selected ? LVIS_SELECTED : 0
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEM>());
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            SendMessage(Handle, LVM_SETITEMSTATE, (nint)index, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void RaiseSelectionChanged()
    {
        if (_synchronizingSelection || _viewModel is null)
            return;

        var ids = new List<string>();
        var current = -1;
        while (true)
        {
            current = (int)SendMessage(Handle, LVM_GETNEXTITEM, (nint)current, (nint)LVNI_SELECTED);
            if (current < 0)
                break;
            if (current < _viewModel.VirtualItems.Count && _viewModel.VirtualItems[current].Item is { Id.Length: > 0 } item)
                ids.Add(item.Id);
        }
        _host.RaiseSelectionChanged(ids);
    }

    private DriveItemModel? HitTest(nint lParam)
    {
        if (_viewModel is null)
            return null;
        var point = new LVHITTESTINFO
        {
            pt = new POINT
            {
                x = unchecked((short)((long)lParam & 0xFFFF)),
                y = unchecked((short)(((long)lParam >> 16) & 0xFFFF))
            }
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVHITTESTINFO>());
        try
        {
            Marshal.StructureToPtr(point, ptr, false);
            var index = (int)SendMessage(Handle, LVM_HITTEST, 0, ptr);
            return index >= 0 && index < _viewModel.VirtualItems.Count
                ? _viewModel.VirtualItems[index].Item
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void ReportScrollPosition()
    {
        if (_viewModel is null)
            return;
        var first = Math.Max(0, (int)SendMessage(Handle, LVM_GETTOPINDEX, 0, 0));
        var perPage = Math.Max(1, (int)SendMessage(Handle, LVM_GETCOUNTPERPAGE, 0, 0));
        _host.RaiseScrollStateChanged(first, Math.Min(_viewModel.VirtualItems.Count - 1, first + perPage));
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var result = CallWindowProcW(_oldWndProc, hwnd, msg, wParam, lParam);
        if (_disposed)
            return result;

        switch (msg)
        {
            case WM_LBUTTONUP:
            case WM_KEYUP:
                RaiseSelectionChanged();
                break;
            case WM_LBUTTONDBLCLK:
                if (HitTest(lParam) is { } doubleItem)
                    _host.RaiseItemDoubleTapped(doubleItem);
                break;
            case WM_RBUTTONUP:
                if (HitTest(lParam) is { } contextItem)
                    _host.RaiseItemContextRequested(contextItem);
                RaiseSelectionChanged();
                break;
            case WM_MOUSEWHEEL:
            case WM_VSCROLL:
                ReportScrollPosition();
                break;
        }
        return result;
    }

    private void ConfigureColumns()
    {
        while (SendMessage(Handle, LVM_DELETECOLUMN, 0, 0) != 0) { }
        InsertColumn(0, "名称", 520);
        InsertColumn(1, "类型", 150);
        InsertColumn(2, "大小", 130);
        InsertColumn(3, "修改时间", 190);
    }

    private void InsertColumn(int index, string title, int width)
    {
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var column = new LVCOLUMN
        {
            mask = LVCF_FMT | LVCF_WIDTH | LVCF_TEXT | LVCF_SUBITEM,
            fmt = LVCFMT_LEFT,
            cx = width,
            pszText = titlePtr,
            iSubItem = index
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVCOLUMN>());
        try
        {
            Marshal.StructureToPtr(column, ptr, false);
            SendMessage(Handle, LVM_INSERTCOLUMNW, (nint)index, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(titlePtr);
        }
    }

    private void ConfigureShellImageLists()
    {
        var fileInfo = new SHFILEINFO();
        var small = SHGetFileInfoW(".txt", FILE_ATTRIBUTE_NORMAL, ref fileInfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES | SHGFI_SMALLICON);
        var large = SHGetFileInfoW(".txt", FILE_ATTRIBUTE_NORMAL, ref fileInfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES | SHGFI_LARGEICON);
        if (small != 0)
            SendMessage(Handle, LVM_SETIMAGELIST, LVSIL_SMALL, small);
        if (large != 0)
            SendMessage(Handle, LVM_SETIMAGELIST, LVSIL_NORMAL, large);
    }

    private static int GetShellIconIndex(DriveItemModel item, bool large)
    {
        var attrs = item.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var lookup = item.IsFolder ? "Folder" : (string.IsNullOrWhiteSpace(item.Extension) ? item.Name : item.Extension);
        var info = new SHFILEINFO();
        var result = SHGetFileInfoW(lookup, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON));
        return result == 0 ? -1 : info.iIcon;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.HostStateChanged -= Host_HostStateChanged;
        if (Handle != 0)
        {
            if (_oldWndProc != 0)
                SetWindowLongPtr(Handle, GWLP_WNDPROC, _oldWndProc);
            DestroyWindow(Handle);
        }
        GC.KeepAlive(_wndProc);
    }

    private static nint MakeLParam(int low, int high) => (nint)((high << 16) | (low & 0xFFFF));

    private static void InitCommonControls()
    {
        var data = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = 0x00000001
        };
        InitCommonControlsEx(ref data);
    }

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
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX lpInitCtrls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint cbFileInfo, uint flags);
}
