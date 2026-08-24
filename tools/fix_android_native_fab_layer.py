from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}")
    path.write_text(text.replace(old, new), encoding="utf-8")


host = ROOT / "src/Hello1Drive.Core/Controls/NativeMobileFileListHost.cs"
replace_once(
    host,
    """    public event EventHandler<NativeMobileFileScrollToEventArgs>? ScrollToPositionRequested;\n    public Func<Task>? RefreshRequestedAsync { get; set; }\n\n    public MainViewModel? ViewModel => _viewModel;\n    public IReadOnlyList<string> SelectedIds => _selectedIds;\n    public bool SelectionMode => _selectionMode;\n    public int LastFirstVisibleIndex { get; private set; }\n""",
    """    public event EventHandler<NativeMobileFileScrollToEventArgs>? ScrollToPositionRequested;\n    public event EventHandler? FloatingUploadRequested;\n    public event EventHandler<NativeFloatingUploadPositionEventArgs>? FloatingUploadPositionChanged;\n    public Func<Task>? RefreshRequestedAsync { get; set; }\n\n    public MainViewModel? ViewModel => _viewModel;\n    public IReadOnlyList<string> SelectedIds => _selectedIds;\n    public bool SelectionMode => _selectionMode;\n    public int LastFirstVisibleIndex { get; private set; }\n    public bool FloatingUploadVisible => _viewModel?.ShowFloatingUploadButton == true;\n    public double FloatingUploadX => Math.Clamp(_viewModel?.Settings.FloatingUploadX ?? 0.94, 0, 1);\n    public double FloatingUploadY => Math.Clamp(_viewModel?.Settings.FloatingUploadY ?? 0.90, 0, 1);\n""",
)
replace_once(
    host,
    """    public void ScrollToPosition(int position) =>\n        ScrollToPositionRequested?.Invoke(this, new NativeMobileFileScrollToEventArgs(Math.Max(0, position)));\n\n    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)\n""",
    """    public void ScrollToPosition(int position) =>\n        ScrollToPositionRequested?.Invoke(this, new NativeMobileFileScrollToEventArgs(Math.Max(0, position)));\n\n    public void RaiseFloatingUploadRequested() => FloatingUploadRequested?.Invoke(this, EventArgs.Empty);\n\n    public void RaiseFloatingUploadPositionChanged(double normalizedX, double normalizedY) =>\n        FloatingUploadPositionChanged?.Invoke(\n            this,\n            new NativeFloatingUploadPositionEventArgs(\n                Math.Clamp(normalizedX, 0, 1),\n                Math.Clamp(normalizedY, 0, 1)));\n\n    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)\n""",
)
replace_once(
    host,
    """public sealed class NativeMobileFileItemEventArgs(DriveItemModel item) : EventArgs\n""",
    """public sealed class NativeFloatingUploadPositionEventArgs(double normalizedX, double normalizedY) : EventArgs\n{\n    public double NormalizedX { get; } = normalizedX;\n    public double NormalizedY { get; } = normalizedY;\n}\n\npublic sealed class NativeMobileFileItemEventArgs(DriveItemModel item) : EventArgs\n""",
)

main = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"
replace_once(
    main,
    """        InitializeComponent();\n        MobileDestinationFolderList.ItemsSource = _mobileDestinationFolders;\n""",
    """        InitializeComponent();\n\n        // Android's native RecyclerView lives on a platform View layer above Avalonia. Its upload\n        // FAB is therefore rendered natively as part of that same layer; keep the Avalonia FAB\n        // for desktop/iOS only so RecyclerView cells can never cover the Android button.\n        if (OperatingSystem.IsAndroid())\n            FloatingActionCanvas.IsVisible = false;\n\n        MobileDestinationFolderList.ItemsSource = _mobileDestinationFolders;\n""",
)
replace_once(
    main,
    """        host.ItemTapped += NativeMobileFileListHost_ItemTapped;\n        host.ItemLongPressed += NativeMobileFileListHost_ItemLongPressed;\n        host.ScrollStateChanged += NativeMobileFileListHost_ScrollStateChanged;\n\n        _nativeMobileFileListHost = host;\n""",
    """        host.ItemTapped += NativeMobileFileListHost_ItemTapped;\n        host.ItemLongPressed += NativeMobileFileListHost_ItemLongPressed;\n        host.ScrollStateChanged += NativeMobileFileListHost_ScrollStateChanged;\n        host.FloatingUploadRequested += NativeMobileFileListHost_FloatingUploadRequested;\n        host.FloatingUploadPositionChanged += NativeMobileFileListHost_FloatingUploadPositionChanged;\n\n        _nativeMobileFileListHost = host;\n""",
)
replace_once(
    main,
    """        host.ItemTapped -= NativeMobileFileListHost_ItemTapped;\n        host.ItemLongPressed -= NativeMobileFileListHost_ItemLongPressed;\n        host.ScrollStateChanged -= NativeMobileFileListHost_ScrollStateChanged;\n        NativeMobileFileListContainer.Children.Remove(host);\n""",
    """        host.ItemTapped -= NativeMobileFileListHost_ItemTapped;\n        host.ItemLongPressed -= NativeMobileFileListHost_ItemLongPressed;\n        host.ScrollStateChanged -= NativeMobileFileListHost_ScrollStateChanged;\n        host.FloatingUploadRequested -= NativeMobileFileListHost_FloatingUploadRequested;\n        host.FloatingUploadPositionChanged -= NativeMobileFileListHost_FloatingUploadPositionChanged;\n        NativeMobileFileListContainer.Children.Remove(host);\n""",
)
replace_once(
    main,
    """    private void NativeMobileFileListHost_ScrollStateChanged(object? sender, NativeMobileFileScrollEventArgs e)\n    {\n        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)\n            return;\n\n        vm.SetMobileListScrolling(e.IsScrolling);\n    }\n\n    private void BeginMobileLongPress(DriveItemModel item, Point start, ulong timestamp)\n""",
    """    private void NativeMobileFileListHost_ScrollStateChanged(object? sender, NativeMobileFileScrollEventArgs e)\n    {\n        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)\n            return;\n\n        vm.SetMobileListScrolling(e.IsScrolling);\n    }\n\n    private void NativeMobileFileListHost_FloatingUploadRequested(object? sender, EventArgs e)\n    {\n        if (!OperatingSystem.IsAndroid())\n            return;\n\n        Dispatcher.UIThread.Post(\n            () => UploadButton_Click(FloatingUploadButton, new RoutedEventArgs()),\n            DispatcherPriority.Input);\n    }\n\n    private async void NativeMobileFileListHost_FloatingUploadPositionChanged(\n        object? sender,\n        NativeFloatingUploadPositionEventArgs e)\n    {\n        if (!OperatingSystem.IsAndroid() || DataContext is not MainViewModel vm)\n            return;\n\n        await vm.SaveFloatingUploadPositionAsync(e.NormalizedX, e.NormalizedY);\n    }\n\n    private void BeginMobileLongPress(DriveItemModel item, Point start, ulong timestamp)\n""",
)

android = ROOT / "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"
replace_once(
    android,
    """using Android.OS;\nusing Android.Views;\nusing Avalonia.Android;\n""",
    """using Android.OS;\nusing Android.Views;\nusing Android.Widget;\nusing Avalonia.Android;\n""",
)
replace_once(
    android,
    """    private readonly Context _context;\n    private readonly NativeMobileFileListHost _host;\n    private readonly SwipeRefreshLayout _refresh;\n    private readonly RecyclerView _recycler;\n""",
    """    private readonly Context _context;\n    private readonly NativeMobileFileListHost _host;\n    private readonly FrameLayout _root;\n    private readonly SwipeRefreshLayout _refresh;\n    private readonly RecyclerView _recycler;\n    private readonly NativeFloatingUploadButtonView _floatingUpload;\n""",
)
replace_once(
    android,
    """        _context = context;\n        _host = host;\n\n        _refresh = new SwipeRefreshLayout(context);\n        _recycler = new RecyclerView(context);\n        _adapter = new NativeFileAdapter(context, host, _recycler);\n""",
    """        _context = context;\n        _host = host;\n\n        _root = new FrameLayout(context)\n        {\n            ClipChildren = false,\n            ClipToPadding = false\n        };\n        _refresh = new SwipeRefreshLayout(context);\n        _recycler = new RecyclerView(context);\n        _floatingUpload = new NativeFloatingUploadButtonView(context, host);\n        _adapter = new NativeFileAdapter(context, host, _recycler);\n""",
)
replace_once(
    android,
    """        _refresh.SetOnRefreshListener(_refreshListener);\n        _refresh.AddView(_recycler, new ViewGroup.LayoutParams(\n            ViewGroup.LayoutParams.MatchParent,\n            ViewGroup.LayoutParams.MatchParent));\n\n        _host.HostStateChanged += Host_HostStateChanged;\n        _host.ScrollToPositionRequested += Host_ScrollToPositionRequested;\n        _recycler.LayoutChange += Recycler_LayoutChange;\n\n        SyncHostState(preservePosition: false);\n    }\n\n    public View RootView => _refresh;\n""",
    """        _refresh.SetOnRefreshListener(_refreshListener);\n        _refresh.AddView(_recycler, new ViewGroup.LayoutParams(\n            ViewGroup.LayoutParams.MatchParent,\n            ViewGroup.LayoutParams.MatchParent));\n\n        _root.AddView(_refresh, new FrameLayout.LayoutParams(\n            ViewGroup.LayoutParams.MatchParent,\n            ViewGroup.LayoutParams.MatchParent));\n        var floatingSize = Dp(48);\n        _root.AddView(_floatingUpload, new FrameLayout.LayoutParams(floatingSize, floatingSize));\n\n        _host.HostStateChanged += Host_HostStateChanged;\n        _host.ScrollToPositionRequested += Host_ScrollToPositionRequested;\n        _recycler.LayoutChange += Recycler_LayoutChange;\n        _root.LayoutChange += Root_LayoutChange;\n\n        SyncHostState(preservePosition: false);\n    }\n\n    public View RootView => _root;\n""",
)
replace_once(
    android,
    """        _adapter.UpdateSelection(_host.SelectedIds, _host.SelectionMode);\n        UpdateTheme();\n\n        if (_viewModel is not null)\n            ApplyLayoutManager(_viewModel.ViewMode, preservePosition);\n""",
    """        _adapter.UpdateSelection(_host.SelectedIds, _host.SelectionMode);\n        UpdateTheme();\n        SyncFloatingUpload();\n\n        if (_viewModel is not null)\n            ApplyLayoutManager(_viewModel.ViewMode, preservePosition);\n""",
)
replace_once(
    android,
    """        if (e.PropertyName == nameof(MainViewModel.ViewMode))\n        {\n            ApplyLayoutManager(_viewModel.ViewMode, preservePosition: true);\n            return;\n        }\n\n        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or\n""",
    """        if (e.PropertyName == nameof(MainViewModel.ViewMode))\n        {\n            ApplyLayoutManager(_viewModel.ViewMode, preservePosition: true);\n            return;\n        }\n\n        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))\n        {\n            SyncFloatingUpload();\n            return;\n        }\n\n        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or\n""",
)
replace_once(
    android,
    """    private void ApplyLayoutManager(FileViewMode mode, bool preservePosition)\n""",
    """    private void Root_LayoutChange(object? sender, View.LayoutChangeEventArgs e)\n    {\n        if (!_disposed)\n            PositionFloatingUpload();\n    }\n\n    private void SyncFloatingUpload()\n    {\n        if (_disposed)\n            return;\n\n        _floatingUpload.Visibility = _host.FloatingUploadVisible ? ViewStates.Visible : ViewStates.Gone;\n        if (_floatingUpload.Visibility == ViewStates.Visible)\n            _root.Post(PositionFloatingUpload);\n    }\n\n    private void PositionFloatingUpload()\n    {\n        if (_disposed || _floatingUpload.Visibility != ViewStates.Visible)\n            return;\n\n        var buttonWidth = _floatingUpload.Width > 0 ? _floatingUpload.Width : Dp(48);\n        var buttonHeight = _floatingUpload.Height > 0 ? _floatingUpload.Height : Dp(48);\n        var maxX = Math.Max(0, _root.Width - buttonWidth);\n        var maxY = Math.Max(0, _root.Height - buttonHeight);\n        if (maxX <= 0 && maxY <= 0)\n            return;\n\n        _floatingUpload.X = (float)(Math.Clamp(_host.FloatingUploadX, 0, 1) * maxX);\n        _floatingUpload.Y = (float)(Math.Clamp(_host.FloatingUploadY, 0, 1) * maxY);\n        _floatingUpload.BringToFront();\n    }\n\n    private void ApplyLayoutManager(FileViewMode mode, bool preservePosition)\n""",
)
replace_once(
    android,
    """        _host.HostStateChanged -= Host_HostStateChanged;\n        _host.ScrollToPositionRequested -= Host_ScrollToPositionRequested;\n        _recycler.LayoutChange -= Recycler_LayoutChange;\n        _recycler.RemoveOnScrollListener(_scrollListener);\n        _refresh.SetOnRefreshListener(null);\n""",
    """        _host.HostStateChanged -= Host_HostStateChanged;\n        _host.ScrollToPositionRequested -= Host_ScrollToPositionRequested;\n        _recycler.LayoutChange -= Recycler_LayoutChange;\n        _root.LayoutChange -= Root_LayoutChange;\n        _recycler.RemoveOnScrollListener(_scrollListener);\n        _refresh.SetOnRefreshListener(null);\n""",
)
replace_once(
    android,
    """        _adapter.Dispose();\n        _recycler.SetAdapter(null);\n        _recycler.SetLayoutManager(null);\n        _refresh.RemoveAllViews();\n        _recycler.Dispose();\n        _refresh.Dispose();\n        _scrollListener.Dispose();\n""",
    """        _adapter.Dispose();\n        _recycler.SetAdapter(null);\n        _recycler.SetLayoutManager(null);\n        _root.RemoveAllViews();\n        _refresh.RemoveAllViews();\n        _floatingUpload.Dispose();\n        _recycler.Dispose();\n        _refresh.Dispose();\n        _root.Dispose();\n        _scrollListener.Dispose();\n""",
)
replace_once(
    android,
    """internal sealed class NativeFileAdapter : RecyclerView.Adapter, IDisposable\n""",
    """internal sealed class NativeFloatingUploadButtonView : View\n{\n    private readonly NativeMobileFileListHost _host;\n    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);\n    private readonly Paint _iconPaint = new(PaintFlags.AntiAlias);\n    private readonly float _touchSlop;\n    private bool _tracking;\n    private bool _moved;\n    private float _downRawX;\n    private float _downRawY;\n    private float _startX;\n    private float _startY;\n\n    public NativeFloatingUploadButtonView(Context context, NativeMobileFileListHost host) : base(context)\n    {\n        _host = host;\n        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? Dp(6);\n\n        Clickable = true;\n        Focusable = true;\n        ContentDescription = \"上传文件\";\n        Elevation = Dp(8);\n        SetWillNotDraw(false);\n\n        _fillPaint.Color = Color.Rgb(253, 111, 113);\n        _fillPaint.SetStyle(Paint.Style.Fill);\n        _iconPaint.Color = Color.Rgb(255, 247, 248);\n        _iconPaint.SetStyle(Paint.Style.Stroke);\n        _iconPaint.StrokeWidth = Dp(1.5f);\n        _iconPaint.StrokeCap = Paint.Cap.Round;\n        _iconPaint.StrokeJoin = Paint.Join.Round;\n    }\n\n    protected override void OnDraw(Canvas canvas)\n    {\n        base.OnDraw(canvas);\n        if (Width <= 0 || Height <= 0)\n            return;\n\n        var diameter = Math.Min(Width, Height);\n        canvas.DrawCircle(Width / 2f, Height / 2f, diameter / 2f, _fillPaint);\n\n        var scale = diameter / 14f;\n        var offsetX = (Width - 14f * scale) / 2f;\n        var offsetY = (Height - 14f * scale) / 2f;\n        float X(float value) => offsetX + value * scale;\n        float Y(float value) => offsetY + value * scale;\n\n        canvas.DrawLine(X(7), Y(12), X(7), Y(2), _iconPaint);\n        canvas.DrawLine(X(3.5f), Y(5.5f), X(7), Y(2), _iconPaint);\n        canvas.DrawLine(X(7), Y(2), X(10.5f), Y(5.5f), _iconPaint);\n        canvas.DrawLine(X(2), Y(12), X(12), Y(12), _iconPaint);\n    }\n\n    public override bool OnTouchEvent(MotionEvent? e)\n    {\n        if (e is null)\n            return false;\n\n        switch (e.ActionMasked)\n        {\n            case MotionEventActions.Down:\n                _tracking = true;\n                _moved = false;\n                _downRawX = e.RawX;\n                _downRawY = e.RawY;\n                _startX = X;\n                _startY = Y;\n                Parent?.RequestDisallowInterceptTouchEvent(true);\n                BringToFront();\n                return true;\n\n            case MotionEventActions.Move:\n                if (!_tracking)\n                    return false;\n                MoveTo(e.RawX, e.RawY);\n                return true;\n\n            case MotionEventActions.Up:\n                if (!_tracking)\n                    return false;\n                MoveTo(e.RawX, e.RawY);\n                _tracking = false;\n                Parent?.RequestDisallowInterceptTouchEvent(false);\n                if (_moved)\n                    SaveNormalizedPosition();\n                else\n                    PerformClick();\n                return true;\n\n            case MotionEventActions.Cancel:\n                _tracking = false;\n                Parent?.RequestDisallowInterceptTouchEvent(false);\n                return true;\n\n            default:\n                return base.OnTouchEvent(e);\n        }\n    }\n\n    public override bool PerformClick()\n    {\n        base.PerformClick();\n        _host.RaiseFloatingUploadRequested();\n        return true;\n    }\n\n    private void MoveTo(float rawX, float rawY)\n    {\n        var dx = rawX - _downRawX;\n        var dy = rawY - _downRawY;\n        if (!_moved && MathF.Sqrt(dx * dx + dy * dy) >= _touchSlop)\n            _moved = true;\n        if (!_moved || Parent is not View parent)\n            return;\n\n        var maxX = Math.Max(0, parent.Width - Width);\n        var maxY = Math.Max(0, parent.Height - Height);\n        X = Math.Clamp(_startX + dx, 0, maxX);\n        Y = Math.Clamp(_startY + dy, 0, maxY);\n    }\n\n    private void SaveNormalizedPosition()\n    {\n        if (Parent is not View parent)\n            return;\n\n        var maxX = Math.Max(1, parent.Width - Width);\n        var maxY = Math.Max(1, parent.Height - Height);\n        _host.RaiseFloatingUploadPositionChanged(X / maxX, Y / maxY);\n    }\n\n    private float Dp(float value)\n    {\n        var density = Resources?.DisplayMetrics?.Density ?? 1f;\n        return value * density;\n    }\n}\n\ninternal sealed class NativeFileAdapter : RecyclerView.Adapter, IDisposable\n""",
)

# Sanity checks for the final source state.
host_text = host.read_text(encoding="utf-8")
main_text = main.read_text(encoding="utf-8")
android_text = android.read_text(encoding="utf-8")
assert "FloatingUploadRequested" in host_text
assert "FloatingUploadPositionChanged" in host_text
assert "FloatingActionCanvas.IsVisible = false" in main_text
assert "NativeMobileFileListHost_FloatingUploadRequested" in main_text
assert "private readonly FrameLayout _root;" in android_text
assert "NativeFloatingUploadButtonView" in android_text
assert "public View RootView => _root;" in android_text
print("Android native floating upload button layer fix applied.")
