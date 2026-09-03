using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int NativeFontMedium = 500;

    private Palette BuildPalette()
    {
        var dark = IsDarkTheme();
        var background = ResolveBackgroundColor(dark);
        if (dark)
        {
            return new Palette(
                background,
                Blend(background, Rgb(42, 44, 50), 0.72),
                Blend(background, Rgb(255, 255, 255), 0.09),
                Blend(background, Rgb(47, 128, 237), 0.34),
                Rgb(244, 244, 246),
                Rgb(179, 181, 188),
                Rgb(54, 56, 63),
                Rgb(42, 44, 50),
                Rgb(28, 29, 33),
                Rgb(0, 0, 0));
        }

        // Avalonia's original desktop surface used a partially opaque #1B1B1F foreground over
        // the wallpaper. GDI text has no brush opacity, so use the visually equivalent softened
        // dark tone instead of the previous fully opaque near-black text.
        return new Palette(
            background,
            Blend(background, Rgb(255, 255, 255), 0.72),
            Blend(background, Rgb(225, 232, 242), 0.58),
            Blend(background, Rgb(47, 128, 237), 0.20),
            Rgb(43, 43, 47),
            Rgb(104, 106, 113),
            Rgb(232, 234, 238),
            Rgb(248, 250, 252),
            Rgb(242, 243, 246),
            Rgb(0, 0, 0));
    }

    private uint ResolveBackgroundColor(bool dark)
    {
        if (_viewModel is not null &&
            string.Equals(_viewModel.SelectedBackgroundModeText, "纯色", StringComparison.Ordinal) &&
            TryParseColor(_viewModel.BackgroundColorText, out var color))
        {
            return color;
        }

        return dark ? Rgb(31, 32, 36) : Rgb(247, 247, 248);
    }

    private bool IsDarkTheme()
    {
        var selected = _viewModel?.SelectedThemeText;
        if (string.Equals(selected, "深色", StringComparison.Ordinal))
            return true;
        if (string.Equals(selected, "浅色", StringComparison.Ordinal))
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1) ?? 1) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseColor(string? value, out uint color)
    {
        color = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var text = value.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length == 8)
            text = text[2..];
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return false;
        color = Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static uint Blend(uint background, uint foreground, double foregroundAmount)
    {
        foregroundAmount = Math.Clamp(foregroundAmount, 0, 1);
        var br = (int)(background & 0xFF);
        var bg = (int)((background >> 8) & 0xFF);
        var bb = (int)((background >> 16) & 0xFF);
        var fr = (int)(foreground & 0xFF);
        var fg = (int)((foreground >> 8) & 0xFF);
        var fb = (int)((foreground >> 16) & 0xFF);
        return Rgb(
            (byte)Math.Round(br + (fr - br) * foregroundAmount),
            (byte)Math.Round(bg + (fg - bg) * foregroundAmount),
            (byte)Math.Round(bb + (fb - bb) * foregroundAmount));
    }

    private void ApplyPaletteToNativeWindow()
    {
        if (ListHandle == 0)
            return;

        _palette = BuildPalette();
        var palette = _palette;

        // The old implementation used WS_EX_LAYERED + a chroma key. That made antialiased text
        // retain dark fringe pixels and let SysListView32 expose a black backbuffer after LVM_SETVIEW.
        // Use a normal opaque HWND and paint the cached Avalonia wallpaper ourselves instead.
        DisableLegacyColorKeyLayering();
        SendMessage(ListHandle, LVM_SETEXTENDEDLISTVIEWSTYLE, 0,
            (nint)(LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_LABELTIP));
        SetWindowTheme(ListHandle, IsDarkTheme() ? "DarkMode_Explorer" : "Explorer", null);
        SendMessage(ListHandle, LVM_SETBKCOLOR, 0, (nint)(long)palette.Background);
        SendMessage(ListHandle, LVM_SETTEXTBKCOLOR, 0, (nint)(long)CLR_NONE);
        SendMessage(ListHandle, LVM_SETTEXTCOLOR, 0, (nint)(long)palette.Text);

        SyncBackdrop(force: false);
        if (Handle != 0)
            InvalidateRect(Handle, 0, false);
        InvalidateRect(ListHandle, 0, false);
    }

    private void ConfigureColumns()
    {
        while (SendMessage(ListHandle, LVM_DELETECOLUMN, 0, 0) != 0) { }
        InsertColumn(0, "名称", 800);
        UpdateColumnWidth();
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
            SendMessage(ListHandle, LVM_INSERTCOLUMNW, (nint)index, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(titlePtr);
        }
    }

    private void UpdateColumnWidth()
    {
        if (ListHandle == 0)
            return;
        GetClientRect(ListHandle, out var client);
        SendMessage(ListHandle, LVM_SETCOLUMNWIDTH, 0, (nint)Math.Max(1, client.Width));
    }

    private void ResizeListToHost()
    {
        if (Handle == 0 || ListHandle == 0)
            return;
        GetClientRect(Handle, out var client);
        MoveWindow(ListHandle, 0, 0, Math.Max(1, client.Width), Math.Max(1, client.Height), false);
        UpdateColumnWidth();
    }

    private void RecreateNativeResources()
    {
        DestroyNativeResources();

        _detailsImageList = CreateLayoutImageList(1, ScaleInt(DetailsRowHeight));
        _largeImageList = CreateLayoutImageList(ScaleInt(LargeArtwork), ScaleInt(LargeArtwork));
        _extraImageList = CreateLayoutImageList(ScaleInt(ExtraArtwork), ScaleInt(ExtraArtwork));
        _normalFont = CreateUiFont(13, FW_NORMAL);
        _mediumFont = CreateUiFont(13, NativeFontMedium);
        _smallFont = CreateUiFont(11.5, FW_NORMAL);
    }

    private nint CreateLayoutImageList(int width, int height)
    {
        var list = ImageList_Create(Math.Max(1, width), Math.Max(1, height), ILC_COLOR32 | ILC_MASK, 1, 1);
        if (list == 0)
            return 0;
        var icon = LoadIconW(0, (nint)32512);
        if (icon != 0)
            ImageList_ReplaceIcon(list, -1, icon);
        return list;
    }

    private nint CreateUiFont(double logicalPixels, int weight)
    {
        var height = -Math.Max(9, ScaleInt(logicalPixels));
        // Segoe UI has no CJK glyphs. GDI font linking therefore rendered Latin with Segoe UI and
        // Chinese with a fallback face whose x-height/em metrics were visibly different. Use one
        // Windows UI CJK family for both scripts so Chinese/Latin names have consistent visual size.
        return CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
    }

    private void DestroyNativeResources()
    {
        if (_detailsImageList != 0) ImageList_Destroy(_detailsImageList);
        if (_largeImageList != 0) ImageList_Destroy(_largeImageList);
        if (_extraImageList != 0) ImageList_Destroy(_extraImageList);
        _detailsImageList = _largeImageList = _extraImageList = 0;

        if (_normalFont != 0) DeleteObject(_normalFont);
        if (_mediumFont != 0) DeleteObject(_mediumFont);
        if (_smallFont != 0) DeleteObject(_smallFont);
        _normalFont = _mediumFont = _smallFont = 0;
    }
}
