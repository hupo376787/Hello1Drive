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
    private readonly UICollectionViewFlowLayout _layout;
    private readonly NativeCollectionView _collection;
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
            BackgroundColor = UIColor.SystemBackground,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };

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

        _host.HostStateChanged += Host_HostStateChanged;
        _host.ScrollToPositionRequested += Host_ScrollToPositionRequested;

        SyncHostState(preservePosition: false);
    }

    public UIView RootView => _collection;

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

        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or
            nameof(MainViewModel.SelectedBackgroundModeText))
        {
            UpdateTheme();
        }
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
        var background = dark ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250);
        _collection.BackgroundColor = background;
        _refresh.TintColor = dark ? UIColor.White : UIColor.DarkGray;
        _source.SetDarkTheme(dark);
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

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;

        _collection.RemoveGestureRecognizer(_longPress);
        _collection.Source = null;
        _collection.RefreshControl = null;
        _source.Dispose();
        _longPress.Dispose();
        _refresh.Dispose();
        _collection.Dispose();
        _layout.Dispose();
        base.Dispose();
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
    private readonly Dictionary<nint, IosNativeFileCellPresenter> _presenters = [];
    private readonly object _imageCacheGate = new();
    private readonly Dictionary<string, UIImage> _imageCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _imageLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _imageLruNodes = new(StringComparer.Ordinal);
    private CancellationTokenSource _thumbnailGenerationCts = new();
    private MainViewModel? _viewModel;
    private bool _scrolling;
    private bool _darkTheme;
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

    public void SetDarkTheme(bool dark)
    {
        if (_darkTheme == dark)
            return;
        _darkTheme = dark;
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
        if (paths is null)
            return;

        foreach (var path in paths.OrderBy(static p => p.Item))
        {
            var cell = _collection.CellForItem(path);
            if (cell is null || !_presenters.TryGetValue(cell.Handle, out var presenter))
                continue;
            RequestThumbnailIfNeeded(presenter, (int)path.Item);
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

        presenter.Bind(position, slot, _mode, _darkTheme, _selectionMode,
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
            if (!_disposed)
                UIView.PerformWithoutAnimation(() => _collection.ReloadData());
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
        _content.Bind(slot.Item, mode, darkTheme, selectionMode, selected, image);
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

    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool selectionMode, bool selected, UIImage? image)
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
/// The folder artwork uses the same Windows-11-style layered geometry as the Android native list.
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
            DrawWindows11FolderGlyph(artRect);
        }
        else
        {
            DrawFileBadge(artRect, _item);
        }

        if (_selectionMode)
            DrawSelectionCircle(bounds, _selected);
    }

    private void DrawWindows11FolderGlyph(CGRect rect)
    {
        const double sourceWidth = 20.0;
        const double sourceHeight = 18.0;
        var scale = Math.Min((double)rect.Width / sourceWidth, (double)rect.Height / sourceHeight);
        var left = (double)rect.GetMidX() - sourceWidth * scale / 2.0;
        var top = (double)rect.GetMidY() - sourceHeight * scale / 2.0;
        CGPoint P(double x, double y) => new(left + x * scale, top + y * scale);

        // Back plate + raised tab.
        (_darkTheme ? UIColor.FromRGB(238, 173, 18) : UIColor.FromRGB(244, 177, 20)).SetFill();
        using (var back = new UIBezierPath())
        {
            back.MoveTo(P(1.2, 5.1));
            back.AddCurveToPoint(P(3.6, 2.7), P(1.2, 3.75), P(2.25, 2.7));
            back.AddLineTo(P(8.25, 2.7));
            back.AddCurveToPoint(P(9.65, 3.35), P(8.85, 2.7), P(9.25, 2.9));
            back.AddLineTo(P(10.85, 4.75));
            back.AddLineTo(P(16.65, 4.75));
            back.AddCurveToPoint(P(18.85, 7.0), P(18.0, 4.75), P(18.85, 5.65));
            back.AddLineTo(P(18.85, 14.55));
            back.AddCurveToPoint(P(16.6, 16.8), P(18.85, 15.9), P(17.95, 16.8));
            back.AddLineTo(P(3.4, 16.8));
            back.AddCurveToPoint(P(1.15, 14.55), P(2.05, 16.8), P(1.15, 15.9));
            back.ClosePath();
            back.Fill();
        }

        // Main front face.
        (_darkTheme ? UIColor.FromRGB(255, 210, 74) : UIColor.FromRGB(255, 211, 75)).SetFill();
        using (var front = new UIBezierPath())
        {
            front.MoveTo(P(1.05, 6.25));
            front.AddCurveToPoint(P(2.3, 5.0), P(1.05, 5.55), P(1.6, 5.0));
            front.AddLineTo(P(17.7, 5.0));
            front.AddCurveToPoint(P(18.95, 6.25), P(18.4, 5.0), P(18.95, 5.55));
            front.AddLineTo(P(18.95, 15.05));
            front.AddCurveToPoint(P(17.15, 16.85), P(18.95, 16.05), P(18.15, 16.85));
            front.AddLineTo(P(2.85, 16.85));
            front.AddCurveToPoint(P(1.05, 15.05), P(1.85, 16.85), P(1.05, 16.05));
            front.ClosePath();
            front.Fill();
        }

        (_darkTheme ? UIColor.FromRGB(255, 231, 139) : UIColor.FromRGB(255, 233, 144)).SetFill();
        using (var highlight = UIBezierPath.FromRoundedRect(
            new CGRect(left + 2.0 * scale, top + 5.65 * scale, 16.0 * scale, 1.1 * scale),
            (nfloat)Math.Max(0.5, 0.55 * scale)))
            highlight.Fill();

        (_darkTheme ? UIColor.FromRGB(222, 156, 12) : UIColor.FromRGB(225, 160, 17)).SetFill();
        using var shade = UIBezierPath.FromRoundedRect(
            new CGRect(left + 2.45 * scale, top + 15.85 * scale, 15.1 * scale, 0.6 * scale),
            (nfloat)Math.Max(0.3, 0.30 * scale));
        shade.Fill();
    }

    private static void DrawFileBadge(CGRect rect, DriveItemModel item)
    {
        var color = item.IsPdf ? UIColor.FromRGB(232, 67, 76)
            : item.IsWord ? UIColor.FromRGB(54, 108, 205)
            : item.IsExcel ? UIColor.FromRGB(33, 133, 88)
            : item.IsPowerPoint ? UIColor.FromRGB(211, 91, 55)
            : item.IsImage ? UIColor.FromRGB(67, 143, 237)
            : item.IsVideo ? UIColor.FromRGB(124, 88, 204)
            : item.IsAudio ? UIColor.FromRGB(226, 93, 142)
            : UIColor.FromRGB(105, 116, 133);
        color.SetFill();
        using (var badge = UIBezierPath.FromRoundedRect(rect, 7))
            badge.Fill();

        using var text = new NSString(item.FileBadgeText);
        var font = UIFont.BoldSystemFontOfSize((nfloat)Math.Min(13, Math.Max(8, (double)rect.Height * 0.26)));
        var attrs = new UIStringAttributes
        {
            ForegroundColor = UIColor.White,
            Font = font
        };
        var size = text.GetSizeUsingAttributes(attrs);
        text.DrawString(new CGPoint(rect.GetMidX() - size.Width / 2, rect.GetMidY() - size.Height / 2), attrs);
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
