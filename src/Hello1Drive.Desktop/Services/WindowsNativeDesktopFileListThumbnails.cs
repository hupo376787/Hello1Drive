using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Microsoft.Win32;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private readonly object _nativeThumbnailPreparationLock = new();
    private readonly HashSet<string> _nativeThumbnailPreparations = [];
    private readonly SemaphoreSlim _nativeThumbnailPreparationGate = new(2, 2);
    private bool TryDrawThumbnail(
        nint hdc,
        nint sharedGraphics,
        DriveItemModel item,
        RECT dest,
        int radius)
    {
        if (_gdiPlusToken == 0)
            return false;

        _thumbnailCache.TryGetValue(item.Id, out var cached);
        var version = item.VersionToken;

        if (cached is null || !string.Equals(cached.VersionToken, version, StringComparison.Ordinal))
        {
            // Never encode/decode an Avalonia bitmap synchronously from WM_PAINT. Native thumbnail
            // preparation is disk/cache work and runs on two background workers. Until it is ready,
            // draw the lightweight file badge and repaint only this item when preparation completes.
            if (item.ThumbnailImage is not null)
                QueueNativeThumbnailPreparation(item);
            return false;
        }

        TouchThumbnail(cached);

        var graphics = sharedGraphics;
        var ownsGraphics = false;
        if (graphics == 0)
        {
            if (GdipCreateFromHDC(hdc, out graphics) != 0 || graphics == 0)
                return false;
            ownsGraphics = true;
            GdipSetInterpolationMode(
                graphics,
                _scrolling ? InterpolationModeBilinear : InterpolationModeHighQualityBilinear);
        }

        uint graphicsState = 0;
        var savedGraphics = GdipSaveGraphics(graphics, out graphicsState) == 0;
        if (!savedGraphics && !ownsGraphics)
            return false;

        FillRectColor(hdc, dest, _palette.ThumbnailBackground);
        nint region = 0;
        try
        {
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
            if (savedGraphics)
                GdipRestoreGraphics(graphics, graphicsState);
            if (ownsGraphics)
                GdipDeleteGraphics(graphics);
        }
    }

    private void QueueNativeThumbnailPreparation(DriveItemModel item)
    {
        if (_disposed || _gdiPlusToken == 0 || item.ThumbnailImage is null ||
            string.IsNullOrWhiteSpace(item.Id))
        {
            return;
        }

        if (_thumbnailCache.TryGetValue(item.Id, out var existing) &&
            string.Equals(existing.VersionToken, item.VersionToken, StringComparison.Ordinal))
        {
            return;
        }

        var itemId = item.Id;
        var version = item.VersionToken;
        var cachePath = item.ThumbnailCachePath;
        var bitmap = item.ThumbnailImage;
        var targetMaxDimension = Math.Max(64, ScaleInt(ExtraArtwork));

        lock (_nativeThumbnailPreparationLock)
        {
            if (!_nativeThumbnailPreparations.Add(itemId))
                return;
        }

        _ = Task.Run(async () =>
        {
            NativeThumbnail? prepared = null;
            try
            {
                await _nativeThumbnailPreparationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    prepared = CreateNativeThumbnail(
                        cachePath,
                        bitmap,
                        version,
                        targetMaxDimension);
                }
                finally
                {
                    _nativeThumbnailPreparationGate.Release();
                }
            }
            catch
            {
                prepared?.Dispose();
                prepared = null;
            }
            finally
            {
                lock (_nativeThumbnailPreparationLock)
                    _nativeThumbnailPreparations.Remove(itemId);
            }

            if (prepared is null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || _viewModel is null)
                {
                    prepared.Dispose();
                    return;
                }

                var index = -1;
                for (var i = 0; i < _viewModel.VirtualItems.Count; i++)
                {
                    var current = _viewModel.VirtualItems[i].Item;
                    if (current is not null &&
                        string.Equals(current.Id, itemId, StringComparison.Ordinal) &&
                        string.Equals(current.VersionToken, version, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    prepared.Dispose();
                    return;
                }

                StoreThumbnail(itemId, prepared);
                QueueNativeItemRedraw(index);
            }, DispatcherPriority.Background);
        });
    }

    private NativeThumbnail? CreateNativeThumbnail(
        string? cachePath,
        Bitmap bitmap,
        string versionToken,
        int targetMaxDimension)
    {
        nint sourceImage = 0;
        IStream? sourceStream = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(cachePath) &&
                File.Exists(cachePath) &&
                GdipLoadImageFromFile(cachePath, out sourceImage) == 0 &&
                sourceImage != 0)
            {
                // Fast path: the persistent thumbnail file is already encoded. Avoid the previous
                // Bitmap.Save -> byte[] -> HGLOBAL -> GDI+ decode round-trip on the UI thread.
            }
            else
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

                if (CreateStreamOnHGlobal(hGlobal, true, out sourceStream) != 0 || sourceStream is null)
                {
                    GlobalFree(hGlobal);
                    return null;
                }

                if (GdipLoadImageFromStream(sourceStream, out sourceImage) != 0 || sourceImage == 0)
                    return null;
            }

            if (GdipGetImageWidth(sourceImage, out var sourceWidth) != 0 ||
                GdipGetImageHeight(sourceImage, out var sourceHeight) != 0 ||
                sourceWidth == 0 ||
                sourceHeight == 0)
            {
                return null;
            }

            var scale = Math.Min(1d,
                targetMaxDimension / (double)Math.Max(sourceWidth, sourceHeight));
            var width = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
            var height = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));

            if (GdipGetImageThumbnail(
                    sourceImage,
                    width,
                    height,
                    out var thumbnailImage,
                    0,
                    0) != 0 ||
                thumbnailImage == 0)
            {
                return null;
            }

            return new NativeThumbnail(versionToken, thumbnailImage, width, height);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (sourceImage != 0)
                GdipDisposeImage(sourceImage);
            if (sourceStream is not null)
                ReleaseComStream(sourceStream);
        }
    }

    private void UpdateVisibleNativeThumbnailPins(IEnumerable<DriveItemModel> items)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !item.SupportsThumbnail)
                continue;

            visible.Add(item.Id);

            // If the managed thumbnail is already decoded but its scaled native copy was evicted
            // earlier, rebuild it before hover/selection invalidates the card.
            if (item.ThumbnailImage is not null &&
                (!_thumbnailCache.TryGetValue(item.Id, out var cached) ||
                 !string.Equals(cached.VersionToken, item.VersionToken, StringComparison.Ordinal)))
            {
                QueueNativeThumbnailPreparation(item);
            }
        }

        _visibleNativeThumbnailIds = visible;
    }

    private void StoreThumbnail(string itemId, NativeThumbnail thumbnail)
    {
        RemoveThumbnail(itemId);
        thumbnail.LruNode = _thumbnailLru.AddFirst(itemId);
        _thumbnailCache[itemId] = thumbnail;

        while (_thumbnailCache.Count > MaxNativeThumbnailCache)
        {
            var victim = _thumbnailLru.Last;
            while (victim is not null && _visibleNativeThumbnailIds.Contains(victim.Value))
                victim = victim.Previous;

            // A very dense viewport may temporarily pin more than the nominal cache size. Keep
            // visible pixels stable and let the cache trim again after the viewport changes.
            if (victim is null)
                break;

            RemoveThumbnail(victim.Value);
        }
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
