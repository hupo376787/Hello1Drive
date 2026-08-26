from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one match, found {count}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Desktop/Avalonia floating upload glyph: use a compact OneDrive-style cloud upload mark.
replace_once(
    "src/Hello1Drive.Core/Views/MainView.axaml",
    '''          <Button x:Name="FloatingUploadButton" Classes="fab" IsHitTestVisible="False">\n            <Path Width="17" Height="17" Stretch="Uniform" Stroke="#FFF7F8" StrokeThickness="1.5" StrokeLineCap="Round"\n                  Data="M7,12 V2 M3.5,5.5 L7,2 L10.5,5.5 M2,12 H12" />\n          </Button>''',
    '''          <Button x:Name="FloatingUploadButton" Classes="fab" IsHitTestVisible="False">\n            <!-- Cloud + upward arrow: recognizable as OneDrive upload at a glance, with enough\n                 internal breathing room to stay crisp inside the 48-DIP floating circle. -->\n            <Path Width="20" Height="20" Stretch="Uniform" Stroke="#FFF7F8" StrokeThickness="1.55"\n                  StrokeLineCap="Round" StrokeJoin="Round" Fill="Transparent"\n                  Data="M4.2,13.1 C2.2,13.1 0.9,11.8 0.9,10 C0.9,8.4 2,7 3.5,6.6 C4,4.2 6,2.5 8.5,2.5 C10.8,2.5 12.8,3.9 13.5,6 C15.6,6.2 17.1,7.8 17.1,9.8 C17.1,11.8 15.7,13.1 13.7,13.1 L4.2,13.1 M9,13.9 V7.2 M6.7,9.5 L9,7.2 L11.3,9.5" />\n          </Button>'''
)

android = "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"

# 2) Pass native scroll direction into the adapter so the just-passed viewport can be warmed first.
replace_once(
    android,
    '''    public void OnScrolled()\n    {\n        if (_disposed)\n            return;\n        _adapter.UpdateVisibleRange(GetFirstVisiblePosition(), GetLastVisiblePosition());\n    }''',
    '''    public void OnScrolled(int dy)\n    {\n        if (_disposed)\n            return;\n        _adapter.UpdateScrollDirection(dy);\n        _adapter.UpdateVisibleRange(GetFirstVisiblePosition(), GetLastVisiblePosition());\n    }'''
)
replace_once(
    android,
    '''        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)\n        {\n            base.OnScrolled(recyclerView, dx, dy);\n            owner.OnScrolled();\n        }''',
    '''        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)\n        {\n            base.OnScrolled(recyclerView, dx, dy);\n            owner.OnScrolled(dy);\n        }'''
)

# 3) Replace the minimal arrow/tray drawing with the same cloud-upload glyph used by Avalonia.
replace_once(
    android,
    '''    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);\n    private readonly Paint _iconPaint = new(PaintFlags.AntiAlias);\n    private readonly float _touchSlop;''',
    '''    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);\n    private readonly Paint _iconPaint = new(PaintFlags.AntiAlias);\n    private readonly global::Android.Graphics.Path _iconPath = new();\n    private readonly float _touchSlop;'''
)
replace_once(
    android,
    '''        _fillPaint.Color = Color.Rgb(253, 111, 113);\n        _fillPaint.SetStyle(Paint.Style.Fill);\n        _iconPaint.Color = Color.Rgb(255, 247, 248);\n        _iconPaint.SetStyle(Paint.Style.Stroke);\n        _iconPaint.StrokeWidth = Dp(1.5f);\n        _iconPaint.StrokeCap = Paint.Cap.Round;\n        _iconPaint.StrokeJoin = Paint.Join.Round;''',
    '''        _fillPaint.Color = Color.Rgb(253, 111, 113);\n        _fillPaint.SetStyle(Paint.Style.Fill);\n        _iconPaint.Color = Color.Rgb(255, 247, 248);\n        _iconPaint.SetStyle(Paint.Style.Stroke);\n        // The icon is authored in an 18 x 18 logical box and scaled by Canvas, so keep this\n        // stroke in logical units. It lands at about 1.8-2.0 dp on a 48 dp FAB.\n        _iconPaint.StrokeWidth = 1.55f;\n        _iconPaint.StrokeCap = Paint.Cap.Round;\n        _iconPaint.StrokeJoin = Paint.Join.Round;\n\n        BuildUploadIconPath();'''
)
replace_once(
    android,
    '''        var scale = diameter / 14f;\n        var offsetX = (Width - 14f * scale) / 2f;\n        var offsetY = (Height - 14f * scale) / 2f;\n        float X(float value) => offsetX + value * scale;\n        float Y(float value) => offsetY + value * scale;\n\n        canvas.DrawLine(X(7), Y(12), X(7), Y(2), _iconPaint);\n        canvas.DrawLine(X(3.5f), Y(5.5f), X(7), Y(2), _iconPaint);\n        canvas.DrawLine(X(7), Y(2), X(10.5f), Y(5.5f), _iconPaint);\n        canvas.DrawLine(X(2), Y(12), X(12), Y(12), _iconPaint);''',
    '''        // Keep the glyph around 46% of the FAB diameter: large enough to read instantly,\n        // but with a clear ring of coral around it so it never looks cramped.\n        var iconSize = diameter * 0.46f;\n        var scale = iconSize / 18f;\n        var offsetX = (Width - iconSize) / 2f;\n        var offsetY = (Height - iconSize) / 2f - diameter * 0.012f;\n        var saveCount = canvas.Save();\n        canvas.Translate(offsetX, offsetY);\n        canvas.Scale(scale, scale);\n        canvas.DrawPath(_iconPath, _iconPaint);\n        canvas.RestoreToCount(saveCount);'''
)
replace_once(
    android,
    '''    public override bool OnTouchEvent(MotionEvent? e)\n    {''',
    '''    private void BuildUploadIconPath()\n    {\n        // Cloud outline. The bottom edge stays simple so the upward arrow remains the focal point.\n        _iconPath.MoveTo(4.2f, 13.1f);\n        _iconPath.CubicTo(2.2f, 13.1f, 0.9f, 11.8f, 0.9f, 10.0f);\n        _iconPath.CubicTo(0.9f, 8.4f, 2.0f, 7.0f, 3.5f, 6.6f);\n        _iconPath.CubicTo(4.0f, 4.2f, 6.0f, 2.5f, 8.5f, 2.5f);\n        _iconPath.CubicTo(10.8f, 2.5f, 12.8f, 3.9f, 13.5f, 6.0f);\n        _iconPath.CubicTo(15.6f, 6.2f, 17.1f, 7.8f, 17.1f, 9.8f);\n        _iconPath.CubicTo(17.1f, 11.8f, 15.7f, 13.1f, 13.7f, 13.1f);\n        _iconPath.LineTo(4.2f, 13.1f);\n\n        // Upload arrow. Let the stem extend just below the cloud baseline for a clearer silhouette.\n        _iconPath.MoveTo(9.0f, 13.9f);\n        _iconPath.LineTo(9.0f, 7.2f);\n        _iconPath.MoveTo(6.7f, 9.5f);\n        _iconPath.LineTo(9.0f, 7.2f);\n        _iconPath.LineTo(11.3f, 9.5f);\n    }\n\n    public override bool OnTouchEvent(MotionEvent? e)\n    {'''
)

# 4) Track the last native scroll direction and prioritize the viewport the user just passed.
replace_once(
    android,
    '''    private int _visibleFirst;\n    private int _visibleLast;\n    private bool _disposed;''',
    '''    private int _visibleFirst;\n    private int _visibleLast;\n    // +1 means the last meaningful movement was downward, -1 upward. The idle prefetch pass\n    // warms the just-passed viewport first so a one-screen reversal never shows stale badges.\n    private int _lastScrollDirection = 1;\n    private bool _disposed;'''
)
replace_once(
    android,
    '''    public void UpdateVisibleRange(int first, int last)\n    {\n        _visibleFirst = Math.Max(0, first);\n        _visibleLast = Math.Max(_visibleFirst, last);\n    }\n\n    public void SetScrolling(bool scrolling)''',
    '''    public void UpdateVisibleRange(int first, int last)\n    {\n        _visibleFirst = Math.Max(0, first);\n        _visibleLast = Math.Max(_visibleFirst, last);\n    }\n\n    public void UpdateScrollDirection(int dy)\n    {\n        if (dy > 0)\n            _lastScrollDirection = 1;\n        else if (dy < 0)\n            _lastScrollDirection = -1;\n    }\n\n    public void SetScrolling(bool scrolling)'''
)
replace_once(
    android,
    '''        // A "page" means one current viewport, not one Graph 200-item metadata page. This keeps\n        // work proportional to the screen size while making the previous/next viewport warm.\n        var pageSize = Math.Max(1, last - first + 1);\n        for (var distance = 1; distance <= pageSize; distance++)\n        {\n            PrefetchThumbnailIfNeeded(last + distance);\n            PrefetchThumbnailIfNeeded(first - distance);\n        }''',
    '''        // A "page" means one current viewport, not one Graph 200-item metadata page. Queue the\n        // viewport the user just passed before the forward look-ahead viewport. This matters after\n        // a fast fling: reversing by one screen should reveal already-decoded thumbnails.\n        var pageSize = Math.Max(1, last - first + 1);\n        if (_lastScrollDirection >= 0)\n        {\n            for (var distance = 1; distance <= pageSize; distance++)\n                PrefetchThumbnailIfNeeded(first - distance);\n            for (var distance = 1; distance <= pageSize; distance++)\n                PrefetchThumbnailIfNeeded(last + distance);\n        }\n        else\n        {\n            for (var distance = 1; distance <= pageSize; distance++)\n                PrefetchThumbnailIfNeeded(last + distance);\n            for (var distance = 1; distance <= pageSize; distance++)\n                PrefetchThumbnailIfNeeded(first - distance);\n        }'''
)

# 5) Critical fix: adjacent prefetch used to populate only the bitmap LRU. RecyclerView can reattach
# an already-bound cached holder without calling OnBindViewHolder, leaving the old placeholder visible.
# Carry the adapter position into the prefetch task and issue a targeted item refresh when it completes.
replace_once(
    android,
    '''        var generationToken = _thumbnailGenerationCts.Token;\n        _ = PrefetchThumbnailAsync(item, generationToken);\n    }\n\n    private async Task PrefetchThumbnailAsync(DriveItemModel item, CancellationToken generationToken)''',
    '''        var generationToken = _thumbnailGenerationCts.Token;\n        _ = PrefetchThumbnailAsync(position, item, generationToken);\n    }\n\n    private async Task PrefetchThumbnailAsync(int position, DriveItemModel item, CancellationToken generationToken)'''
)
replace_once(
    android,
    '''                AddBitmapToCache(item, bitmap);\n            }\n            finally\n            {\n                _thumbnailGate.Release();\n            }''',
    '''                AddBitmapToCache(item, bitmap);\n                PublishPrefetchedThumbnail(position, item.Id);\n            }\n            finally\n            {\n                _thumbnailGate.Release();\n            }'''
)
replace_once(
    android,
    '''    private void RequestThumbnailIfNeeded(NativeFileViewHolder holder, int position)\n    {''',
    '''    private void PublishPrefetchedThumbnail(int position, string itemId)\n    {\n        _recycler.Post(() =>\n        {\n            if (_disposed || _scrolling || _viewModel is null || position < 0 || position >= ItemCount)\n                return;\n\n            var current = _viewModel.MobileItems[position].Item;\n            if (current is null || !string.Equals(current.Id, itemId, StringComparison.Ordinal))\n                return;\n\n            // If RecyclerView still has the holder attached, update it directly. Otherwise mark the\n            // one adapter position dirty. This is the key part: cached detached holders are then\n            // rebound when they come back instead of reappearing with their old no-thumbnail state.\n            if (_recycler.FindViewHolderForAdapterPosition(position) is NativeFileViewHolder holder &&\n                TryGetBitmap(current, out var bitmap) && bitmap is not null)\n            {\n                holder.ApplyThumbnail(itemId, bitmap);\n                return;\n            }\n\n            NotifyItemChanged(position);\n        });\n    }\n\n    private void RequestThumbnailIfNeeded(NativeFileViewHolder holder, int position)\n    {'''
)

# 6) Keep the iOS floating glyph visually aligned with desktop/Android. This only touches drawing.
ios = "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs"
replace_once(
    ios,
    '''        var scale = diameter / 14d;\n        var offsetX = (width - 14d * scale) / 2d;\n        var offsetY = (height - 14d * scale) / 2d;\n        double X(double value) => offsetX + value * scale;\n        double Y(double value) => offsetY + value * scale;\n\n        using var path = new UIBezierPath\n        {\n            LineWidth = (nfloat)Math.Max(1d, 1.5d * scale / 3.4d),\n            LineCapStyle = CGLineCap.Round,\n            LineJoinStyle = CGLineJoin.Round\n        };\n        path.MoveTo(new CGPoint((nfloat)X(7), (nfloat)Y(12)));\n        path.AddLineTo(new CGPoint((nfloat)X(7), (nfloat)Y(2)));\n        path.MoveTo(new CGPoint((nfloat)X(3.5), (nfloat)Y(5.5)));\n        path.AddLineTo(new CGPoint((nfloat)X(7), (nfloat)Y(2)));\n        path.AddLineTo(new CGPoint((nfloat)X(10.5), (nfloat)Y(5.5)));\n        path.MoveTo(new CGPoint((nfloat)X(2), (nfloat)Y(12)));\n        path.AddLineTo(new CGPoint((nfloat)X(12), (nfloat)Y(12)));\n        UIColor.FromRGB(255, 247, 248).SetStroke();\n        path.Stroke();''',
    '''        var iconSize = diameter * 0.46d;\n        var scale = iconSize / 18d;\n        var offsetX = (width - iconSize) / 2d;\n        var offsetY = (height - iconSize) / 2d - diameter * 0.012d;\n        double X(double value) => offsetX + value * scale;\n        double Y(double value) => offsetY + value * scale;\n\n        using var path = new UIBezierPath\n        {\n            LineWidth = (nfloat)1.55d,\n            LineCapStyle = CGLineCap.Round,\n            LineJoinStyle = CGLineJoin.Round\n        };\n\n        // Use short cubic cloud segments so the iOS native FAB matches the Avalonia/Android glyph.\n        path.MoveTo(new CGPoint((nfloat)X(4.2), (nfloat)Y(13.1)));\n        path.AddCurveToPoint(new CGPoint((nfloat)X(0.9), (nfloat)Y(10.0)),\n            new CGPoint((nfloat)X(2.2), (nfloat)Y(13.1)), new CGPoint((nfloat)X(0.9), (nfloat)Y(11.8)));\n        path.AddCurveToPoint(new CGPoint((nfloat)X(3.5), (nfloat)Y(6.6)),\n            new CGPoint((nfloat)X(0.9), (nfloat)Y(8.4)), new CGPoint((nfloat)X(2.0), (nfloat)Y(7.0)));\n        path.AddCurveToPoint(new CGPoint((nfloat)X(8.5), (nfloat)Y(2.5)),\n            new CGPoint((nfloat)X(4.0), (nfloat)Y(4.2)), new CGPoint((nfloat)X(6.0), (nfloat)Y(2.5)));\n        path.AddCurveToPoint(new CGPoint((nfloat)X(13.5), (nfloat)Y(6.0)),\n            new CGPoint((nfloat)X(10.8), (nfloat)Y(2.5)), new CGPoint((nfloat)X(12.8), (nfloat)Y(3.9)));\n        path.AddCurveToPoint(new CGPoint((nfloat)X(17.1), (nfloat)Y(9.8)),\n            new CGPoint((nfloat)X(15.6), (nfloat)Y(6.2)), new CGPoint((nfloat)X(17.1), (nfloat)Y(7.8)));\n        path.AddCurveToPoint(new CGPoint((nfloat)X(13.7), (nfloat)Y(13.1)),\n            new CGPoint((nfloat)X(17.1), (nfloat)Y(11.8)), new CGPoint((nfloat)X(15.7), (nfloat)Y(13.1)));\n        path.AddLineTo(new CGPoint((nfloat)X(4.2), (nfloat)Y(13.1)));\n        path.MoveTo(new CGPoint((nfloat)X(9.0), (nfloat)Y(13.9)));\n        path.AddLineTo(new CGPoint((nfloat)X(9.0), (nfloat)Y(7.2)));\n        path.MoveTo(new CGPoint((nfloat)X(6.7), (nfloat)Y(9.5)));\n        path.AddLineTo(new CGPoint((nfloat)X(9.0), (nfloat)Y(7.2)));\n        path.AddLineTo(new CGPoint((nfloat)X(11.3), (nfloat)Y(9.5)));\n        UIColor.FromRGB(255, 247, 248).SetStroke();\n        path.Stroke();'''
)

print("mobile thumbnail + upload polish applied")
