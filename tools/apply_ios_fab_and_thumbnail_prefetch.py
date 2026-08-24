from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


# -----------------------------------------------------------------------------
# Core: native Android/iOS owns the floating upload button while the native list
# is visible, and both platforms route upload/position events through the host.
# -----------------------------------------------------------------------------
main_view = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"
replace_once(
    main_view,
    '''        // Android's native RecyclerView lives on a platform View layer above Avalonia. Its upload
        // FAB is therefore rendered natively as part of that same layer; keep the Avalonia FAB
        // for desktop/iOS only so RecyclerView cells can never cover the Android button.
        if (OperatingSystem.IsAndroid())
            FloatingActionCanvas.IsVisible = false;''',
    '''        // Android RecyclerView and iOS UICollectionView live on platform-native view layers above
        // Avalonia. Render the upload FAB inside the same native root on both platforms so file
        // cells can never cover it; desktop keeps the Avalonia FAB.
        if (UsesNativeMobileFileList)
            FloatingActionCanvas.IsVisible = false;''',
    "MainView native FAB visibility",
)
replace_once(
    main_view,
    '''    private void NativeMobileFileListHost_FloatingUploadRequested(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsAndroid())
            return;

        Dispatcher.UIThread.Post(
            () => UploadButton_Click(FloatingUploadButton, new RoutedEventArgs()),
            DispatcherPriority.Input);
    }

    private async void NativeMobileFileListHost_FloatingUploadPositionChanged(
        object? sender,
        NativeFloatingUploadPositionEventArgs e)
    {
        if (!OperatingSystem.IsAndroid() || DataContext is not MainViewModel vm)
            return;

        await vm.SaveFloatingUploadPositionAsync(e.NormalizedX, e.NormalizedY);
    }''',
    '''    private void NativeMobileFileListHost_FloatingUploadRequested(object? sender, EventArgs e)
    {
        if (!UsesNativeMobileFileList)
            return;

        Dispatcher.UIThread.Post(
            () => UploadButton_Click(FloatingUploadButton, new RoutedEventArgs()),
            DispatcherPriority.Input);
    }

    private async void NativeMobileFileListHost_FloatingUploadPositionChanged(
        object? sender,
        NativeFloatingUploadPositionEventArgs e)
    {
        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)
            return;

        await vm.SaveFloatingUploadPositionAsync(e.NormalizedX, e.NormalizedY);
    }''',
    "MainView native FAB event routing",
)


# -----------------------------------------------------------------------------
# Android: current viewport remains highest priority. Once idle, prefetch one
# viewport before and one viewport after into the persistent thumbnail cache.
# The same four-worker gate is shared with visible decode/download work, so a
# folder with thousands of files never creates an unbounded network workload.
# -----------------------------------------------------------------------------
android = ROOT / "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"
replace_once(
    android,
    '''    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingIds = new(StringComparer.Ordinal);''',
    '''    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _prefetchingIds = new(StringComparer.Ordinal);''',
    "Android prefetch dictionary",
)
replace_once(
    android,
    '''    public void StartVisibleThumbnailWork()
    {
        if (_disposed || _scrolling || _viewModel is null || ItemCount == 0)
            return;

        var first = Math.Clamp(_visibleFirst, 0, ItemCount - 1);
        var last = Math.Clamp(_visibleLast, first, ItemCount - 1);
        for (var position = first; position <= last; position++)
        {
            if (_recycler.FindViewHolderForAdapterPosition(position) is NativeFileViewHolder holder)
                RequestThumbnailIfNeeded(holder, position);
        }
    }''',
    '''    public void StartVisibleThumbnailWork()
    {
        if (_disposed || _scrolling || _viewModel is null || ItemCount == 0)
            return;

        CaptureVisibleRangeFromLayout();
        var first = Math.Clamp(_visibleFirst, 0, ItemCount - 1);
        var last = Math.Clamp(_visibleLast, first, ItemCount - 1);

        // Visible holders always win: queue them before any look-ahead work.
        for (var position = first; position <= last; position++)
        {
            if (_recycler.FindViewHolderForAdapterPosition(position) is NativeFileViewHolder holder)
                RequestThumbnailIfNeeded(holder, position);
        }

        // A "page" means one current viewport, not one Graph 200-item metadata page. This keeps
        // work proportional to the screen size while making the previous/next viewport warm.
        var pageSize = Math.Max(1, last - first + 1);
        for (var distance = 1; distance <= pageSize; distance++)
        {
            PrefetchThumbnailIfNeeded(last + distance);
            PrefetchThumbnailIfNeeded(first - distance);
        }
    }

    private void CaptureVisibleRangeFromLayout()
    {
        if (_recycler.GetLayoutManager() is not LinearLayoutManager layout)
            return;

        var first = layout.FindFirstVisibleItemPosition();
        var last = layout.FindLastVisibleItemPosition();
        if (first >= 0 && last >= first)
            UpdateVisibleRange(first, last);
    }''',
    "Android three-page thumbnail window",
)
replace_once(
    android,
    '''        _recycler.Post(() =>
        {
            if (!_disposed)
                NotifyDataSetChanged();
        });''',
    '''        _recycler.Post(() =>
        {
            if (_disposed)
                return;

            NotifyDataSetChanged();
            _recycler.PostDelayed(() =>
            {
                if (!_disposed && !_scrolling)
                    StartVisibleThumbnailWork();
            }, 48);
        });''',
    "Android prefetch after metadata collection update",
)
replace_once(
    android,
    '''    private void RequestThumbnailIfNeeded(NativeFileViewHolder holder, int position)
    {''',
    '''    private void PrefetchThumbnailIfNeeded(int position)
    {
        if (_disposed || _scrolling || _viewModel is null || position < 0 || position >= ItemCount)
            return;

        var item = _viewModel.MobileItems[position].Item;
        if (item is null || !item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return;

        // Native memory cache or persistent disk cache already makes this adjacent item warm.
        if ((TryGetBitmap(item, out var bitmap) && bitmap is not null) ||
            AppServices.ThumbnailCache.TryGetCachedPath(item, out _))
        {
            return;
        }

        if (!_prefetchingIds.TryAdd(item.Id, 0))
            return;

        var generationToken = _thumbnailGenerationCts.Token;
        _ = PrefetchThumbnailAsync(item, generationToken);
    }

    private async Task PrefetchThumbnailAsync(DriveItemModel item, CancellationToken generationToken)
    {
        try
        {
            await _thumbnailGate.WaitAsync(generationToken).ConfigureAwait(false);
            try
            {
                generationToken.ThrowIfCancellationRequested();
                if (_scrolling)
                    return;

                // Prefetch only the encoded file. When the item becomes visible, BitmapFactory
                // decodes from local storage quickly without spending memory on two hidden pages.
                await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (System.OperationCanceledException)
        {
            // Normal when a new fling/folder/view mode invalidates this look-ahead window.
        }
        catch
        {
            // Adjacent prefetch is best-effort and must never affect the visible file list.
        }
        finally
        {
            _prefetchingIds.TryRemove(item.Id, out _);
        }
    }

    private void RequestThumbnailIfNeeded(NativeFileViewHolder holder, int position)
    {''',
    "Android adjacent thumbnail prefetch methods",
)
replace_once(
    android,
    '''        previous.Cancel();
        previous.Dispose();
        _loadingIds.Clear();
    }''',
    '''        previous.Cancel();
        previous.Dispose();
        _loadingIds.Clear();
        _prefetchingIds.Clear();
    }''',
    "Android cancel adjacent prefetch",
)


# -----------------------------------------------------------------------------
# iOS: host UICollectionView and native FAB inside one UIKit root. The FAB is a
# sibling above UICollectionView, supports tap + drag, and persists the same
# normalized position as Android/desktop. Also add the same 3-viewport thumbnail
# policy used by Android.
# -----------------------------------------------------------------------------
ios = ROOT / "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs"
replace_once(
    ios,
    '''    private readonly NativeMobileFileListHost _host;
    private readonly UICollectionViewFlowLayout _layout;
    private readonly NativeCollectionView _collection;
    private readonly UIRefreshControl _refresh;
    private readonly IosNativeFileCollectionSource _source;
    private readonly UILongPressGestureRecognizer _longPress;''',
    '''    private readonly NativeMobileFileListHost _host;
    private readonly IosNativeFileRootView _root;
    private readonly UICollectionViewFlowLayout _layout;
    private readonly NativeCollectionView _collection;
    private readonly IosNativeFloatingUploadButtonView _floatingUpload;
    private readonly UIRefreshControl _refresh;
    private readonly IosNativeFileCollectionSource _source;
    private readonly UILongPressGestureRecognizer _longPress;''',
    "iOS root/FAB fields",
)
replace_once(
    ios,
    '''    public IosNativeFileListController(NativeMobileFileListHost host)
    {
        _host = host;
        _layout = new UICollectionViewFlowLayout''',
    '''    public IosNativeFileListController(NativeMobileFileListHost host)
    {
        _host = host;
        _root = new IosNativeFileRootView();
        _layout = new UICollectionViewFlowLayout''',
    "iOS root construction",
)
replace_once(
    ios,
    '''        _refresh = new UIRefreshControl();''',
    '''        _floatingUpload = new IosNativeFloatingUploadButtonView(host);

        _refresh = new UIRefreshControl();''',
    "iOS FAB construction",
)
replace_once(
    ios,
    '''        _collection.AddGestureRecognizer(_longPress);
        _collection.NativeLayoutChanged += Collection_NativeLayoutChanged;

        _host.HostStateChanged += Host_HostStateChanged;''',
    '''        _collection.AddGestureRecognizer(_longPress);
        _collection.NativeLayoutChanged += Collection_NativeLayoutChanged;

        _root.AddSubview(_collection);
        _root.AddSubview(_floatingUpload);
        _root.NativeLayoutChanged += Root_NativeLayoutChanged;

        _host.HostStateChanged += Host_HostStateChanged;''',
    "iOS native root hierarchy",
)
replace_once(
    ios,
    '''    public UIView RootView => _collection;''',
    '''    public UIView RootView => _root;''',
    "iOS root view",
)
replace_once(
    ios,
    '''        _source.UpdateSelection(_host.SelectedIds, _host.SelectionMode);
        UpdateTheme();

        if (_viewModel is not null)''',
    '''        _source.UpdateSelection(_host.SelectedIds, _host.SelectionMode);
        UpdateTheme();
        SyncFloatingUpload();

        if (_viewModel is not null)''',
    "iOS sync FAB state",
)
replace_once(
    ios,
    '''        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or''',
    '''        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))
        {
            SyncFloatingUpload();
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or''',
    "iOS FAB settings reaction",
)
replace_once(
    ios,
    '''    private void ApplyLayout(FileViewMode mode, bool preservePosition)
    {''',
    '''    private void Root_NativeLayoutChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        _collection.Frame = _root.Bounds;
        PositionFloatingUpload();
    }

    private void SyncFloatingUpload()
    {
        if (_disposed)
            return;

        _floatingUpload.Hidden = !_host.FloatingUploadVisible;
        if (!_floatingUpload.Hidden)
            PositionFloatingUpload();
    }

    private void PositionFloatingUpload()
    {
        if (_disposed || _floatingUpload.Hidden)
            return;

        const double size = 48d;
        var maxX = Math.Max(0d, (double)_root.Bounds.Width - size);
        var maxY = Math.Max(0d, (double)_root.Bounds.Height - size);
        var x = Math.Clamp(_host.FloatingUploadX, 0d, 1d) * maxX;
        var y = Math.Clamp(_host.FloatingUploadY, 0d, 1d) * maxY;
        _floatingUpload.Frame = new CGRect((nfloat)x, (nfloat)y, (nfloat)size, (nfloat)size);
        _root.BringSubviewToFront(_floatingUpload);
    }

    private void ApplyLayout(FileViewMode mode, bool preservePosition)
    {''',
    "iOS root/FAB positioning methods",
)
replace_once(
    ios,
    '''        _host.HostStateChanged -= Host_HostStateChanged;
        _host.ScrollToPositionRequested -= Host_ScrollToPositionRequested;
        _refresh.ValueChanged -= Refresh_ValueChanged;
        _collection.NativeLayoutChanged -= Collection_NativeLayoutChanged;''',
    '''        _host.HostStateChanged -= Host_HostStateChanged;
        _host.ScrollToPositionRequested -= Host_ScrollToPositionRequested;
        _refresh.ValueChanged -= Refresh_ValueChanged;
        _collection.NativeLayoutChanged -= Collection_NativeLayoutChanged;
        _root.NativeLayoutChanged -= Root_NativeLayoutChanged;''',
    "iOS root event cleanup",
)
replace_once(
    ios,
    '''        _collection.RemoveGestureRecognizer(_longPress);
        _collection.Source = null;
        _collection.RefreshControl = null;
        _source.Dispose();
        _longPress.Dispose();
        _refresh.Dispose();
        _collection.Dispose();
        _layout.Dispose();
        base.Dispose();''',
    '''        _collection.RemoveGestureRecognizer(_longPress);
        _collection.Source = null;
        _collection.RefreshControl = null;
        _floatingUpload.RemoveFromSuperview();
        _collection.RemoveFromSuperview();
        _source.Dispose();
        _longPress.Dispose();
        _refresh.Dispose();
        _floatingUpload.Dispose();
        _collection.Dispose();
        _root.Dispose();
        _layout.Dispose();
        base.Dispose();''',
    "iOS root/FAB disposal",
)
replace_once(
    ios,
    '''internal sealed class NativeCollectionView : UICollectionView
{''',
    '''internal sealed class IosNativeFileRootView : UIView
{
    private CGSize _lastSize;

    public IosNativeFileRootView()
    {
        BackgroundColor = UIColor.Clear;
        Opaque = false;
    }

    public event EventHandler? NativeLayoutChanged;

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var size = Bounds.Size;
        if (Math.Abs((double)(size.Width - _lastSize.Width)) < 0.5 &&
            Math.Abs((double)(size.Height - _lastSize.Height)) < 0.5)
        {
            return;
        }

        _lastSize = size;
        NativeLayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class IosNativeFloatingUploadButtonView : UIView
{
    private readonly NativeMobileFileListHost _host;
    private readonly UITapGestureRecognizer _tap;
    private readonly UIPanGestureRecognizer _pan;
    private CGPoint _panStartOrigin;

    public IosNativeFloatingUploadButtonView(NativeMobileFileListHost host)
    {
        _host = host;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        UserInteractionEnabled = true;
        MultipleTouchEnabled = false;
        AccessibilityLabel = "上传文件";

        _tap = new UITapGestureRecognizer(HandleTap);
        _pan = new UIPanGestureRecognizer(HandlePan);
        _tap.RequireGestureRecognizerToFail(_pan);
        AddGestureRecognizer(_tap);
        AddGestureRecognizer(_pan);
    }

    public override void Draw(CGRect rect)
    {
        base.Draw(rect);
        var width = (double)Bounds.Width;
        var height = (double)Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var diameter = Math.Min(width, height);
        var circleRect = new CGRect(
            (nfloat)((width - diameter) / 2d),
            (nfloat)((height - diameter) / 2d),
            (nfloat)diameter,
            (nfloat)diameter);
        UIColor.FromRGB(253, 111, 113).SetFill();
        using (var circle = UIBezierPath.FromOval(circleRect))
            circle.Fill();

        var scale = diameter / 14d;
        var offsetX = (width - 14d * scale) / 2d;
        var offsetY = (height - 14d * scale) / 2d;
        double X(double value) => offsetX + value * scale;
        double Y(double value) => offsetY + value * scale;

        using var path = new UIBezierPath
        {
            LineWidth = (nfloat)Math.Max(1d, 1.5d * scale / 3.4d),
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        };
        path.MoveTo(new CGPoint((nfloat)X(7), (nfloat)Y(12)));
        path.AddLineTo(new CGPoint((nfloat)X(7), (nfloat)Y(2)));
        path.MoveTo(new CGPoint((nfloat)X(3.5), (nfloat)Y(5.5)));
        path.AddLineTo(new CGPoint((nfloat)X(7), (nfloat)Y(2)));
        path.AddLineTo(new CGPoint((nfloat)X(10.5), (nfloat)Y(5.5)));
        path.MoveTo(new CGPoint((nfloat)X(2), (nfloat)Y(12)));
        path.AddLineTo(new CGPoint((nfloat)X(12), (nfloat)Y(12)));
        UIColor.FromRGB(255, 247, 248).SetStroke();
        path.Stroke();
    }

    private void HandleTap(UITapGestureRecognizer recognizer)
    {
        if (recognizer.State == UIGestureRecognizerState.Ended)
            _host.RaiseFloatingUploadRequested();
    }

    private void HandlePan(UIPanGestureRecognizer recognizer)
    {
        if (Superview is not UIView parent)
            return;

        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began:
                _panStartOrigin = Frame.Location;
                parent.BringSubviewToFront(this);
                break;

            case UIGestureRecognizerState.Changed:
            {
                var translation = recognizer.TranslationInView(parent);
                var maxX = Math.Max(0d, (double)parent.Bounds.Width - (double)Frame.Width);
                var maxY = Math.Max(0d, (double)parent.Bounds.Height - (double)Frame.Height);
                var x = Math.Clamp((double)_panStartOrigin.X + (double)translation.X, 0d, maxX);
                var y = Math.Clamp((double)_panStartOrigin.Y + (double)translation.Y, 0d, maxY);
                Frame = new CGRect((nfloat)x, (nfloat)y, Frame.Width, Frame.Height);
                break;
            }

            case UIGestureRecognizerState.Ended:
                SaveNormalizedPosition(parent);
                break;
        }
    }

    private void SaveNormalizedPosition(UIView parent)
    {
        var maxX = Math.Max(1d, (double)parent.Bounds.Width - (double)Frame.Width);
        var maxY = Math.Max(1d, (double)parent.Bounds.Height - (double)Frame.Height);
        _host.RaiseFloatingUploadPositionChanged(
            Math.Clamp((double)Frame.X / maxX, 0d, 1d),
            Math.Clamp((double)Frame.Y / maxY, 0d, 1d));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RemoveGestureRecognizer(_tap);
            RemoveGestureRecognizer(_pan);
            _tap.Dispose();
            _pan.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class NativeCollectionView : UICollectionView
{''',
    "iOS native root/FAB classes",
)

replace_once(
    ios,
    '''    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingIds = new(StringComparer.Ordinal);''',
    '''    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _prefetchingIds = new(StringComparer.Ordinal);''',
    "iOS prefetch dictionary",
)
replace_once(
    ios,
    '''    public void StartVisibleThumbnailWork()
    {
        if (_disposed || _scrolling || _viewModel is null || ItemCount == 0)
            return;

        var paths = _collection.IndexPathsForVisibleItems;
        if (paths is null)
            return;

        foreach (var path in paths.OrderBy(static p => p.Item))
        {
            var cell = _collection.CellForItem(path);
            if (cell is null || !_presenters.TryGetValue(cell.Handle, out var presenter))
                continue;
            RequestThumbnailIfNeeded(presenter, (int)path.Item);
        }
    }''',
    '''    public void StartVisibleThumbnailWork()
    {
        if (_disposed || _scrolling || _viewModel is null || ItemCount == 0)
            return;

        var paths = _collection.IndexPathsForVisibleItems;
        if (paths is null || paths.Length == 0)
            return;

        // Visible cells are queued first so look-ahead work can never delay what is on screen.
        foreach (var path in paths.OrderBy(static p => p.Item))
        {
            var cell = _collection.CellForItem(path);
            if (cell is null || !_presenters.TryGetValue(cell.Handle, out var presenter))
                continue;
            RequestThumbnailIfNeeded(presenter, (int)path.Item);
        }

        var (first, last) = GetVisibleRange();
        var pageSize = Math.Max(1, last - first + 1);
        for (var distance = 1; distance <= pageSize; distance++)
        {
            PrefetchThumbnailIfNeeded(last + distance);
            PrefetchThumbnailIfNeeded(first - distance);
        }
    }''',
    "iOS three-page thumbnail window",
)
replace_once(
    ios,
    '''        _collection.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed)
                UIView.PerformWithoutAnimation(() => _collection.ReloadData());
        });''',
    '''        _collection.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
                return;

            UIView.PerformWithoutAnimation(() => _collection.ReloadData());
            _collection.LayoutIfNeeded();
            if (!_scrolling)
                StartVisibleThumbnailWork();
        });''',
    "iOS prefetch after metadata collection update",
)
replace_once(
    ios,
    '''    private void RequestThumbnailIfNeeded(IosNativeFileCellPresenter presenter, int position)
    {''',
    '''    private void PrefetchThumbnailIfNeeded(int position)
    {
        if (_disposed || _scrolling || _viewModel is null || position < 0 || position >= ItemCount)
            return;

        var item = _viewModel.MobileItems[position].Item;
        if (item is null || !item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return;

        if ((TryGetImage(item, out var image) && image is not null) ||
            AppServices.ThumbnailCache.TryGetCachedPath(item, out _))
        {
            return;
        }

        if (!_prefetchingIds.TryAdd(item.Id, 0))
            return;

        var generationToken = _thumbnailGenerationCts.Token;
        _ = PrefetchThumbnailAsync(item, generationToken);
    }

    private async Task PrefetchThumbnailAsync(DriveItemModel item, CancellationToken generationToken)
    {
        try
        {
            await _thumbnailGate.WaitAsync(generationToken).ConfigureAwait(false);
            try
            {
                generationToken.ThrowIfCancellationRequested();
                if (_scrolling)
                    return;

                await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (System.OperationCanceledException)
        {
            // A new drag/deceleration/folder/view mode superseded this adjacent window.
        }
        catch
        {
            // Adjacent prefetch is cosmetic and must not affect UICollectionView scrolling.
        }
        finally
        {
            _prefetchingIds.TryRemove(item.Id, out _);
        }
    }

    private void RequestThumbnailIfNeeded(IosNativeFileCellPresenter presenter, int position)
    {''',
    "iOS adjacent thumbnail prefetch methods",
)
replace_once(
    ios,
    '''        previous.Cancel();
        previous.Dispose();
        _loadingIds.Clear();
    }''',
    '''        previous.Cancel();
        previous.Dispose();
        _loadingIds.Clear();
        _prefetchingIds.Clear();
    }''',
    "iOS cancel adjacent prefetch",
)

print("Applied iOS native FAB and Android/iOS three-viewport thumbnail prefetch.")
