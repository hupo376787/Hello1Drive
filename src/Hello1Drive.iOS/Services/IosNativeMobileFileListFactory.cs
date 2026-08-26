using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using CoreGraphics;
using Foundation;
using UIKit;
using Avalonia.iOS;
using Avalonia.Platform;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.iOS.Services;

/// <summary>
/// iOS high-performance file surface. The scrolling hot path is UIKit-native:
/// UICollectionView + cell reuse + native UIImage thumbnail decode/cache.
/// </summary>
internal sealed class IosNativeMobileFileListFactory : INativeMobileFileListFactory
{
    private readonly ConcurrentDictionary<nint, IosNativeFileListController> _controllers = new();

    public IPlatformHandle CreateControl(IPlatformHandle parent, NativeMobileFileListHost host)
    {
        var controller = new IosNativeFileListController(host);
        var handle = new UIViewControlHandle(controller.RootView);
        _controllers[handle.Handle] = controller;
        return handle;
    }

    public void DestroyControl(IPlatformHandle control)
    {
        if (_controllers.TryRemove(control.Handle, out var controller))
            controller.Dispose();
    }
}

internal sealed class IosNativeFileListController : NSObject, IDisposable
{
    private readonly NativeMobileFileListHost _host;
    private readonly IosNativeFileRootView _root;
    private readonly UICollectionViewFlowLayout _layout;
    private readonly NativeCollectionView _collection;
    private readonly IosNativeFloatingUploadButtonView _floatingUpload;
    private readonly UIRefreshControl _refresh;
    private readonly IosNativeFileCollectionSource _source;
    private readonly UILongPressGestureRecognizer _longPress;
    private MainViewModel? _viewModel;
    private FileViewMode _lastMode = (FileViewMode)(-1);
    private nfloat _lastWidth;
    private bool _disposed;

    public IosNativeFileListController(NativeMobileFileListHost host)
    {
        _host = host;
        _root = new IosNativeFileRootView();
        _layout = new UICollectionViewFlowLayout
        {
            ScrollDirection = UICollectionViewScrollDirection.Vertical,
            MinimumLineSpacing = 0,
            MinimumInteritemSpacing = 0,
            SectionInset = UIEdgeInsets.Zero
        };

        _collection = new NativeCollectionView(CGRect.Empty, _layout)
        {
            AlwaysBounceVertical = true,
            ShowsVerticalScrollIndicator = true,
            AllowsSelection = true,
            AllowsMultipleSelection = false,
            BackgroundColor = UIColor.Clear,
            Opaque = false,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };

        _floatingUpload = new IosNativeFloatingUploadButtonView(host);

        _refresh = new UIRefreshControl();
        _refresh.ValueChanged += Refresh_ValueChanged;
        _collection.RefreshControl = _refresh;

        _source = new IosNativeFileCollectionSource(_collection, host);
        _collection.Source = _source;
        _collection.RegisterClassForCell(typeof(UICollectionViewCell), IosNativeFileCollectionSource.ReuseIdentifier);

        _longPress = new UILongPressGestureRecognizer(HandleLongPress)
        {
            MinimumPressDuration = 0.42
        };
        _collection.AddGestureRecognizer(_longPress);
        _collection.NativeLayoutChanged += Collection_NativeLayoutChanged;

        _root.AddSubview(_collection);
        _root.AddSubview(_floatingUpload);
        _root.NativeLayoutChanged += Root_NativeLayoutChanged;

        _host.HostStateChanged += Host_HostStateChanged;
        _host.ScrollToPositionRequested += Host_ScrollToPositionRequested;

        SyncHostState(preservePosition: false);
    }

    public UIView RootView => _root;

    private void Host_HostStateChanged(object? sender, EventArgs e) => SyncHostState(preservePosition: true);

    private void Host_ScrollToPositionRequested(object? sender, NativeMobileFileScrollToEventArgs e)
    {
        if (_disposed || _source.ItemCount == 0)
            return;

        var position = Math.Clamp(e.Position, 0, _source.ItemCount - 1);
        var indexPath = NSIndexPath.FromItemSection(position, 0);
        _collection.ScrollToItem(indexPath, UICollectionViewScrollPosition.Top, animated: false);
        _source.ReportScrollState(false);
    }

    private void Collection_NativeLayoutChanged(object? sender, EventArgs e)
    {
        if (_disposed || _viewModel is null)
            return;
        ApplyLayout(_viewModel.ViewMode, preservePosition: true);
    }

    private void SyncHostState(bool preservePosition)
    {
        if (_disposed)
            return;

        var vm = _host.ViewModel;
        if (!ReferenceEquals(_viewModel, vm))
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _viewModel = vm;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;

            _source.Attach(_viewModel);
        }

        _source.UpdateSelection(_host.SelectedIds, _host.SelectionMode);
        UpdateTheme();
        SyncFloatingUpload();

        if (_viewModel is not null)
            ApplyLayout(_viewModel.ViewMode, preservePosition);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _viewModel is null)
            return;

        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            ApplyLayout(_viewModel.ViewMode, preservePosition: true);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.MobileItems))
        {
            _collection.BeginInvokeOnMainThread(() =>
            {
                if (_disposed || _scrolling)
                    return;
                _collection.LayoutIfNeeded();
                _source.StartVisibleThumbnailWork();
            });
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))
        {
            SyncFloatingUpload();
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or
            nameof(MainViewModel.SelectedBackgroundModeText) or
            nameof(MainViewModel.TransparentFileItemBackground))
        {
            UpdateTheme();
        }
    }

    private void Root_NativeLayoutChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        _collection.Frame = _root.Bounds;
        PositionFloatingUpload();
        _collection.BeginInvokeOnMainThread(() =>
        {
            if (_disposed || _scrolling)
                return;
            _collection.LayoutIfNeeded();
            _source.StartVisibleThumbnailWork();
        });
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
    {
        var width = Math.Max(1d, (double)_collection.Bounds.Width);
        if (_lastMode == mode && Math.Abs((double)_lastWidth - width) < 0.5)
            return;

        var first = preservePosition ? _source.FirstVisibleIndex : 0;
        _lastMode = mode;
        _lastWidth = (nfloat)width;
        _source.SetMode(mode);

        switch (mode)
        {
            case FileViewMode.LargeIcons:
                ConfigureGridLayout(width, minCellWidth: 108, cellHeight: 146, spacing: 4);
                break;
            case FileViewMode.ExtraLargeIcons:
                ConfigureGridLayout(width, minCellWidth: 150, cellHeight: 188, spacing: 4);
                break;
            default:
                _layout.MinimumInteritemSpacing = 0;
                _layout.MinimumLineSpacing = 0;
                _layout.SectionInset = UIEdgeInsets.Zero;
                _layout.ItemSize = new CGSize(width, 46);
                break;
        }

        _layout.InvalidateLayout();
        _collection.ReloadData();

        if (preservePosition && first > 0 && _source.ItemCount > 0)
        {
            var target = Math.Clamp(first, 0, _source.ItemCount - 1);
            _collection.LayoutIfNeeded();
            _collection.ScrollToItem(NSIndexPath.FromItemSection(target, 0), UICollectionViewScrollPosition.Top, animated: false);
        }
    }

    private void ConfigureGridLayout(double width, double minCellWidth, double cellHeight, double spacing)
    {
        var span = Math.Max(1, (int)Math.Floor((width + spacing) / (minCellWidth + spacing)));
        var totalSpacing = spacing * Math.Max(0, span - 1);
        var cellWidth = Math.Max(1d, (width - totalSpacing) / span);
        _layout.MinimumInteritemSpacing = (nfloat)spacing;
        _layout.MinimumLineSpacing = (nfloat)spacing;
        _layout.SectionInset = UIEdgeInsets.Zero;
        _layout.ItemSize = new CGSize(cellWidth, cellHeight);
    }

    private void UpdateTheme()
    {
        var dark = IsDarkTheme();
        var transparent = _viewModel?.TransparentFileItemBackground == true;
        var background = dark ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250);
        _collection.BackgroundColor = transparent ? UIColor.Clear : background;
        _collection.Opaque = !transparent;
        _refresh.TintColor = dark ? UIColor.White : UIColor.DarkGray;
        _source.SetPresentation(dark, transparent);
    }

    private bool IsDarkTheme()
    {
        if (_viewModel?.SelectedThemeText == "深色")
            return true;
        if (_viewModel?.SelectedThemeText == "浅色")
            return false;
        return _collection.TraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
    }

    private async void Refresh_ValueChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            await _host.RaiseRefreshRequestedAsync();
        }
        catch (System.OperationCanceledException)
        {
            // Navigation or a newer refresh superseded this one.
        }
        finally
        {
            if (!_disposed)
                _collection.BeginInvokeOnMainThread(() => _refresh.EndRefreshing());
        }
    }

    private void HandleLongPress(UILongPressGestureRecognizer recognizer)
    {
        if (_disposed || recognizer.State != UIGestureRecognizerState.Began)
            return;

        var point = recognizer.LocationInView(_collection);
        var indexPath = _collection.IndexPathForItemAtPoint(point);
        if (indexPath is null)
            return;

        var item = _source.GetItem((int)indexPath.Item);
        if (item is null)
            return;

        var feedback = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Light);
        feedback.Prepare();
        feedback.ImpactOccurred();
        feedback.Dispose();
        _host.RaiseItemLongPressed(item);
    }

    public new void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _host.HostStateChanged -= Host_HostStateChanged;
        _host.ScrollToPositionRequested -= Host_ScrollToPositionRequested;
        _refresh.ValueChanged -= Refresh_ValueChanged;
        _collection.NativeLayoutChanged -= Collection_NativeLayoutChanged;
        _root.NativeLayoutChanged -= Root_NativeLayoutChanged;

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;

        _collection.RemoveGestureRecognizer(_longPress);
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
        base.Dispose();
    }
}

internal sealed class IosNativeFileRootView : UIView
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

        var iconSize = diameter * 0.46d;
        var scale = iconSize / 18d;
        var offsetX = (width - iconSize) / 2d;
        var offsetY = (height - iconSize) / 2d - diameter * 0.012d;
        double X(double value) => offsetX + value * scale;
        double Y(double value) => offsetY + value * scale;

        using var path = new UIBezierPath
        {
            LineWidth = (nfloat)1.55d,
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        };

        // Use short cubic cloud segments so the iOS native FAB matches the Avalonia/Android glyph.
        path.MoveTo(new CGPoint((nfloat)X(4.2), (nfloat)Y(13.1)));
        path.AddCurveToPoint(new CGPoint((nfloat)X(0.9), (nfloat)Y(10.0)),
            new CGPoint((nfloat)X(2.2), (nfloat)Y(13.1)), new CGPoint((nfloat)X(0.9), (nfloat)Y(11.8)));
        path.AddCurveToPoint(new CGPoint((nfloat)X(3.5), (nfloat)Y(6.6)),
            new CGPoint((nfloat)X(0.9), (nfloat)Y(8.4)), new CGPoint((nfloat)X(2.0), (nfloat)Y(7.0)));
        path.AddCurveToPoint(new CGPoint((nfloat)X(8.5), (nfloat)Y(2.5)),
            new CGPoint((nfloat)X(4.0), (nfloat)Y(4.2)), new CGPoint((nfloat)X(6.0), (nfloat)Y(2.5)));
        path.AddCurveToPoint(new CGPoint((nfloat)X(13.5), (nfloat)Y(6.0)),
            new CGPoint((nfloat)X(10.8), (nfloat)Y(2.5)), new CGPoint((nfloat)X(12.8), (nfloat)Y(3.9)));
        path.AddCurveToPoint(new CGPoint((nfloat)X(17.1), (nfloat)Y(9.8)),
            new CGPoint((nfloat)X(15.6), (nfloat)Y(6.2)), new CGPoint((nfloat)X(17.1), (nfloat)Y(7.8)));
        path.AddCurveToPoint(new CGPoint((nfloat)X(13.7), (nfloat)Y(13.1)),
            new CGPoint((nfloat)X(17.1), (nfloat)Y(11.8)), new CGPoint((nfloat)X(15.7), (nfloat)Y(13.1)));
        path.AddLineTo(new CGPoint((nfloat)X(4.2), (nfloat)Y(13.1)));
        path.MoveTo(new CGPoint((nfloat)X(9.0), (nfloat)Y(13.9)));
        path.AddLineTo(new CGPoint((nfloat)X(9.0), (nfloat)Y(7.2)));
        path.MoveTo(new CGPoint((nfloat)X(6.7), (nfloat)Y(9.5)));
        path.AddLineTo(new CGPoint((nfloat)X(9.0), (nfloat)Y(7.2)));
        path.AddLineTo(new CGPoint((nfloat)X(11.3), (nfloat)Y(9.5)));
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
{
    private CGSize _lastSize;

    public NativeCollectionView(CGRect frame, UICollectionViewLayout layout)
        : base(frame, layout)
    {
    }

    public event EventHandler? NativeLayoutChanged;

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var size = Bounds.Size;
        if (Math.Abs((double)(size.Width - _lastSize.Width)) < 0.5 &&
            Math.Abs((double)(size.Height - _lastSize.Height)) < 0.5)
            return;

        _lastSize = size;
        NativeLayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class IosNativeFileCollectionSource : UICollectionViewSource
{
    public static readonly NSString ReuseIdentifier = new("Hello1DriveNativeFileCell");

    private readonly UICollectionView _collection;
    private readonly NativeMobileFileListHost _host;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _prefetchingIds = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, IosNativeFileCellPresenter> _presenters = [];
    private readonly object _imageCacheGate = new();
    private readonly Dictionary<string, UIImage> _imageCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _imageLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _imageLruNodes = new(StringComparer.Ordinal);
    private CancellationTokenSource _thumbnailGenerationCts = new();
    private MainViewModel? _viewModel;
    private bool _scrolling;
    private bool _darkTheme;
    private bool _transparentBackground;
    private bool _selectionMode;
    private HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private FileViewMode _mode = FileViewMode.Details;
    private bool _disposed;

    private const int ImageCacheLimit = 96;

    public IosNativeFileCollectionSource(UICollectionView collection, NativeMobileFileListHost host)
    {
        _collection = collection;
        _host = host;
    }

    public int ItemCount => _viewModel?.MobileItems.Count ?? 0;
    public FileViewMode Mode => _mode;

    public int FirstVisibleIndex => GetVisibleRange().first;

    public void Attach(MainViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is not null)
            _viewModel.MobileItems.CollectionChanged -= MobileItems_CollectionChanged;

        CancelThumbnailGeneration();
        _viewModel = viewModel;

        if (_viewModel is not null)
            _viewModel.MobileItems.CollectionChanged += MobileItems_CollectionChanged;

        _collection.ReloadData();
    }

    public void SetMode(FileViewMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        CancelThumbnailGeneration();
        RebindVisible();
    }

    public void SetPresentation(bool dark, bool transparentBackground)
    {
        if (_darkTheme == dark && _transparentBackground == transparentBackground)
            return;
        _darkTheme = dark;
        _transparentBackground = transparentBackground;
        RebindVisible();
    }

    public void UpdateSelection(IReadOnlyList<string> selectedIds, bool selectionMode)
    {
        _selectionMode = selectionMode;
        _selectedIds = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        RebindVisible();
    }

    public DriveItemModel? GetItem(int position)
    {
        if (_viewModel is null || position < 0 || position >= _viewModel.MobileItems.Count)
            return null;
        return _viewModel.MobileItems[position].Item;
    }

    public override nint GetItemsCount(UICollectionView collectionView, nint section) => ItemCount;

    public override UICollectionViewCell GetCell(UICollectionView collectionView, NSIndexPath indexPath)
    {
        var cell = collectionView.DequeueReusableCell(ReuseIdentifier, indexPath);
        if (!_presenters.TryGetValue(cell.Handle, out var presenter))
        {
            presenter = new IosNativeFileCellPresenter(cell, this);
            _presenters[cell.Handle] = presenter;
        }

        BindPresenter(presenter, (int)indexPath.Item);
        return cell;
    }

    public override void ItemSelected(UICollectionView collectionView, NSIndexPath indexPath)
    {
        collectionView.DeselectItem(indexPath, animated: false);
        var item = GetItem((int)indexPath.Item);
        if (item is not null)
            _host.RaiseItemTapped(item);
    }

    public override void DraggingStarted(UIScrollView scrollView)
    {
        SetScrolling(true);
        ReportScrollState(true);
    }

    public override void DraggingEnded(UIScrollView scrollView, bool willDecelerate)
    {
        if (!willDecelerate)
            FinishScrolling();
    }

    public override void DecelerationStarted(UIScrollView scrollView)
    {
        SetScrolling(true);
    }

    public override void DecelerationEnded(UIScrollView scrollView)
    {
        FinishScrolling();
    }

    public override void Scrolled(UIScrollView scrollView)
    {
        ReportScrollState(_scrolling || scrollView.Dragging || scrollView.Decelerating);
    }

    public void ReportScrollState(bool scrolling)
    {
        var (first, last) = GetVisibleRange();
        _host.RaiseScrollStateChanged(scrolling, first, last);
    }

    private void FinishScrolling()
    {
        SetScrolling(false);
        ReportScrollState(false);
        _ = RestartVisibleThumbnailWorkAfterIdleAsync();
    }

    private async Task RestartVisibleThumbnailWorkAfterIdleAsync()
    {
        await Task.Delay(70).ConfigureAwait(false);
        if (_disposed || _scrolling)
            return;

        _collection.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed && !_scrolling)
                StartVisibleThumbnailWork();
        });
    }

    private void SetScrolling(bool scrolling)
    {
        if (_scrolling == scrolling)
            return;
        _scrolling = scrolling;
        if (scrolling)
            CancelThumbnailGeneration();
    }

    private (int first, int last) GetVisibleRange()
    {
        var paths = _collection.IndexPathsForVisibleItems;
        if (paths is null || paths.Length == 0)
            return (0, 0);

        var first = int.MaxValue;
        var last = 0;
        foreach (var path in paths)
        {
            var index = (int)path.Item;
            first = Math.Min(first, index);
            last = Math.Max(last, index);
        }
        return (first == int.MaxValue ? 0 : first, last);
    }

    public void StartVisibleThumbnailWork()
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
    }

    internal void RebindPresenter(IosNativeFileCellPresenter presenter)
    {
        if (_disposed || presenter.BoundIndex < 0 || presenter.BoundIndex >= ItemCount)
            return;
        BindPresenter(presenter, presenter.BoundIndex);
    }

    private void BindPresenter(IosNativeFileCellPresenter presenter, int position)
    {
        if (_viewModel is null || position < 0 || position >= _viewModel.MobileItems.Count)
            return;

        var slot = _viewModel.MobileItems[position];
        var item = slot.Item;
        UIImage? cached = null;
        if (item is not null)
            TryGetImage(item, out cached);

        presenter.Bind(position, slot, _mode, _darkTheme, _transparentBackground, _selectionMode,
            item is not null && _selectedIds.Contains(item.Id), cached);

        if (!_scrolling && cached is null)
            RequestThumbnailIfNeeded(presenter, position);
    }

    private void MobileItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed)
            return;

        CancelThumbnailGeneration();
        _collection.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
                return;

            UIView.PerformWithoutAnimation(() => _collection.ReloadData());
            _collection.LayoutIfNeeded();
            if (!_scrolling)
                StartVisibleThumbnailWork();
        });
    }

    private void RebindVisible()
    {
        if (_disposed || _viewModel is null)
            return;

        var paths = _collection.IndexPathsForVisibleItems;
        if (paths is null)
            return;

        foreach (var path in paths)
        {
            var cell = _collection.CellForItem(path);
            if (cell is not null && _presenters.TryGetValue(cell.Handle, out var presenter))
                BindPresenter(presenter, (int)path.Item);
        }
    }

    private void PrefetchThumbnailIfNeeded(int position)
    {
        if (_disposed || _scrolling || _viewModel is null || position < 0 || position >= ItemCount)
            return;

        var item = _viewModel.MobileItems[position].Item;
        if (item is null || !item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return;

        // Keep the adjacent viewport in the bounded native UIImage LRU as well as on disk.
        if (TryGetImage(item, out var image) && image is not null)
            return;

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

                var path = await AppServices.ThumbnailCache
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

                AddImageToCache(item, image);
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
    {
        if (_disposed || _scrolling || _viewModel is null || position < 0 || position >= ItemCount)
            return;

        var item = _viewModel.MobileItems[position].Item;
        if (item is null || !item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return;

        if (TryGetImage(item, out var existing) && existing is not null)
        {
            presenter.ApplyThumbnail(item.Id, existing);
            return;
        }

        if (!_loadingIds.TryAdd(item.Id, 0))
            return;

        var generationToken = _thumbnailGenerationCts.Token;
        presenter.MarkThumbnailRequest(item.Id);
        _ = LoadThumbnailAsync(presenter, item, generationToken);
    }

    private async Task LoadThumbnailAsync(IosNativeFileCellPresenter presenter, DriveItemModel item, CancellationToken generationToken)
    {
        try
        {
            await _thumbnailGate.WaitAsync(generationToken).ConfigureAwait(false);
            try
            {
                generationToken.ThrowIfCancellationRequested();
                if (_scrolling)
                    return;

                var path = await AppServices.ThumbnailCache
                    .GetOrDownloadAsync(item, AppServices.OneDrive, generationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                generationToken.ThrowIfCancellationRequested();
                var image = await Task.Run(() => UIImage.FromFile(path), generationToken).ConfigureAwait(false);
                if (image is null)
                    return;

                AddImageToCache(item, image);
                if (generationToken.IsCancellationRequested)
                    return;

                _collection.BeginInvokeOnMainThread(() =>
                {
                    if (!_disposed && !_scrolling)
                        presenter.ApplyThumbnail(item.Id, image);
                });
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (System.OperationCanceledException)
        {
            // Expected while the user flings or navigates to another folder.
        }
        catch
        {
            // Cosmetic failure only. The shared OneDrive layer handles transient network retry.
        }
        finally
        {
            _loadingIds.TryRemove(item.Id, out _);
        }
    }

    private bool TryGetImage(DriveItemModel item, out UIImage? image)
    {
        var key = ImageKey(item);
        lock (_imageCacheGate)
        {
            if (!_imageCache.TryGetValue(key, out image) || image is null)
            {
                image = null;
                return false;
            }
            TouchImageLru(key);
            return true;
        }
    }

    private void AddImageToCache(DriveItemModel item, UIImage image)
    {
        var key = ImageKey(item);
        lock (_imageCacheGate)
        {
            _imageCache[key] = image;
            TouchImageLru(key);
            while (_imageLru.Count > ImageCacheLimit)
            {
                var oldest = _imageLru.First;
                if (oldest is null)
                    break;
                _imageLru.RemoveFirst();
                _imageLruNodes.Remove(oldest.Value);
                _imageCache.Remove(oldest.Value);
                // Do not Dispose here: a visible cell may still be drawing the UIImage.
            }
        }
    }

    private void TouchImageLru(string key)
    {
        if (_imageLruNodes.TryGetValue(key, out var node))
        {
            _imageLru.Remove(node);
            _imageLru.AddLast(node);
            return;
        }
        _imageLruNodes[key] = _imageLru.AddLast(key);
    }

    private string ImageKey(DriveItemModel item) => $"{item.Id}|{item.VersionToken}|{_mode}";

    private void CancelThumbnailGeneration()
    {
        var previous = Interlocked.Exchange(ref _thumbnailGenerationCts, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
        _loadingIds.Clear();
        _prefetchingIds.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_viewModel is not null)
                _viewModel.MobileItems.CollectionChanged -= MobileItems_CollectionChanged;
            _viewModel = null;

            CancelThumbnailGeneration();
            _thumbnailGenerationCts.Dispose();

            foreach (var presenter in _presenters.Values)
                presenter.Dispose();
            _presenters.Clear();

            lock (_imageCacheGate)
            {
                _imageCache.Clear();
                _imageLru.Clear();
                _imageLruNodes.Clear();
            }
            _thumbnailGate.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class IosNativeFileCellPresenter : IDisposable
{
    private readonly UICollectionViewCell _cell;
    private readonly IosNativeFileCollectionSource _owner;
    private readonly IosNativeFileCellContentView _content;
    private VirtualDriveItemSlot? _slot;
    private int _refreshQueued;
    private string? _thumbnailRequestItemId;
    private bool _disposed;

    public IosNativeFileCellPresenter(UICollectionViewCell cell, IosNativeFileCollectionSource owner)
    {
        _cell = cell;
        _owner = owner;
        _content = new IosNativeFileCellContentView(cell.ContentView.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };
        cell.ContentView.AddSubview(_content);
    }

    public int BoundIndex { get; private set; } = -1;

    public void Bind(
        int index,
        VirtualDriveItemSlot slot,
        FileViewMode mode,
        bool darkTheme,
        bool transparentBackground,
        bool selectionMode,
        bool selected,
        UIImage? image)
    {
        BoundIndex = index;
        if (!ReferenceEquals(_slot, slot))
        {
            if (_slot is not null)
                _slot.PropertyChanged -= Slot_PropertyChanged;
            _slot = slot;
            _slot.PropertyChanged += Slot_PropertyChanged;
        }

        _thumbnailRequestItemId = null;
        _content.Bind(slot.Item, mode, darkTheme, transparentBackground, selectionMode, selected, image);
    }

    public void MarkThumbnailRequest(string itemId) => _thumbnailRequestItemId = itemId;

    public void ApplyThumbnail(string itemId, UIImage image)
    {
        if (_disposed || _slot?.Item is not { } item ||
            !string.Equals(item.Id, itemId, StringComparison.Ordinal))
            return;

        _thumbnailRequestItemId = null;
        _content.SetThumbnail(image);
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(VirtualDriveItemSlot.Item) and
            not nameof(VirtualDriveItemSlot.Name) and
            not nameof(VirtualDriveItemSlot.SizeDisplay))
            return;

        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;

        _cell.BeginInvokeOnMainThread(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed && _slot is not null)
                _owner.RebindPresenter(this);
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_slot is not null)
            _slot.PropertyChanged -= Slot_PropertyChanged;
        _slot = null;
        _content.RemoveFromSuperview();
        _content.Dispose();
    }
}

internal sealed class IosNativeFileCellContentView : UIView
{
    private readonly IosNativeFileVisualView _visual;
    private readonly UILabel _nameLabel;
    private readonly UILabel _sizeLabel;
    private DriveItemModel? _item;
    private FileViewMode _mode;
    private bool _darkTheme;
    private bool _selectionMode;
    private bool _selected;

    public IosNativeFileCellContentView(CGRect frame) : base(frame)
    {
        ClipsToBounds = true;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        Layer.CornerRadius = 8;

        _visual = new IosNativeFileVisualView();
        _nameLabel = new UILabel
        {
            Lines = 2,
            LineBreakMode = UILineBreakMode.TailTruncation,
            Font = UIFont.SystemFontOfSize(14),
            TextAlignment = UITextAlignment.Left
        };
        _sizeLabel = new UILabel
        {
            Lines = 1,
            LineBreakMode = UILineBreakMode.TailTruncation,
            Font = UIFont.SystemFontOfSize(11),
            TextAlignment = UITextAlignment.Right
        };

        AddSubview(_visual);
        AddSubview(_nameLabel);
        AddSubview(_sizeLabel);
    }

    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool transparentBackground, bool selectionMode, bool selected, UIImage? image)
    {
        _item = item;
        _mode = mode;
        _darkTheme = darkTheme;
        _selectionMode = selectionMode;
        _selected = selected;

        var primary = darkTheme ? UIColor.FromRGB(238, 238, 238) : UIColor.FromRGB(37, 37, 37);
        var secondary = darkTheme ? UIColor.FromRGB(175, 175, 175) : UIColor.FromRGB(108, 116, 128);
        _nameLabel.TextColor = primary;
        _sizeLabel.TextColor = secondary;
        _nameLabel.Text = item?.Name ?? string.Empty;
        _sizeLabel.Text = item?.SizeDisplay ?? string.Empty;

        BackgroundColor = selected
            ? (darkTheme ? UIColor.FromRGBA(47, 128, 237, 77) : UIColor.FromRGBA(47, 128, 237, 36))
            : transparentBackground
                ? UIColor.Clear
                : (darkTheme ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250));

        _visual.Bind(item, darkTheme, selectionMode, selected, image);
        SetNeedsLayout();
    }

    public void SetThumbnail(UIImage image) => _visual.SetThumbnail(image);

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var width = (double)Bounds.Width;
        var height = (double)Bounds.Height;

        if (_mode == FileViewMode.Details)
        {
            Layer.CornerRadius = 5;
            _visual.Frame = new CGRect(8, 5, 36, 36);
            _nameLabel.TextAlignment = UITextAlignment.Left;
            _nameLabel.Lines = 1;
            _nameLabel.Frame = new CGRect(52, 0, Math.Max(20d, width - 148), height);
            _sizeLabel.TextAlignment = UITextAlignment.Right;
            _sizeLabel.Frame = new CGRect(Math.Max(52d, width - 90), 0, 82, height);
            return;
        }

        Layer.CornerRadius = _mode == FileViewMode.ExtraLargeIcons ? 11 : 9;
        var visualSize = _mode == FileViewMode.ExtraLargeIcons
            ? Math.Min(112d, Math.Max(72d, width - 30))
            : Math.Min(80d, Math.Max(58d, width - 24));
        var top = _mode == FileViewMode.ExtraLargeIcons ? 8 : 7;
        _visual.Frame = new CGRect((width - visualSize) / 2, top, visualSize, visualSize);
        _nameLabel.TextAlignment = UITextAlignment.Center;
        _nameLabel.Lines = 2;
        _nameLabel.Frame = new CGRect(7, (double)_visual.Frame.Bottom + 4, Math.Max(1d, width - 14), 35);
        _sizeLabel.TextAlignment = UITextAlignment.Center;
        _sizeLabel.Frame = new CGRect(7, height - 20, Math.Max(1d, width - 14), 16);
    }
}

/// <summary>
/// Single native drawing view for folder/file art, thumbnails and selection affordance.
/// The folder artwork uses the same supplied layered-yellow geometry as the Android native list.
/// </summary>
internal sealed class IosNativeFileVisualView : UIView
{
    private DriveItemModel? _item;
    private UIImage? _thumbnail;
    private bool _darkTheme;
    private bool _selectionMode;
    private bool _selected;

    public IosNativeFileVisualView()
    {
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
    }

    public void Bind(DriveItemModel? item, bool darkTheme, bool selectionMode, bool selected, UIImage? image)
    {
        _item = item;
        _darkTheme = darkTheme;
        _selectionMode = selectionMode;
        _selected = selected;
        _thumbnail = image;
        SetNeedsDisplay();
    }

    public void SetThumbnail(UIImage image)
    {
        _thumbnail = image;
        SetNeedsDisplay();
    }

    public override void Draw(CGRect rect)
    {
        base.Draw(rect);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (_item is null)
        {
            (_darkTheme ? UIColor.FromRGB(39, 39, 39) : UIColor.FromRGB(232, 234, 237)).SetFill();
            using var placeholder = UIBezierPath.FromRoundedRect(bounds.Inset(3, 3), 6);
            placeholder.Fill();
            return;
        }

        var artRect = bounds.Inset(2, 2);
        if (_thumbnail is not null)
        {
            using (var clip = UIBezierPath.FromRoundedRect(artRect, 6))
            {
                clip.AddClip();
                _thumbnail.Draw(CenterCropRect(artRect, _thumbnail.Size));
            }

            if (_item.IsVideo)
                DrawPlayBadge(artRect);
        }
        else if (_item.IsFolder)
        {
            DrawLayeredFolderGlyph(artRect);
        }
        else
        {
            DrawFileBadge(artRect, _item);
        }

        if (_selectionMode)
            DrawSelectionCircle(bounds, _selected);
    }

    private static void DrawLayeredFolderGlyph(CGRect rect)
    {
        const double sourceWidth = 32.0;
        const double sourceHeight = 26.0;
        var scale = Math.Min((double)rect.Width / sourceWidth, (double)rect.Height / sourceHeight);
        var left = (double)rect.GetMidX() - sourceWidth * scale / 2.0;
        var top = (double)rect.GetMidY() - sourceHeight * scale / 2.0;
        CGPoint P(double x, double y) => new(left + x * scale, top + y * scale);

        // Golden rear shell with the long sloped tab from the supplied reference image.
        UIColor.FromRGB(247, 188, 15).SetFill();
        using (var back = new UIBezierPath())
        {
            back.MoveTo(P(0, 7.5));
            back.AddCurveToPoint(P(4.3, 3.1), P(0, 5.1), P(1.9, 3.1));
            back.AddLineTo(P(9.7, 3.1));
            back.AddCurveToPoint(P(12.2, 4.0), P(10.7, 3.1), P(11.4, 3.4));
            back.AddLineTo(P(15.2, 6.3));
            back.AddCurveToPoint(P(18.7, 7.4), P(16.2, 7.1), P(17.3, 7.4));
            back.AddLineTo(P(29.5, 7.4));
            back.AddCurveToPoint(P(32.0, 9.9), P(30.9, 7.4), P(32.0, 8.5));
            back.AddLineTo(P(32.0, 22.3));
            back.AddCurveToPoint(P(28.3, 26.0), P(32.0, 24.3), P(30.3, 26.0));
            back.AddLineTo(P(3.7, 26.0));
            back.AddCurveToPoint(P(0, 22.3), P(1.7, 26.0), P(0, 24.3));
            back.ClosePath();
            back.Fill();
        }

        UIColor.FromRGB(255, 210, 141).SetFill();
        using (var rearInsert = UIBezierPath.FromRoundedRect(
            new CGRect(left + 4.4 * scale, top + 8.8 * scale, 24.5 * scale, 12.6 * scale),
            (nfloat)Math.Max(0.6, 1.2 * scale)))
            rearInsert.Fill();

        UIColor.FromRGB(255, 242, 214).SetFill();
        using (var innerSheet = UIBezierPath.FromRoundedRect(
            new CGRect(left + 3.2 * scale, top + 10.1 * scale, 24.5 * scale, 11.8 * scale),
            (nfloat)Math.Max(0.6, 1.2 * scale)))
            innerSheet.Fill();

        UIColor.FromRGB(255, 215, 107).SetFill();
        using (var front = UIBezierPath.FromRoundedRect(
            new CGRect(left, top + 11.5 * scale, 32.0 * scale, 14.5 * scale),
            (nfloat)Math.Max(0.8, 2.1 * scale)))
            front.Fill();

        UIColor.FromRGBA(255, 233, 176, 88).SetFill();
        using var sheen = UIBezierPath.FromRoundedRect(
            new CGRect(left + 2.0 * scale, top + 12.2 * scale, 28.0 * scale, 1.2 * scale),
            (nfloat)Math.Max(0.4, 0.6 * scale));
        sheen.Fill();
    }

    private static void DrawFileBadge(CGRect rect, DriveItemModel item)
    {
        if (item.IsImage)
        {
            DrawImagePlaceholder(rect);
            return;
        }

        if (item.IsVideo)
        {
            DrawVideoPlaceholder(rect);
            return;
        }

        if (item.IsAudio)
        {
            DrawAudioPlaceholder(rect);
            return;
        }

        DrawDocumentPlaceholder(rect, item);
    }

    private static void DrawImagePlaceholder(CGRect rect)
    {
        double X(double value) => (double)rect.Left + (double)rect.Width * value;
        double Y(double value) => (double)rect.Top + (double)rect.Height * value;
        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.22;

        UIColor.FromRGB(238, 247, 255).SetFill();
        using (var background = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
            background.Fill();
        UIColor.FromRGB(142, 197, 255).SetStroke();
        using (var border = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
        {
            border.LineWidth = (nfloat)Math.Max(1, (double)rect.Width * 0.04);
            border.Stroke();
        }

        UIColor.FromRGB(59, 130, 246).SetStroke();
        using (var mountain = new UIBezierPath
        {
            LineWidth = (nfloat)Math.Max(1.4, (double)rect.Width * 0.055),
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        })
        {
            mountain.MoveTo(new CGPoint(X(0.18), Y(0.75)));
            mountain.AddLineTo(new CGPoint(X(0.36), Y(0.53)));
            mountain.AddLineTo(new CGPoint(X(0.50), Y(0.65)));
            mountain.AddLineTo(new CGPoint(X(0.64), Y(0.50)));
            mountain.AddLineTo(new CGPoint(X(0.82), Y(0.75)));
            mountain.Stroke();
        }

        UIColor.FromRGB(253, 186, 116).SetFill();
        var sunRadius = Math.Min((double)rect.Width, (double)rect.Height) * 0.085;
        using var sun = UIBezierPath.FromOval(new CGRect(X(0.31) - sunRadius, Y(0.31) - sunRadius, sunRadius * 2, sunRadius * 2));
        sun.Fill();
    }

    private static void DrawVideoPlaceholder(CGRect rect)
    {
        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.22;
        UIColor.FromRGB(238, 242, 255).SetFill();
        using (var background = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
            background.Fill();
        UIColor.FromRGB(165, 180, 252).SetStroke();
        using (var border = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
        {
            border.LineWidth = (nfloat)Math.Max(1, (double)rect.Width * 0.035);
            border.Stroke();
        }

        var playRadius = Math.Min((double)rect.Width, (double)rect.Height) * 0.285;
        var center = new CGPoint(rect.GetMidX(), rect.GetMidY());
        UIColor.FromRGB(124, 58, 237).SetFill();
        using (var circle = UIBezierPath.FromOval(new CGRect(center.X - playRadius, center.Y - playRadius, playRadius * 2, playRadius * 2)))
            circle.Fill();

        UIColor.White.SetFill();
        using var triangle = new UIBezierPath();
        triangle.MoveTo(new CGPoint(center.X - playRadius * 0.28, center.Y - playRadius * 0.48));
        triangle.AddLineTo(new CGPoint(center.X + playRadius * 0.55, center.Y));
        triangle.AddLineTo(new CGPoint(center.X - playRadius * 0.28, center.Y + playRadius * 0.48));
        triangle.ClosePath();
        triangle.Fill();
    }

    private static void DrawAudioPlaceholder(CGRect rect)
    {
        double X(double value) => (double)rect.Left + (double)rect.Width * value;
        double Y(double value) => (double)rect.Top + (double)rect.Height * value;
        var min = Math.Min((double)rect.Width, (double)rect.Height);

        UIColor.FromRGB(96, 165, 250).SetFill();
        using (var leftCircle = UIBezierPath.FromOval(new CGRect(X(0.42) - min * 0.36, Y(0.58) - min * 0.36, min * 0.72, min * 0.72)))
            leftCircle.Fill();
        UIColor.FromRGB(251, 113, 133).SetFill();
        using (var rightCircle = UIBezierPath.FromOval(new CGRect(X(0.58) - min * 0.36, Y(0.42) - min * 0.36, min * 0.72, min * 0.72)))
            rightCircle.Fill();

        UIColor.White.SetStroke();
        using (var note = new UIBezierPath
        {
            LineWidth = (nfloat)Math.Max(1.6, min * 0.07),
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        })
        {
            note.MoveTo(new CGPoint(X(0.40), Y(0.30)));
            note.AddLineTo(new CGPoint(X(0.40), Y(0.70)));
            note.MoveTo(new CGPoint(X(0.40), Y(0.30)));
            note.AddLineTo(new CGPoint(X(0.70), Y(0.23)));
            note.AddLineTo(new CGPoint(X(0.70), Y(0.60)));
            note.Stroke();
        }

        UIColor.White.SetFill();
        using (var leftHead = UIBezierPath.FromOval(new CGRect(X(0.23), Y(0.64), X(0.46) - X(0.23), Y(0.82) - Y(0.64))))
            leftHead.Fill();
        using var rightHead = UIBezierPath.FromOval(new CGRect(X(0.53), Y(0.54), X(0.76) - X(0.53), Y(0.72) - Y(0.54)));
        rightHead.Fill();
    }

    private static void DrawDocumentPlaceholder(CGRect rect, DriveItemModel item)
    {
        var background = item.IsPdf ? UIColor.FromRGB(255, 245, 245)
            : item.IsWord ? UIColor.FromRGB(239, 246, 255)
            : item.IsExcel ? UIColor.FromRGB(240, 253, 244)
            : item.IsPowerPoint ? UIColor.FromRGB(255, 247, 237)
            : item.IsArchive ? UIColor.FromRGB(245, 243, 255)
            : item.IsUrlShortcut ? UIColor.FromRGB(240, 253, 250)
            : UIColor.FromRGB(248, 250, 252);
        var borderColor = item.IsPdf ? UIColor.FromRGB(240, 160, 168)
            : item.IsWord ? UIColor.FromRGB(147, 197, 253)
            : item.IsExcel ? UIColor.FromRGB(134, 239, 172)
            : item.IsPowerPoint ? UIColor.FromRGB(253, 186, 116)
            : item.IsArchive ? UIColor.FromRGB(196, 181, 253)
            : item.IsUrlShortcut ? UIColor.FromRGB(94, 234, 212)
            : UIColor.FromRGB(203, 213, 225);
        var accent = item.IsPdf ? UIColor.FromRGB(239, 68, 68)
            : item.IsWord ? UIColor.FromRGB(37, 99, 235)
            : item.IsExcel ? UIColor.FromRGB(22, 163, 74)
            : item.IsPowerPoint ? UIColor.FromRGB(249, 115, 22)
            : item.IsArchive ? UIColor.FromRGB(139, 92, 246)
            : item.IsUrlShortcut ? UIColor.FromRGB(14, 165, 164)
            : item.IsText ? UIColor.FromRGB(100, 116, 139)
            : UIColor.FromRGB(96, 165, 250);

        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.18;
        background.SetFill();
        using (var card = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
            card.Fill();
        borderColor.SetStroke();
        using (var border = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
        {
            border.LineWidth = (nfloat)Math.Max(1, (double)rect.Width * 0.035);
            border.Stroke();
        }

        var stripHeight = (double)rect.Height * 0.30;
        var stripRect = new CGRect(rect.Left, rect.Bottom - stripHeight, rect.Width, stripHeight);
        accent.SetFill();
        using (var strip = UIBezierPath.FromRoundedRect(stripRect, (nfloat)(radius * 0.65)))
            strip.Fill();
        using (var stripTopFill = UIBezierPath.FromRect(new CGRect(rect.Left, stripRect.Top, rect.Width, (nfloat)(radius * 0.65))))
        {
            stripTopFill.Fill();
        }

        using var text = new NSString(item.FileBadgeText);
        var font = UIFont.BoldSystemFontOfSize((nfloat)Math.Min(8, Math.Max(5, (double)rect.Height * 0.16)));
        var attrs = new UIStringAttributes
        {
            ForegroundColor = UIColor.White,
            Font = font
        };
        var size = text.GetSizeUsingAttributes(attrs);
        text.DrawString(new CGPoint(rect.GetMidX() - size.Width / 2, stripRect.GetMidY() - size.Height / 2), attrs);
    }

    private static void DrawPlayBadge(CGRect rect)
    {
        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.16;
        var center = new CGPoint(rect.GetMidX(), rect.GetMidY());
        UIColor.FromRGBA(20, 20, 20, 204).SetFill();
        using (var circle = UIBezierPath.FromOval(new CGRect(center.X - radius, center.Y - radius, radius * 2, radius * 2)))
            circle.Fill();

        UIColor.White.SetFill();
        using var triangle = new UIBezierPath();
        triangle.MoveTo(new CGPoint(center.X - radius * 0.28, center.Y - radius * 0.50));
        triangle.AddLineTo(new CGPoint(center.X + radius * 0.55, center.Y));
        triangle.AddLineTo(new CGPoint(center.X - radius * 0.28, center.Y + radius * 0.50));
        triangle.ClosePath();
        triangle.Fill();
    }

    private static void DrawSelectionCircle(CGRect bounds, bool selected)
    {
        const double r = 10;
        var cx = (double)bounds.Width - 17;
        const double cy = 17;
        (selected ? UIColor.FromRGB(47, 128, 237) : UIColor.FromRGBA(255, 255, 255, 209)).SetFill();
        using (var circle = UIBezierPath.FromOval(new CGRect(cx - r, cy - r, r * 2, r * 2)))
            circle.Fill();

        var stroke = selected ? UIColor.White : UIColor.Gray;
        stroke.SetStroke();
        using (var outline = UIBezierPath.FromOval(new CGRect(cx - r, cy - r, r * 2, r * 2)))
        {
            outline.LineWidth = 1.5f;
            outline.Stroke();
        }

        if (!selected)
            return;

        UIColor.White.SetStroke();
        using var check = new UIBezierPath
        {
            LineWidth = 2.0f,
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        };
        check.MoveTo(new CGPoint(cx - r * 0.45, cy));
        check.AddLineTo(new CGPoint(cx - r * 0.10, cy + r * 0.35));
        check.AddLineTo(new CGPoint(cx + r * 0.52, cy - r * 0.42));
        check.Stroke();
    }

    private static CGRect CenterCropRect(CGRect destination, CGSize source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return destination;
        var scale = Math.Max((double)destination.Width / (double)source.Width,
            (double)destination.Height / (double)source.Height);
        var width = (double)source.Width * scale;
        var height = (double)source.Height * scale;
        return new CGRect(
            (double)destination.GetMidX() - width / 2,
            (double)destination.GetMidY() - height / 2,
            width,
            height);
    }
}
