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
    private void DrawFolder(nint hdc, RECT dest)
    {
        var width = dest.Width;
        var height = dest.Height;
        var top = dest.top + (int)(height * 0.20);
        FillRoundRect(hdc,
            new RECT(dest.left + (int)(width * 0.04), top, dest.right - (int)(width * 0.04), top + (int)(height * 0.68)),
            Math.Max(2, (int)(width * 0.07)), Rgb(247, 188, 15));
        FillRoundRect(hdc,
            new RECT(dest.left + (int)(width * 0.08), dest.top + (int)(height * 0.10), dest.left + (int)(width * 0.50), dest.top + (int)(height * 0.28)),
            Math.Max(2, (int)(width * 0.05)), Rgb(247, 188, 15));
        FillRoundRect(hdc,
            new RECT(dest.left + (int)(width * 0.14), dest.top + (int)(height * 0.36), dest.right - (int)(width * 0.14), dest.top + (int)(height * 0.66)),
            Math.Max(2, (int)(width * 0.035)), Rgb(255, 242, 214));
        FillRoundRect(hdc,
            new RECT(dest.left + (int)(width * 0.04), dest.top + (int)(height * 0.44), dest.right - (int)(width * 0.04), dest.top + (int)(height * 0.86)),
            Math.Max(2, (int)(width * 0.07)), Rgb(255, 215, 107));
    }

    private void DrawFileBadge(nint hdc, DriveItemModel item, RECT dest, int radius, Palette palette)
    {
        var accent = GetAccent(item);
        var insetX = Math.Max(2, (int)(dest.Width * 0.12));
        var insetY = Math.Max(1, (int)(dest.Height * 0.05));
        var body = new RECT(dest.left + insetX, dest.top + insetY, dest.right - insetX, dest.bottom - insetY);
        var bodyRadius = Math.Max(3, (int)(radius * 0.75));
        FillRoundRect(hdc, body, bodyRadius, palette.FileBody);

        var stripHeight = Math.Max(9, (int)(body.Height * 0.31));
        var strip = new RECT(body.left, body.bottom - stripHeight, body.right, body.bottom);
        FillRoundRect(hdc, strip, bodyRadius, accent);
        var badge = item.IsImage ? "IMG" : item.IsVideo ? "VID" : item.IsAudio ? "MUS" : item.FileBadgeText;
        DrawTextLine(hdc, badge, strip, _smallFont, Rgb(255, 255, 255), center: true);
    }

    private void DrawVideoBadge(nint hdc, RECT dest, Palette palette)
    {
        var diameter = Math.Clamp((int)(Math.Min(dest.Width, dest.Height) * 0.25), ScaleInt(14), ScaleInt(26));
        var badge = new RECT(dest.right - diameter - ScaleInt(2), dest.bottom - diameter - ScaleInt(2), dest.right - ScaleInt(2), dest.bottom - ScaleInt(2));
        FillEllipse(hdc, badge, palette.VideoBadge);

        var cx = (badge.left + badge.right) / 2;
        var cy = (badge.top + badge.bottom) / 2;
        var half = Math.Max(3, diameter / 5);
        var points = new[]
        {
            new POINT { x = cx - half / 2, y = cy - half },
            new POINT { x = cx - half / 2, y = cy + half },
            new POINT { x = cx + half, y = cy }
        };
        FillPolygon(hdc, points, Rgb(255, 255, 255));
    }

    private uint GetAccent(DriveItemModel item)
    {
        if (item.IsImage) return Rgb(59, 130, 246);
        if (item.IsVideo) return Rgb(124, 58, 237);
        if (item.IsAudio) return Rgb(236, 72, 153);
        if (item.IsPdf) return Rgb(239, 68, 68);
        if (item.IsWord) return Rgb(37, 99, 235);
        if (item.IsExcel) return Rgb(22, 163, 74);
        if (item.IsPowerPoint) return Rgb(249, 115, 22);
        if (item.IsArchive) return Rgb(139, 92, 246);
        if (item.IsUrlShortcut) return Rgb(14, 165, 164);
        if (item.IsText) return Rgb(100, 116, 139);
        return Rgb(96, 165, 250);
    }

}
