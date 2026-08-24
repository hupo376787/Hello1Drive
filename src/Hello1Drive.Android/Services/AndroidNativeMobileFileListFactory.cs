using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Avalonia.Android;
using Avalonia.Platform;
using AndroidX.RecyclerView.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Android.Services;

/// <summary>
/// Android implementation of the phone file surface. The scrolling hot path is entirely native:
/// RecyclerView + native Canvas-drawn cells + native BitmapFactory thumbnail decode.
/// Avalonia remains responsible for the surrounding shell, preview pages and dialogs.
/// </summary>
internal sealed class AndroidNativeMobileFileListFactory : INativeMobileFileListFactory
{
    private readonly ConcurrentDictionary<nint, AndroidNativeFileListController> _controllers = new();

    public IPlatformHandle CreateControl(IPlatformHandle parent, NativeMobileFileListHost host)
    {
        var context = (parent as AndroidViewControlHandle)?.View.Context
            ?? MainActivity.Instance
            ?? global::Android.App.Application.Context;

        var controller = new AndroidNativeFileListController(context, host);
        var handle = new AndroidViewControlHandle(controller.RootView);
        _controllers[handle.Handle] = controller;
        return handle;
    }

    public void DestroyControl(IPlatformHandle control)
    {
        if (_controllers.TryRemove(control.Handle, out var controller))
            controller.Dispose();
    }
}

internal sealed class AndroidNativeFileListController : Java.Lang.Object, IDisposable
{
    private readonly Context _context;
    private readonly NativeMobileFileListHost _host;
    private readonly FrameLayout _root;
    private readonly SwipeRefreshLayout _refresh;
    private readonly RecyclerView _recycler;
    private readonly NativeFloatingUploadButtonView _floatingUpload;
    private readonly NativeFileAdapter _adapter;
    private readonly NativeScrollListener _scrollListener;
    private readonly NativeRefreshListener _refreshListener;
    private MainViewModel? _viewModel;
    private bool _disposed;
    private bool _scrolling;

    public AndroidNativeFileListController(Context context, NativeMobileFileListHost host)
    {
        _context = context;
        _host = host;

        _root = new FrameLayout(context)
        {
            ClipChildren = false,
            ClipToPadding = false
        };
        _refresh = new SwipeRefreshLayout(context);
        _recycler = new RecyclerView(context);
        _floatingUpload = new NativeFloatingUploadButtonView(context, host);
        _adapter = new NativeFileAdapter(context, host, _recycler);
        _scrollListener = new NativeScrollListener(this);
        _refreshListener = new NativeRefreshListener(this);

        _recycler.HasFixedSize = true;
        _recycler.SetItemViewCacheSize(24);
        _recycler.SetItemAnimator(null);
        _recycler.OverScrollMode = OverScrollMode.IfContentScrolls;
        _recycler.SetAdapter(_adapter);
        _recycler.AddOnScrollListener(_scrollListener);

        _refresh.SetOnRefreshListener(_refreshListener);
        _refresh.AddView(_recycler, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        _root.AddView(_refresh, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        var floatingSize = Dp(48);
        _root.AddView(_floatingUpload, new FrameLayout.LayoutParams(floatingSize, floatingSize));

        _host.HostStateChanged += Host_HostStateChanged;
        _host.ScrollToPositionRequested += Host_ScrollToPositionRequested;
        _recycler.LayoutChange += Recycler_LayoutChange;
        _root.LayoutChange += Root_LayoutChange;

        SyncHostState(preservePosition: false);
    }

    public View RootView => _root;

    private void Host_HostStateChanged(object? sender, EventArgs e) => SyncHostState(preservePosition: true);

    private void Host_ScrollToPositionRequested(object? sender, NativeMobileFileScrollToEventArgs e)
    {
        if (_disposed)
            return;
        _recycler.StopScroll();
        _recycler.ScrollToPosition(e.Position);
        _recycler.Post(() => ReportScrollState(false));
    }

    private void Recycler_LayoutChange(object? sender, View.LayoutChangeEventArgs e)
    {
        if (_disposed)
            return;
        if (_viewModel?.ViewMode is FileViewMode.LargeIcons or FileViewMode.ExtraLargeIcons)
            ApplyLayoutManager(_viewModel.ViewMode, preservePosition: true);
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

            _adapter.Attach(_viewModel);
        }

        _adapter.UpdateSelection(_host.SelectedIds, _host.SelectionMode);
        UpdateTheme();
        SyncFloatingUpload();

        if (_viewModel is not null)
            ApplyLayoutManager(_viewModel.ViewMode, preservePosition);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _viewModel is null)
            return;

        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            ApplyLayoutManager(_viewModel.ViewMode, preservePosition: true);
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

    private void Root_LayoutChange(object? sender, View.LayoutChangeEventArgs e)
    {
        if (!_disposed)
            PositionFloatingUpload();
    }

    private void SyncFloatingUpload()
    {
        if (_disposed)
            return;

        _floatingUpload.Visibility = _host.FloatingUploadVisible ? ViewStates.Visible : ViewStates.Gone;
        if (_floatingUpload.Visibility == ViewStates.Visible)
            _root.Post(PositionFloatingUpload);
    }

    private void PositionFloatingUpload()
    {
        if (_disposed || _floatingUpload.Visibility != ViewStates.Visible)
            return;

        var buttonWidth = _floatingUpload.Width > 0 ? _floatingUpload.Width : Dp(48);
        var buttonHeight = _floatingUpload.Height > 0 ? _floatingUpload.Height : Dp(48);
        var maxX = Math.Max(0, _root.Width - buttonWidth);
        var maxY = Math.Max(0, _root.Height - buttonHeight);
        if (maxX <= 0 && maxY <= 0)
            return;

        _floatingUpload.X = (float)(Math.Clamp(_host.FloatingUploadX, 0, 1) * maxX);
        _floatingUpload.Y = (float)(Math.Clamp(_host.FloatingUploadY, 0, 1) * maxY);
        _floatingUpload.BringToFront();
    }

    private void ApplyLayoutManager(FileViewMode mode, bool preservePosition)
    {
        var first = preservePosition ? GetFirstVisiblePosition() : 0;
        var currentMode = _adapter.Mode;
        if (currentMode == mode && _recycler.GetLayoutManager() is not null)
        {
            if (mode is FileViewMode.LargeIcons or FileViewMode.ExtraLargeIcons &&
                _recycler.GetLayoutManager() is GridLayoutManager currentGrid)
            {
                var desired = CalculateSpanCount(mode);
                if (currentGrid.SpanCount != desired)
                    currentGrid.SpanCount = desired;
            }
            return;
        }

        _adapter.SetMode(mode);
        RecyclerView.LayoutManager manager = mode switch
        {
            FileViewMode.LargeIcons => new GridLayoutManager(_context, CalculateSpanCount(mode)),
            FileViewMode.ExtraLargeIcons => new GridLayoutManager(_context, CalculateSpanCount(mode)),
            _ => new LinearLayoutManager(_context, LinearLayoutManager.Vertical, false)
        };

        _recycler.SetLayoutManager(manager);
        _adapter.NotifyDataSetChanged();
        if (preservePosition && first > 0)
            _recycler.ScrollToPosition(first);
    }

    private int CalculateSpanCount(FileViewMode mode)
    {
        var widthPx = _recycler.Width;
        if (widthPx <= 0)
            widthPx = _context.Resources?.DisplayMetrics?.WidthPixels ?? Dp(360);

        var minCellDp = mode == FileViewMode.ExtraLargeIcons ? 150 : 108;
        var minCellPx = Dp(minCellDp);
        return Math.Max(1, widthPx / Math.Max(1, minCellPx));
    }

    private void UpdateTheme()
    {
        var dark = IsDarkTheme();
        var transparent = _viewModel?.TransparentFileItemBackground == true;
        var background = transparent
            ? Color.Transparent
            : dark ? Color.Rgb(18, 18, 18) : Color.Rgb(250, 250, 250);
        _refresh.SetBackgroundColor(background);
        _recycler.SetBackgroundColor(background);
        _adapter.SetPresentation(dark, transparent);
    }

    private bool IsDarkTheme()
    {
        if (_viewModel?.SelectedThemeText == "深色")
            return true;
        if (_viewModel?.SelectedThemeText == "浅色")
            return false;

        var configuration = _context.Resources?.Configuration;
        if (configuration is null)
            return false;
        return (configuration.UiMode & UiMode.NightMask) == UiMode.NightYes;
    }

    public void OnScrollStateChanged(int newState)
    {
        if (_disposed)
            return;

        var nowScrolling = newState != RecyclerView.ScrollStateIdle;
        if (nowScrolling == _scrolling)
        {
            if (!nowScrolling)
                ReportScrollState(false);
            return;
        }

        _scrolling = nowScrolling;
        _adapter.SetScrolling(nowScrolling);
        ReportScrollState(nowScrolling);

        if (!nowScrolling)
        {
            // Let RecyclerView finish its final layout/prefetch frame before thumbnail decode starts.
            _recycler.PostDelayed(() =>
            {
                if (!_disposed && !_scrolling)
                    _adapter.StartVisibleThumbnailWork();
            }, 72);
        }
    }

    public void OnScrolled()
    {
        if (_disposed)
            return;
        _adapter.UpdateVisibleRange(GetFirstVisiblePosition(), GetLastVisiblePosition());
    }

    private void ReportScrollState(bool scrolling)
    {
        var first = GetFirstVisiblePosition();
        var last = GetLastVisiblePosition();
        _adapter.UpdateVisibleRange(first, last);
        _host.RaiseScrollStateChanged(scrolling, first, last);
    }

    private int GetFirstVisiblePosition()
    {
        return _recycler.GetLayoutManager() switch
        {
            LinearLayoutManager linear => Math.Max(0, linear.FindFirstVisibleItemPosition()),
            _ => 0
        };
    }

    private int GetLastVisiblePosition()
    {
        return _recycler.GetLayoutManager() switch
        {
            LinearLayoutManager linear => Math.Max(0, linear.FindLastVisibleItemPosition()),
            _ => 0
        };
    }

    public async void OnRefresh()
    {
        if (_disposed)
            return;

        try
        {
            await _host.RaiseRefreshRequestedAsync();
        }
        catch (System.OperationCanceledException)
        {
            // A new navigation/refresh superseded this one.
        }
        finally
        {
            if (!_disposed)
                _refresh.Post(() => _refresh.Refreshing = false);
        }
    }

    private int Dp(float value)
    {
        var density = _context.Resources?.DisplayMetrics?.Density ?? 1f;
        return Math.Max(1, (int)MathF.Round(value * density));
    }

    public new void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _host.HostStateChanged -= Host_HostStateChanged;
        _host.ScrollToPositionRequested -= Host_ScrollToPositionRequested;
        _recycler.LayoutChange -= Recycler_LayoutChange;
        _root.LayoutChange -= Root_LayoutChange;
        _recycler.RemoveOnScrollListener(_scrollListener);
        _refresh.SetOnRefreshListener(null);

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;

        _adapter.Dispose();
        _recycler.SetAdapter(null);
        _recycler.SetLayoutManager(null);
        _root.RemoveAllViews();
        _refresh.RemoveAllViews();
        _floatingUpload.Dispose();
        _recycler.Dispose();
        _refresh.Dispose();
        _root.Dispose();
        _scrollListener.Dispose();
        _refreshListener.Dispose();
        base.Dispose();
    }

    private sealed class NativeScrollListener(AndroidNativeFileListController owner) : RecyclerView.OnScrollListener
    {
        public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
        {
            base.OnScrollStateChanged(recyclerView, newState);
            owner.OnScrollStateChanged(newState);
        }

        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            base.OnScrolled(recyclerView, dx, dy);
            owner.OnScrolled();
        }
    }

    private sealed class NativeRefreshListener(AndroidNativeFileListController owner) : Java.Lang.Object, SwipeRefreshLayout.IOnRefreshListener
    {
        public void OnRefresh() => owner.OnRefresh();
    }
}

internal sealed class NativeFloatingUploadButtonView : View
{
    private readonly NativeMobileFileListHost _host;
    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _iconPaint = new(PaintFlags.AntiAlias);
    private readonly float _touchSlop;
    private bool _tracking;
    private bool _moved;
    private float _downRawX;
    private float _downRawY;
    private float _startX;
    private float _startY;

    public NativeFloatingUploadButtonView(Context context, NativeMobileFileListHost host) : base(context)
    {
        _host = host;
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? Dp(6);

        Clickable = true;
        Focusable = true;
        ContentDescription = "上传文件";
        Elevation = Dp(8);
        SetWillNotDraw(false);

        _fillPaint.Color = Color.Rgb(253, 111, 113);
        _fillPaint.SetStyle(Paint.Style.Fill);
        _iconPaint.Color = Color.Rgb(255, 247, 248);
        _iconPaint.SetStyle(Paint.Style.Stroke);
        _iconPaint.StrokeWidth = Dp(1.5f);
        _iconPaint.StrokeCap = Paint.Cap.Round;
        _iconPaint.StrokeJoin = Paint.Join.Round;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Width <= 0 || Height <= 0)
            return;

        var diameter = Math.Min(Width, Height);
        canvas.DrawCircle(Width / 2f, Height / 2f, diameter / 2f, _fillPaint);

        var scale = diameter / 14f;
        var offsetX = (Width - 14f * scale) / 2f;
        var offsetY = (Height - 14f * scale) / 2f;
        float X(float value) => offsetX + value * scale;
        float Y(float value) => offsetY + value * scale;

        canvas.DrawLine(X(7), Y(12), X(7), Y(2), _iconPaint);
        canvas.DrawLine(X(3.5f), Y(5.5f), X(7), Y(2), _iconPaint);
        canvas.DrawLine(X(7), Y(2), X(10.5f), Y(5.5f), _iconPaint);
        canvas.DrawLine(X(2), Y(12), X(12), Y(12), _iconPaint);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
            return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _tracking = true;
                _moved = false;
                _downRawX = e.RawX;
                _downRawY = e.RawY;
                _startX = X;
                _startY = Y;
                Parent?.RequestDisallowInterceptTouchEvent(true);
                BringToFront();
                return true;

            case MotionEventActions.Move:
                if (!_tracking)
                    return false;
                MoveTo(e.RawX, e.RawY);
                return true;

            case MotionEventActions.Up:
                if (!_tracking)
                    return false;
                MoveTo(e.RawX, e.RawY);
                _tracking = false;
                Parent?.RequestDisallowInterceptTouchEvent(false);
                if (_moved)
                    SaveNormalizedPosition();
                else
                    PerformClick();
                return true;

            case MotionEventActions.Cancel:
                _tracking = false;
                Parent?.RequestDisallowInterceptTouchEvent(false);
                return true;

            default:
                return base.OnTouchEvent(e);
        }
    }

    public override bool PerformClick()
    {
        base.PerformClick();
        _host.RaiseFloatingUploadRequested();
        return true;
    }

    private void MoveTo(float rawX, float rawY)
    {
        var dx = rawX - _downRawX;
        var dy = rawY - _downRawY;
        if (!_moved && MathF.Sqrt(dx * dx + dy * dy) >= _touchSlop)
            _moved = true;
        if (!_moved || Parent is not View parent)
            return;

        var maxX = Math.Max(0, parent.Width - Width);
        var maxY = Math.Max(0, parent.Height - Height);
        X = Math.Clamp(_startX + dx, 0, maxX);
        Y = Math.Clamp(_startY + dy, 0, maxY);
    }

    private void SaveNormalizedPosition()
    {
        if (Parent is not View parent)
            return;

        var maxX = Math.Max(1, parent.Width - Width);
        var maxY = Math.Max(1, parent.Height - Height);
        _host.RaiseFloatingUploadPositionChanged(X / maxX, Y / maxY);
    }

    private float Dp(float value)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        return value * density;
    }
}

internal sealed class NativeFileAdapter : RecyclerView.Adapter, IDisposable
{
    private readonly Context _context;
    private readonly NativeMobileFileListHost _host;
    private readonly RecyclerView _recycler;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingIds = new(StringComparer.Ordinal);
    private readonly object _bitmapCacheGate = new();
    private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _bitmapLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _bitmapLruNodes = new(StringComparer.Ordinal);
    private CancellationTokenSource _thumbnailGenerationCts = new();
    private MainViewModel? _viewModel;
    private bool _scrolling;
    private bool _darkTheme;
    private bool _transparentBackground;
    private bool _selectionMode;
    private HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private int _visibleFirst;
    private int _visibleLast;
    private bool _disposed;

    private const int BitmapCacheLimit = 96;

    public NativeFileAdapter(Context context, NativeMobileFileListHost host, RecyclerView recycler)
    {
        _context = context;
        _host = host;
        _recycler = recycler;
        HasStableIds = true;
    }

    public FileViewMode Mode { get; private set; } = FileViewMode.Details;

    public override int ItemCount => _viewModel?.MobileItems.Count ?? 0;

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

        NotifyDataSetChanged();
    }

    public void SetMode(FileViewMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        CancelThumbnailGeneration();
    }

    public void SetPresentation(bool dark, bool transparentBackground)
    {
        if (_darkTheme == dark && _transparentBackground == transparentBackground)
            return;
        _darkTheme = dark;
        _transparentBackground = transparentBackground;
        RefreshVisible();
    }

    public void UpdateSelection(IReadOnlyList<string> selectedIds, bool selectionMode)
    {
        _selectionMode = selectionMode;
        _selectedIds = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        RefreshVisible();
    }

    public void UpdateVisibleRange(int first, int last)
    {
        _visibleFirst = Math.Max(0, first);
        _visibleLast = Math.Max(_visibleFirst, last);
    }

    public void SetScrolling(bool scrolling)
    {
        if (_scrolling == scrolling)
            return;

        _scrolling = scrolling;
        if (scrolling)
            CancelThumbnailGeneration();
    }

    public void StartVisibleThumbnailWork()
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
    }

    public override long GetItemId(int position)
    {
        // The logical slot is the stable RecyclerView item. A placeholder later receiving Graph
        // metadata must not change its stable ID, otherwise RecyclerView can invalidate/rebind a
        // large portion of the viewport during background page arrival.
        return long.MinValue + position;
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = new NativeFileItemView(parent.Context ?? _context);
        return new NativeFileViewHolder(view, this, _host);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is not NativeFileViewHolder fileHolder || _viewModel is null ||
            position < 0 || position >= _viewModel.MobileItems.Count)
            return;

        var slot = _viewModel.MobileItems[position];
        var item = slot.Item;
        Bitmap? cachedBitmap = null;
        if (item is not null)
            TryGetBitmap(item, out cachedBitmap);

        fileHolder.Bind(slot, Mode, _darkTheme, _transparentBackground, _selectionMode,
            item is not null && _selectedIds.Contains(item.Id), cachedBitmap);

        if (!_scrolling && cachedBitmap is null)
            RequestThumbnailIfNeeded(fileHolder, position);
    }

    public override void OnViewRecycled(Java.Lang.Object holder)
    {
        if (holder is NativeFileViewHolder fileHolder)
            fileHolder.Unbind();
        base.OnViewRecycled(holder);
    }

    public override void OnViewDetachedFromWindow(Java.Lang.Object holder)
    {
        if (holder is NativeFileViewHolder fileHolder)
            fileHolder.CancelThumbnailBinding();
        base.OnViewDetachedFromWindow(holder);
    }

    internal void RebindHolder(NativeFileViewHolder holder)
    {
        if (_disposed)
            return;
        var position = holder.BindingPosition;
        if (position < 0 || position >= ItemCount)
            return;
        OnBindViewHolder(holder, position);
    }

    private void MobileItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed)
            return;

        CancelThumbnailGeneration();
        _recycler.Post(() =>
        {
            if (!_disposed)
                NotifyDataSetChanged();
        });
    }

    private DriveItemModel? GetItem(int position)
    {
        if (_viewModel is null || position < 0 || position >= _viewModel.MobileItems.Count)
            return null;
        return _viewModel.MobileItems[position].Item;
    }

    private void RequestThumbnailIfNeeded(NativeFileViewHolder holder, int position)
    {
        if (_disposed || _scrolling || _viewModel is null || position < 0 || position >= ItemCount)
            return;

        var item = _viewModel.MobileItems[position].Item;
        if (item is null || !item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return;

        if (TryGetBitmap(item, out var existing) && existing is not null)
        {
            holder.ApplyThumbnail(item.Id, existing);
            return;
        }

        if (!_loadingIds.TryAdd(item.Id, 0))
            return;

        var generationToken = _thumbnailGenerationCts.Token;
        holder.MarkThumbnailRequest(item.Id);
        _ = LoadThumbnailAsync(holder, item, generationToken);
    }

    private async Task LoadThumbnailAsync(NativeFileViewHolder holder, DriveItemModel item, CancellationToken generationToken)
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
                var targetPx = Mode switch
                {
                    FileViewMode.ExtraLargeIcons => 320,
                    FileViewMode.LargeIcons => 256,
                    _ => 128
                };

                var bitmap = await Task.Run(() => DecodeScaled(path, targetPx), generationToken).ConfigureAwait(false);
                if (bitmap is null)
                    return;

                AddBitmapToCache(item, bitmap);
                if (generationToken.IsCancellationRequested)
                    return;

                _recycler.Post(() =>
                {
                    if (_disposed || _scrolling)
                        return;
                    holder.ApplyThumbnail(item.Id, bitmap);
                });
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (System.OperationCanceledException)
        {
            // Normal while a native fling is in progress or the folder changes.
        }
        catch
        {
            // Thumbnail failures are cosmetic. The shared OneDrive layer already retries transient
            // network problems; leave the type badge visible when a thumbnail is unavailable.
        }
        finally
        {
            _loadingIds.TryRemove(item.Id, out _);
        }
    }

    private Bitmap? DecodeScaled(string path, int targetPx)
    {
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
                return null;

            var sample = 1;
            var largest = Math.Max(bounds.OutWidth, bounds.OutHeight);
            while (largest / (sample * 2) >= targetPx)
                sample *= 2;

            var options = new BitmapFactory.Options
            {
                InSampleSize = Math.Max(1, sample),
                InPreferredConfig = Bitmap.Config.Argb8888
            };
            return BitmapFactory.DecodeFile(path, options);
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetBitmap(DriveItemModel item, out Bitmap? bitmap)
    {
        var key = BitmapKey(item);
        lock (_bitmapCacheGate)
        {
            if (!_bitmapCache.TryGetValue(key, out bitmap) || bitmap is null || bitmap.IsRecycled)
            {
                bitmap = null;
                return false;
            }

            TouchBitmapLru(key);
            return true;
        }
    }

    private void AddBitmapToCache(DriveItemModel item, Bitmap bitmap)
    {
        var key = BitmapKey(item);
        lock (_bitmapCacheGate)
        {
            _bitmapCache[key] = bitmap;
            TouchBitmapLru(key);
            while (_bitmapLru.Count > BitmapCacheLimit)
            {
                var oldest = _bitmapLru.First;
                if (oldest is null)
                    break;
                _bitmapLru.RemoveFirst();
                _bitmapLruNodes.Remove(oldest.Value);
                _bitmapCache.Remove(oldest.Value);
                // Do not Dispose here: a currently attached native item may still be drawing this
                // bitmap. Once no View/cache reference remains Android's GC releases it safely.
            }
        }
    }

    private void TouchBitmapLru(string key)
    {
        if (_bitmapLruNodes.TryGetValue(key, out var node))
        {
            _bitmapLru.Remove(node);
            _bitmapLru.AddLast(node);
            return;
        }

        var created = _bitmapLru.AddLast(key);
        _bitmapLruNodes[key] = created;
    }

    private string BitmapKey(DriveItemModel item) => $"{item.Id}|{item.VersionToken}|{Mode}";

    private void RefreshVisible()
    {
        if (_disposed || ItemCount == 0)
            return;
        var first = Math.Clamp(_visibleFirst, 0, ItemCount - 1);
        var last = Math.Clamp(_visibleLast, first, ItemCount - 1);
        NotifyItemRangeChanged(first, Math.Max(1, last - first + 1));
    }

    private void CancelThumbnailGeneration()
    {
        var previous = Interlocked.Exchange(ref _thumbnailGenerationCts, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
        _loadingIds.Clear();
    }


    public new void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_viewModel is not null)
            _viewModel.MobileItems.CollectionChanged -= MobileItems_CollectionChanged;
        _viewModel = null;

        CancelThumbnailGeneration();
        _thumbnailGenerationCts.Dispose();

        lock (_bitmapCacheGate)
        {
            _bitmapCache.Clear();
            _bitmapLru.Clear();
            _bitmapLruNodes.Clear();
        }
        _thumbnailGate.Dispose();
    }
}

internal sealed class NativeFileViewHolder : RecyclerView.ViewHolder
{
    private readonly NativeFileItemView _view;
    private readonly NativeFileAdapter _owner;
    private readonly NativeMobileFileListHost _host;
    private VirtualDriveItemSlot? _slot;
    private int _refreshQueued;
    private string? _thumbnailRequestItemId;

    public NativeFileViewHolder(NativeFileItemView view, NativeFileAdapter owner, NativeMobileFileListHost host)
        : base(view)
    {
        _view = view;
        _owner = owner;
        _host = host;
        _view.Click += View_Click;
        _view.LongClick += View_LongClick;
    }

    public int BindingPosition => BindingAdapterPosition;

    public void Bind(
        VirtualDriveItemSlot slot,
        FileViewMode mode,
        bool darkTheme,
        bool transparentBackground,
        bool selectionMode,
        bool selected,
        Bitmap? bitmap)
    {
        if (!ReferenceEquals(_slot, slot))
        {
            if (_slot is not null)
                _slot.PropertyChanged -= Slot_PropertyChanged;
            _slot = slot;
            _slot.PropertyChanged += Slot_PropertyChanged;
        }

        _thumbnailRequestItemId = null;
        _view.Bind(slot.Item, mode, darkTheme, transparentBackground, selectionMode, selected, bitmap);
    }

    public void MarkThumbnailRequest(string itemId) => _thumbnailRequestItemId = itemId;

    public void ApplyThumbnail(string itemId, Bitmap bitmap)
    {
        if (_slot?.Item is not { } item ||
            !string.Equals(item.Id, itemId, StringComparison.Ordinal) ||
            bitmap.IsRecycled)
            return;

        _thumbnailRequestItemId = null;
        _view.SetThumbnail(bitmap);
    }

    public void CancelThumbnailBinding() => _thumbnailRequestItemId = null;

    public void Unbind()
    {
        if (_slot is not null)
            _slot.PropertyChanged -= Slot_PropertyChanged;
        _slot = null;
        _thumbnailRequestItemId = null;
        _view.Bind(null, _view.Mode, _view.DarkTheme, _view.TransparentBackground, false, false, null);
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(VirtualDriveItemSlot.Item) and
            not nameof(VirtualDriveItemSlot.Name) and
            not nameof(VirtualDriveItemSlot.SizeDisplay))
            return;

        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;

        ItemView.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_slot is not null)
                _owner.RebindHolder(this);
        });
    }

    private void View_Click(object? sender, EventArgs e)
    {
        if (_slot?.Item is { } item)
            _host.RaiseItemTapped(item);
    }

    private void View_LongClick(object? sender, View.LongClickEventArgs e)
    {
        if (_slot?.Item is not { } item)
            return;

        ItemView.PerformHapticFeedback(FeedbackConstants.LongPress);
        _host.RaiseItemLongPressed(item);
        e.Handled = true;
    }
}

/// <summary>
/// One native Android View per realized file cell. Text, badges, the Windows-11-style folder glyph,
/// selection affordance and thumbnail are drawn directly on a Canvas to minimize View hierarchy/measure overhead.
/// </summary>
internal sealed class NativeFileItemView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    private readonly Paint _textPaint = new(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
    private readonly Paint _secondaryTextPaint = new(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
    private readonly global::Android.Graphics.Path _folderPath = new();
    private DriveItemModel? _item;
    private Bitmap? _thumbnail;
    private bool _selectionMode;
    private bool _selected;

    public NativeFileItemView(Context context) : base(context)
    {
        Clickable = true;
        LongClickable = true;
        Focusable = true;
        SetBackgroundColor(Color.Transparent);
        SetWillNotDraw(false);
        SetPadding(0, 0, 0, 0);
        SetMinimumHeight(DesiredHeightPx());
    }

    public FileViewMode Mode { get; private set; } = FileViewMode.Details;
    public bool DarkTheme { get; private set; }
    public bool TransparentBackground { get; private set; }

    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool transparentBackground, bool selectionMode, bool selected, Bitmap? thumbnail)
    {
        var modeChanged = Mode != mode;
        _item = item;
        Mode = mode;
        DarkTheme = darkTheme;
        TransparentBackground = transparentBackground;
        _selectionMode = selectionMode;
        _selected = selected;
        _thumbnail = thumbnail;

        // Row/cell height depends only on the view mode. Avoid RequestLayout on every recycled
        // OnBindViewHolder call; a fast fling should be draw/bind work, not repeated measure work.
        if (modeChanged)
        {
            SetMinimumHeight(DesiredHeightPx());
            RequestLayout();
        }
        Invalidate();
    }

    public void SetThumbnail(Bitmap bitmap)
    {
        _thumbnail = bitmap;
        Invalidate();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var width = MeasureSpec.GetSize(widthMeasureSpec);
        if (width <= 0)
            width = SuggestedMinimumWidth;
        SetMeasuredDimension(width, DesiredHeightPx());
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0)
            return;

        if (TransparentBackground)
        {
            // Clear the recycled native cell buffer so the Avalonia custom background below the
            // NativeControlHost remains visible instead of retaining a previous opaque frame.
            canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear);
        }
        else
        {
            var bg = DarkTheme ? Color.Rgb(18, 18, 18) : Color.Rgb(250, 250, 250);
            canvas.DrawColor(bg);
        }

        if (_selected)
        {
            _paint.Color = DarkTheme ? Color.Argb(120, 47, 128, 237) : Color.Argb(56, 47, 128, 237);
            canvas.DrawRoundRect(Dp(4), Dp(2), width - Dp(4), height - Dp(2), Dp(8), Dp(8), _paint);
        }

        if (_item is null)
        {
            DrawPlaceholder(canvas, width, height);
            return;
        }

        if (Mode == FileViewMode.Details)
            DrawDetails(canvas, width, height, _item);
        else
            DrawGrid(canvas, width, height, _item);

        if (_selectionMode)
            DrawSelectionCircle(canvas, width, _selected);
    }

    private void DrawDetails(Canvas canvas, int width, int height, DriveItemModel item)
    {
        var iconSize = Dp(36);
        var iconLeft = Dp(8);
        var iconTop = (height - iconSize) / 2f;
        DrawItemVisual(canvas, item, new RectF(iconLeft, iconTop, iconLeft + iconSize, iconTop + iconSize));

        ConfigureTextPaint(primary: true, Sp(14));
        var nameLeft = Dp(54);
        var rightPadding = Dp(10);
        var sizeWidth = Math.Min(Dp(88), Math.Max(Dp(60), width / 4));
        var nameMaxWidth = Math.Max(Dp(30), width - nameLeft - sizeWidth - rightPadding);
        var baseline = height / 2f + Sp(5);
        canvas.DrawText(Ellipsize(item.Name, _textPaint, nameMaxWidth), nameLeft, baseline, _textPaint);

        ConfigureTextPaint(primary: false, Sp(12));
        _secondaryTextPaint.TextAlign = Paint.Align.Right;
        canvas.DrawText(Ellipsize(item.SizeDisplay, _secondaryTextPaint, sizeWidth), width - rightPadding, baseline, _secondaryTextPaint);
        _secondaryTextPaint.TextAlign = Paint.Align.Left;

        _paint.Color = DarkTheme ? Color.Argb(34, 255, 255, 255) : Color.Argb(24, 0, 0, 0);
        canvas.DrawRect(nameLeft, height - 1, width, height, _paint);
    }

    private void DrawGrid(Canvas canvas, int width, int height, DriveItemModel item)
    {
        var extra = Mode == FileViewMode.ExtraLargeIcons;
        var visualSize = Math.Min(width - Dp(18), Dp(extra ? 116 : 82));
        var visualLeft = (width - visualSize) / 2f;
        var visualTop = Dp(extra ? 12 : 9);
        DrawItemVisual(canvas, item, new RectF(visualLeft, visualTop, visualLeft + visualSize, visualTop + visualSize));

        ConfigureTextPaint(primary: true, Sp(extra ? 14 : 13));
        _textPaint.TextAlign = Paint.Align.Center;
        var nameY = visualTop + visualSize + Dp(extra ? 25 : 22);
        canvas.DrawText(Ellipsize(item.Name, _textPaint, width - Dp(14)), width / 2f, nameY, _textPaint);

        ConfigureTextPaint(primary: false, Sp(11));
        _secondaryTextPaint.TextAlign = Paint.Align.Center;
        canvas.DrawText(Ellipsize(item.SizeDisplay, _secondaryTextPaint, width - Dp(18)), width / 2f, nameY + Dp(18), _secondaryTextPaint);
        _textPaint.TextAlign = Paint.Align.Left;
        _secondaryTextPaint.TextAlign = Paint.Align.Left;
    }

    private void DrawItemVisual(Canvas canvas, DriveItemModel item, RectF rect)
    {
        if (_thumbnail is { IsRecycled: false })
        {
            _paint.Color = Color.White;
            canvas.DrawRoundRect(rect, Dp(6), Dp(6), _paint);
            var src = new Rect(0, 0, _thumbnail.Width, _thumbnail.Height);
            var fitted = CenterCropRect(rect, _thumbnail.Width, _thumbnail.Height);
            canvas.Save();
            canvas.ClipRect(rect);
            canvas.DrawBitmap(_thumbnail, src, fitted, _paint);
            canvas.Restore();

            if (item.IsVideo)
                DrawPlayBadge(canvas, rect);
            return;
        }

        if (item.IsFolder)
        {
            DrawFolder(canvas, rect);
            return;
        }

        DrawFileBadge(canvas, item, rect);
    }

    private void DrawFolder(Canvas canvas, RectF rect)
    {
        // Four-layer yellow folder matched to the supplied OneDrive folder artwork.
        // Keep the hot path allocation-free: the same reusable Path/Paint are used for every cell.
        const float sourceWidth = 32f;
        const float sourceHeight = 26f;
        var scale = Math.Min(rect.Width() / sourceWidth, rect.Height() / sourceHeight);
        var left = rect.CenterX() - sourceWidth * scale / 2f;
        var top = rect.CenterY() - sourceHeight * scale / 2f;
        float X(float x) => left + x * scale;
        float Y(float y) => top + y * scale;

        // Golden rear shell with the long sloped tab from the reference image.
        _folderPath.Reset();
        _folderPath.MoveTo(X(0f), Y(7.5f));
        _folderPath.CubicTo(X(0f), Y(5.1f), X(1.9f), Y(3.1f), X(4.3f), Y(3.1f));
        _folderPath.LineTo(X(9.7f), Y(3.1f));
        _folderPath.CubicTo(X(10.7f), Y(3.1f), X(11.4f), Y(3.4f), X(12.2f), Y(4f));
        _folderPath.LineTo(X(15.2f), Y(6.3f));
        _folderPath.CubicTo(X(16.2f), Y(7.1f), X(17.3f), Y(7.4f), X(18.7f), Y(7.4f));
        _folderPath.LineTo(X(29.5f), Y(7.4f));
        _folderPath.CubicTo(X(30.9f), Y(7.4f), X(32f), Y(8.5f), X(32f), Y(9.9f));
        _folderPath.LineTo(X(32f), Y(22.3f));
        _folderPath.CubicTo(X(32f), Y(24.3f), X(30.3f), Y(26f), X(28.3f), Y(26f));
        _folderPath.LineTo(X(3.7f), Y(26f));
        _folderPath.CubicTo(X(1.7f), Y(26f), X(0f), Y(24.3f), X(0f), Y(22.3f));
        _folderPath.Close();
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(247, 188, 15);
        canvas.DrawPath(_folderPath, _paint);

        // Peach rear insert.
        _paint.Color = Color.Rgb(255, 210, 141);
        canvas.DrawRoundRect(X(4.4f), Y(8.8f), X(28.9f), Y(21.4f),
            Math.Max(1f, 1.2f * scale), Math.Max(1f, 1.2f * scale), _paint);

        // Cream inner sheet.
        _paint.Color = Color.Rgb(255, 242, 214);
        canvas.DrawRoundRect(X(3.2f), Y(10.1f), X(27.7f), Y(21.9f),
            Math.Max(1f, 1.2f * scale), Math.Max(1f, 1.2f * scale), _paint);

        // Broad pale-yellow front cover.
        _paint.Color = Color.Rgb(255, 215, 107);
        canvas.DrawRoundRect(X(0f), Y(11.5f), X(32f), Y(26f),
            Math.Max(1f, 2.1f * scale), Math.Max(1f, 2.1f * scale), _paint);

        // Very soft top sheen preserves the light-at-the-top look of the supplied artwork.
        _paint.Color = Color.Argb(88, 255, 233, 176);
        canvas.DrawRoundRect(X(2f), Y(12.2f), X(30f), Y(13.4f),
            Math.Max(1f, 0.6f * scale), Math.Max(1f, 0.6f * scale), _paint);
    }

    private void DrawFileBadge(Canvas canvas, DriveItemModel item, RectF rect)
    {
        if (item.IsImage)
        {
            DrawImagePlaceholder(canvas, rect);
            return;
        }

        if (item.IsVideo)
        {
            DrawVideoPlaceholder(canvas, rect);
            return;
        }

        if (item.IsAudio)
        {
            DrawAudioPlaceholder(canvas, rect);
            return;
        }

        DrawDocumentPlaceholder(canvas, item, rect);
    }

    private void DrawImagePlaceholder(Canvas canvas, RectF rect)
    {
        float X(float value) => rect.Left + rect.Width() * value;
        float Y(float value) => rect.Top + rect.Height() * value;
        var radius = Math.Min(rect.Width(), rect.Height()) * 0.22f;

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(238, 247, 255);
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(Dp(1), rect.Width() * 0.04f);
        _paint.Color = Color.Rgb(142, 197, 255);
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _folderPath.Reset();
        _folderPath.MoveTo(X(0.18f), Y(0.75f));
        _folderPath.LineTo(X(0.36f), Y(0.53f));
        _folderPath.LineTo(X(0.50f), Y(0.65f));
        _folderPath.LineTo(X(0.64f), Y(0.50f));
        _folderPath.LineTo(X(0.82f), Y(0.75f));
        _paint.Color = Color.Rgb(59, 130, 246);
        _paint.StrokeWidth = Math.Max(Dp(1.4f), rect.Width() * 0.055f);
        _paint.StrokeCap = Paint.Cap.Round;
        _paint.StrokeJoin = Paint.Join.Round;
        canvas.DrawPath(_folderPath, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(253, 186, 116);
        canvas.DrawCircle(X(0.31f), Y(0.31f), Math.Min(rect.Width(), rect.Height()) * 0.085f, _paint);
    }

    private void DrawVideoPlaceholder(Canvas canvas, RectF rect)
    {
        var radius = Math.Min(rect.Width(), rect.Height()) * 0.22f;
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(238, 242, 255);
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(Dp(1), rect.Width() * 0.035f);
        _paint.Color = Color.Rgb(165, 180, 252);
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(124, 58, 237);
        var playRadius = Math.Min(rect.Width(), rect.Height()) * 0.285f;
        canvas.DrawCircle(rect.CenterX(), rect.CenterY(), playRadius, _paint);

        _folderPath.Reset();
        _folderPath.MoveTo(rect.CenterX() - playRadius * 0.28f, rect.CenterY() - playRadius * 0.48f);
        _folderPath.LineTo(rect.CenterX() + playRadius * 0.55f, rect.CenterY());
        _folderPath.LineTo(rect.CenterX() - playRadius * 0.28f, rect.CenterY() + playRadius * 0.48f);
        _folderPath.Close();
        _paint.Color = Color.White;
        canvas.DrawPath(_folderPath, _paint);
    }

    private void DrawAudioPlaceholder(Canvas canvas, RectF rect)
    {
        float X(float value) => rect.Left + rect.Width() * value;
        float Y(float value) => rect.Top + rect.Height() * value;
        var min = Math.Min(rect.Width(), rect.Height());

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(96, 165, 250);
        canvas.DrawCircle(X(0.42f), Y(0.58f), min * 0.36f, _paint);
        _paint.Color = Color.Rgb(251, 113, 133);
        canvas.DrawCircle(X(0.58f), Y(0.42f), min * 0.36f, _paint);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.Color = Color.White;
        _paint.StrokeWidth = Math.Max(Dp(1.6f), min * 0.07f);
        _paint.StrokeCap = Paint.Cap.Round;
        _paint.StrokeJoin = Paint.Join.Round;
        _folderPath.Reset();
        _folderPath.MoveTo(X(0.40f), Y(0.30f));
        _folderPath.LineTo(X(0.40f), Y(0.70f));
        _folderPath.MoveTo(X(0.40f), Y(0.30f));
        _folderPath.LineTo(X(0.70f), Y(0.23f));
        _folderPath.LineTo(X(0.70f), Y(0.60f));
        canvas.DrawPath(_folderPath, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        canvas.DrawOval(new RectF(X(0.23f), Y(0.64f), X(0.46f), Y(0.82f)), _paint);
        canvas.DrawOval(new RectF(X(0.53f), Y(0.54f), X(0.76f), Y(0.72f)), _paint);
    }

    private void DrawDocumentPlaceholder(Canvas canvas, DriveItemModel item, RectF rect)
    {
        var background = item.IsPdf ? Color.Rgb(255, 245, 245)
            : item.IsWord ? Color.Rgb(239, 246, 255)
            : item.IsExcel ? Color.Rgb(240, 253, 244)
            : item.IsPowerPoint ? Color.Rgb(255, 247, 237)
            : item.IsArchive ? Color.Rgb(245, 243, 255)
            : item.IsUrlShortcut ? Color.Rgb(240, 253, 250)
            : Color.Rgb(248, 250, 252);
        var border = item.IsPdf ? Color.Rgb(240, 160, 168)
            : item.IsWord ? Color.Rgb(147, 197, 253)
            : item.IsExcel ? Color.Rgb(134, 239, 172)
            : item.IsPowerPoint ? Color.Rgb(253, 186, 116)
            : item.IsArchive ? Color.Rgb(196, 181, 253)
            : item.IsUrlShortcut ? Color.Rgb(94, 234, 212)
            : Color.Rgb(203, 213, 225);
        var accent = item.IsPdf ? Color.Rgb(239, 68, 68)
            : item.IsWord ? Color.Rgb(37, 99, 235)
            : item.IsExcel ? Color.Rgb(22, 163, 74)
            : item.IsPowerPoint ? Color.Rgb(249, 115, 22)
            : item.IsArchive ? Color.Rgb(139, 92, 246)
            : item.IsUrlShortcut ? Color.Rgb(14, 165, 164)
            : item.IsText ? Color.Rgb(100, 116, 139)
            : Color.Rgb(96, 165, 250);

        var radius = Math.Min(rect.Width(), rect.Height()) * 0.18f;
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = background;
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(Dp(1), rect.Width() * 0.035f);
        _paint.Color = border;
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = accent;
        var stripTop = rect.Bottom - rect.Height() * 0.30f;
        var strip = new RectF(rect.Left, stripTop, rect.Right, rect.Bottom);
        canvas.DrawRoundRect(strip, radius * 0.65f, radius * 0.65f, _paint);
        canvas.DrawRect(rect.Left, stripTop, rect.Right, stripTop + radius * 0.65f, _paint);

        _textPaint.Color = Color.White;
        _textPaint.TextAlign = Paint.Align.Center;
        _textPaint.TextSize = Math.Min(rect.Height() * 0.16f, Sp(8));
        _textPaint.SetTypeface(Typeface.DefaultBold);
        var labelY = stripTop + (rect.Bottom - stripTop) / 2f - (_textPaint.Ascent() + _textPaint.Descent()) / 2f;
        canvas.DrawText(item.FileBadgeText, rect.CenterX(), labelY, _textPaint);
        _textPaint.TextAlign = Paint.Align.Left;
    }

    private void DrawPlayBadge(Canvas canvas, RectF rect)
    {
        var radius = Math.Min(rect.Width(), rect.Height()) * 0.16f;
        _paint.Color = Color.Argb(205, 20, 20, 20);
        canvas.DrawCircle(rect.CenterX(), rect.CenterY(), radius, _paint);
        _paint.Color = Color.White;
        var path = new global::Android.Graphics.Path();
        path.MoveTo(rect.CenterX() - radius * 0.28f, rect.CenterY() - radius * 0.50f);
        path.LineTo(rect.CenterX() + radius * 0.55f, rect.CenterY());
        path.LineTo(rect.CenterX() - radius * 0.28f, rect.CenterY() + radius * 0.50f);
        path.Close();
        canvas.DrawPath(path, _paint);
        path.Dispose();
    }

    private void DrawSelectionCircle(Canvas canvas, int width, bool selected)
    {
        var r = Dp(10);
        var cx = width - Dp(17);
        var cy = Dp(17);
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = selected ? Color.Rgb(47, 128, 237)
            : (DarkTheme ? Color.Argb(190, 45, 45, 45) : Color.Argb(210, 255, 255, 255));
        canvas.DrawCircle(cx, cy, r, _paint);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Dp(1.5f);
        _paint.Color = selected ? Color.White : (DarkTheme ? Color.LightGray : Color.Gray);
        canvas.DrawCircle(cx, cy, r, _paint);
        if (selected)
        {
            _paint.StrokeWidth = Dp(2f);
            _paint.StrokeCap = Paint.Cap.Round;
            var check = new global::Android.Graphics.Path();
            check.MoveTo(cx - r * 0.45f, cy);
            check.LineTo(cx - r * 0.10f, cy + r * 0.35f);
            check.LineTo(cx + r * 0.52f, cy - r * 0.42f);
            canvas.DrawPath(check, _paint);
            check.Dispose();
        }
        _paint.SetStyle(Paint.Style.Fill);
    }

    private void DrawPlaceholder(Canvas canvas, int width, int height)
    {
        _paint.Color = DarkTheme ? Color.Rgb(39, 39, 39) : Color.Rgb(232, 234, 237);
        if (Mode == FileViewMode.Details)
        {
            canvas.DrawRoundRect(Dp(10), Dp(9), Dp(42), height - Dp(9), Dp(6), Dp(6), _paint);
            canvas.DrawRoundRect(Dp(54), Dp(17), width * 0.62f, Dp(31), Dp(5), Dp(5), _paint);
            return;
        }

        var box = Math.Min(width - Dp(20), Dp(Mode == FileViewMode.ExtraLargeIcons ? 112 : 80));
        var left = (width - box) / 2f;
        canvas.DrawRoundRect(left, Dp(10), left + box, Dp(10) + box, Dp(8), Dp(8), _paint);
        canvas.DrawRoundRect(Dp(12), Dp(20) + box, width - Dp(12), Dp(34) + box, Dp(5), Dp(5), _paint);
    }

    private void ConfigureTextPaint(bool primary, float sizePx)
    {
        var paint = primary ? _textPaint : _secondaryTextPaint;
        paint.Color = primary
            ? (DarkTheme ? Color.Rgb(238, 238, 238) : Color.Rgb(28, 28, 28))
            : (DarkTheme ? Color.Rgb(165, 165, 165) : Color.Rgb(110, 110, 110));
        paint.TextSize = sizePx;
        paint.SetTypeface(Typeface.Default);
        paint.TextAlign = Paint.Align.Left;
    }

    private string Ellipsize(string text, Paint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0 || paint.MeasureText(text) <= maxWidth)
            return text;

        const string ellipsis = "…";
        var ellipsisWidth = paint.MeasureText(ellipsis);
        if (ellipsisWidth >= maxWidth)
            return ellipsis;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var width = paint.MeasureText(text, 0, mid) + ellipsisWidth;
            if (width <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }
        return low <= 0 ? ellipsis : text[..low] + ellipsis;
    }

    private RectF CenterCropRect(RectF dest, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return dest;

        var sourceAspect = sourceWidth / (float)sourceHeight;
        var destAspect = dest.Width() / Math.Max(1f, dest.Height());
        if (sourceAspect > destAspect)
        {
            var scaledWidth = dest.Height() * sourceAspect;
            var left = dest.CenterX() - scaledWidth / 2f;
            return new RectF(left, dest.Top, left + scaledWidth, dest.Bottom);
        }

        var scaledHeight = dest.Width() / sourceAspect;
        var top = dest.CenterY() - scaledHeight / 2f;
        return new RectF(dest.Left, top, dest.Right, top + scaledHeight);
    }

    private int DesiredHeightPx() => Dp(Mode switch
    {
        FileViewMode.ExtraLargeIcons => 188,
        FileViewMode.LargeIcons => 146,
        _ => 48
    });

    private int Dp(float value)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        return Math.Max(1, (int)MathF.Round(value * density));
    }

    private float Sp(float value)
    {
        var metrics = Resources?.DisplayMetrics;
        return metrics is null
            ? value
            : global::Android.Util.TypedValue.ApplyDimension(
                global::Android.Util.ComplexUnitType.Sp,
                value,
                metrics);
    }
}
