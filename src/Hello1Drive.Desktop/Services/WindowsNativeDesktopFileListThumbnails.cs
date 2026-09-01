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
    private bool TryDrawThumbnail(nint hdc, DriveItemModel item, RECT dest, int radius)
    {
        if (item.ThumbnailImage is null || _gdiPlusToken == 0)
            return false;

        if (!_thumbnailCache.TryGetValue(item.Id, out var cached) ||
            !ReferenceEquals(cached.Source, item.ThumbnailImage) ||
            !string.Equals(cached.VersionToken, item.VersionToken, StringComparison.Ordinal))
        {
            if (_scrolling)
                return false;
            cached = CreateNativeThumbnail(item);
            if (cached is null)
                return false;
            StoreThumbnail(item.Id, cached);
        }
        else
        {
            TouchThumbnail(cached);
        }

        FillRectColor(hdc, dest, _palette.ThumbnailBackground);
        if (GdipCreateFromHDC(hdc, out var graphics) != 0 || graphics == 0)
            return false;

        nint region = 0;
        try
        {
            GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBilinear);
            region = CreateRoundRectRgn(dest.left, dest.top, dest.right + 1, dest.bottom + 1, radius * 2, radius * 2);
            if (region != 0)
                GdipSetClipHrgn(graphics, region, CombineModeReplace);

            var sourceWidth = Math.Max(1u, cached.Width);
            var sourceHeight = Math.Max(1u, cached.Height);
            var destinationWidth = Math.Max(1, dest.Width);
            var destinationHeight = Math.Max(1, dest.Height);
            var sourceAspect = sourceWidth / (double)sourceHeight;
            var destinationAspect = destinationWidth / (double)destinationHeight;

            int sx = 0;
            int sy = 0;
            int sw = checked((int)sourceWidth);
            int sh = checked((int)sourceHeight);
            if (sourceAspect > destinationAspect)
            {
                sw = Math.Max(1, (int)Math.Round(sourceHeight * destinationAspect));
                sx = Math.Max(0, ((int)sourceWidth - sw) / 2);
            }
            else if (sourceAspect < destinationAspect)
            {
                sh = Math.Max(1, (int)Math.Round(sourceWidth / destinationAspect));
                sy = Math.Max(0, ((int)sourceHeight - sh) / 2);
            }

            return GdipDrawImageRectRectI(
                graphics,
                cached.Image,
                dest.left,
                dest.top,
                destinationWidth,
                destinationHeight,
                sx,
                sy,
                sw,
                sh,
                UnitPixel,
                0,
                0,
                0) == 0;
        }
        finally
        {
            if (region != 0)
                DeleteObject(region);
            GdipDeleteGraphics(graphics);
        }
    }

    private NativeThumbnail? CreateNativeThumbnail(DriveItemModel item)
    {
        var bitmap = item.ThumbnailImage;
        if (bitmap is null)
            return null;

        try
        {
            using var encoded = new MemoryStream();
#pragma warning disable CS0618
            bitmap.Save(encoded);
#pragma warning restore CS0618
            var bytes = encoded.ToArray();
            if (bytes.Length == 0)
                return null;

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

            if (GdipGetImageWidth(image, out var width) != 0 || GdipGetImageHeight(image, out var height) != 0)
            {
                GdipDisposeImage(image);
                ReleaseComStream(stream);
                return null;
            }

            return new NativeThumbnail(bitmap, item.VersionToken, image, width, height, stream);
        }
        catch
        {
            return null;
        }
    }

    private void StoreThumbnail(string itemId, NativeThumbnail thumbnail)
    {
        RemoveThumbnail(itemId);
        thumbnail.LruNode = _thumbnailLru.AddFirst(itemId);
        _thumbnailCache[itemId] = thumbnail;

        while (_thumbnailCache.Count > MaxNativeThumbnailCache && _thumbnailLru.Last is { } last)
            RemoveThumbnail(last.Value);
    }

    private void TouchThumbnail(NativeThumbnail thumbnail)
    {
        if (thumbnail.LruNode is null || thumbnail.LruNode.List is null)
            return;
        _thumbnailLru.Remove(thumbnail.LruNode);
        _thumbnailLru.AddFirst(thumbnail.LruNode);
    }

    private void RemoveThumbnail(string itemId)
    {
        if (!_thumbnailCache.Remove(itemId, out var thumbnail))
            return;
        if (thumbnail.LruNode?.List is not null)
            _thumbnailLru.Remove(thumbnail.LruNode);
        thumbnail.Dispose();
    }

    private void ClearThumbnailCache()
    {
        foreach (var thumbnail in _thumbnailCache.Values)
            thumbnail.Dispose();
        _thumbnailCache.Clear();
        _thumbnailLru.Clear();
    }

    private static void ReleaseComStream(IStream stream)
    {
        try
        {
            if (Marshal.IsComObject(stream))
                Marshal.FinalReleaseComObject(stream);
        }
        catch
        {
            // Best-effort release; the stream is held for the complete GDI+ image lifetime.
        }
    }

}
