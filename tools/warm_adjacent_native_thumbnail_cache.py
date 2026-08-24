from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


android = ROOT / "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"
replace_once(
    android,
    '''        // Native memory cache or persistent disk cache already makes this adjacent item warm.
        if ((TryGetBitmap(item, out var bitmap) && bitmap is not null) ||
            AppServices.ThumbnailCache.TryGetCachedPath(item, out _))
        {
            return;
        }''',
    '''        // Keep the adjacent viewport fully warm in the bounded native LRU. A disk-cache hit
        // still needs BitmapFactory decode once, so do that now while the list is idle.
        if (TryGetBitmap(item, out var bitmap) && bitmap is not null)
            return;''',
    "Android prefetch cache check",
)
replace_once(
    android,
    '''                // Prefetch only the encoded file. When the item becomes visible, BitmapFactory
                // decodes from local storage quickly without spending memory on two hidden pages.
                await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);''',
    '''                var path = await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                generationToken.ThrowIfCancellationRequested();
                if (TryGetBitmap(item, out var existing) && existing is not null)
                    return;

                var targetPx = Mode switch
                {
                    FileViewMode.ExtraLargeIcons => 320,
                    FileViewMode.LargeIcons => 256,
                    _ => 128
                };
                var bitmap = await Task.Run(() => DecodeScaled(path, targetPx), generationToken).ConfigureAwait(false);
                if (bitmap is null)
                    return;

                if (generationToken.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    generationToken.ThrowIfCancellationRequested();
                }

                AddBitmapToCache(item, bitmap);''',
    "Android adjacent decode warmup",
)

ios = ROOT / "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs"
replace_once(
    ios,
    '''        if ((TryGetImage(item, out var image) && image is not null) ||
            AppServices.ThumbnailCache.TryGetCachedPath(item, out _))
        {
            return;
        }''',
    '''        // Keep the adjacent viewport in the bounded native UIImage LRU as well as on disk.
        if (TryGetImage(item, out var image) && image is not null)
            return;''',
    "iOS prefetch cache check",
)
replace_once(
    ios,
    '''                await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);''',
    '''                var path = await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                generationToken.ThrowIfCancellationRequested();
                if (TryGetImage(item, out var existing) && existing is not null)
                    return;

                var image = await Task.Run(() => UIImage.FromFile(path), generationToken).ConfigureAwait(false);
                if (image is null)
                    return;

                if (generationToken.IsCancellationRequested)
                {
                    image.Dispose();
                    generationToken.ThrowIfCancellationRequested();
                }

                AddImageToCache(item, image);''',
    "iOS adjacent decode warmup",
)

print("Adjacent Android/iOS viewport thumbnails now warm native decoded LRUs.")
