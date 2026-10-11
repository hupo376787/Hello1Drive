using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Hello1Drive.Models;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Controls;

/// <summary>
/// A virtualized desktop ItemsControl. The panel realizes and recycles only viewport-sized
/// batches of lightweight item controls, each with its own retained drawing and slot subscription.
/// All view modes share one Avalonia scroll/selection path, including while overlays are open.
/// </summary>
public sealed partial class DesktopVirtualFileSurface : ItemsControl
{
    public static readonly StyledProperty<FileViewMode> ModeProperty =
        AvaloniaProperty.Register<DesktopVirtualFileSurface, FileViewMode>(nameof(Mode), FileViewMode.Details);

    public static readonly StyledProperty<IBrush?> ItemBackgroundProperty =
        AvaloniaProperty.Register<DesktopVirtualFileSurface, IBrush?>(nameof(ItemBackground));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<DesktopVirtualFileSurface, IBrush?>(nameof(SelectionBrush));

    private const double DetailsRowHeight = 46;
    private const double GridSpacing = 4;
    private const double LargePreferredWidth = 152;
    private const double LargeMinWidth = 136;
    private const double LargeMaxWidth = 184;
    private const double LargeHeight = 162;
    private const double ExtraPreferredWidth = 220;
    private const double ExtraMinWidth = 190;
    private const double ExtraMaxWidth = 276;
    private const double ExtraHeight = 212;

    private static readonly IBrush LightText = new SolidColorBrush(Color.Parse("#E51B1B1F"));
    private static readonly IBrush DarkText = new SolidColorBrush(Color.Parse("#F2F4F4F6"));
    private static readonly IBrush LightMutedText = new SolidColorBrush(Color.Parse("#A31B1B1F"));
    private static readonly IBrush DarkMutedText = new SolidColorBrush(Color.Parse("#A8F4F4F6"));
    private static readonly IBrush LightHover = new SolidColorBrush(Color.Parse("#17FFFFFF"));
    private static readonly IBrush DarkHover = new SolidColorBrush(Color.Parse("#16000000"));
    private static readonly IBrush DefaultSelection = new SolidColorBrush(Color.Parse("#4A2F80ED"));
    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#10000000"));
    private static readonly IBrush ThumbnailBackground = new SolidColorBrush(Color.Parse("#16000000"));
    private static readonly IBrush VideoBadgeBrush = new SolidColorBrush(Color.Parse("#B0000000"));
    private static readonly IBrush WhiteBrush = new SolidColorBrush(Colors.White);
    private static readonly IBrush FolderBack = new SolidColorBrush(Color.Parse("#F7BC0F"));
    private static readonly IBrush FolderPaper = new SolidColorBrush(Color.Parse("#FFF2D6"));
    private static readonly IBrush FolderFront = new SolidColorBrush(Color.Parse("#FFD76B"));
    private static readonly IBrush ImageBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush VideoBrush = new SolidColorBrush(Color.Parse("#7C3AED"));
    private static readonly IBrush AudioBrush = new SolidColorBrush(Color.Parse("#EC4899"));
    private static readonly IBrush PdfBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush WordBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush ExcelBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush PowerPointBrush = new SolidColorBrush(Color.Parse("#F97316"));
    private static readonly IBrush ArchiveBrush = new SolidColorBrush(Color.Parse("#8B5CF6"));
    private static readonly IBrush UrlBrush = new SolidColorBrush(Color.Parse("#0EA5A4"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush GenericBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IBrush LightFileBody = new SolidColorBrush(Color.Parse("#F8FAFC"));
    private static readonly IBrush DarkFileBody = new SolidColorBrush(Color.Parse("#2A2C32"));

    private readonly Dictionary<TextCacheKey, FormattedText> _textCache = [];
    private readonly Queue<TextCacheKey> _textCacheOrder = [];
    private MainViewModel? _vm;
    private double _viewportOffsetY;
    private double _viewportHeight = 800;
    private double _viewportWidth = 800;
    private DesktopFileVirtualizingPanel? _virtualizingPanel;
    private int _hoverIndex = -1;

    private readonly record struct TextCacheKey(
        string Text,
        int Width2,
        int Height2,
        int Font10,
        bool Dark,
        TextAlignment Alignment,
        FontWeight Weight,
        IBrush Brush);

    static DesktopVirtualFileSurface()
    {
        AffectsMeasure<DesktopVirtualFileSurface>(ModeProperty);
        AffectsRender<DesktopVirtualFileSurface>(ModeProperty, ItemBackgroundProperty, SelectionBrushProperty);
    }

    public DesktopVirtualFileSurface()
    {
        Focusable = true;
        ItemsPanel = new FuncTemplate<Panel?>(() =>
        {
            _virtualizingPanel = new DesktopFileVirtualizingPanel(this);
            return _virtualizingPanel;
        });
        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as MainViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        PointerMoved += Surface_PointerMoved;
        PointerExited += (_, _) => SetHoverIndex(-1);
        ActualThemeVariantChanged += (_, _) =>
        {
            ClearTextCache();
            InvalidateRealizedItems();
        };
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public FileViewMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public IBrush? ItemBackground
    {
        get => GetValue(ItemBackgroundProperty);
        set => SetValue(ItemBackgroundProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public double MouseWheelLineHeight => Mode switch
    {
        FileViewMode.LargeIcons => LargeHeight + GridSpacing,
        FileViewMode.ExtraLargeIcons => ExtraHeight + GridSpacing,
        _ => DetailsRowHeight
    };

    public void ClearHoverForScroll() => SetHoverIndex(-1);

    public void RefreshLayout()
    {
        InvalidateMeasure();
        _virtualizingPanel?.RefreshViewport(force: true);
    }

    public void SetViewport(double offsetY, double viewportHeight, double viewportWidth)
    {
        var width = Math.Max(1, viewportWidth);
        var widthChanged = Math.Abs(width - _viewportWidth) > 0.5;
        var heightChanged = Math.Abs(Math.Max(1, viewportHeight) - _viewportHeight) > 0.5;
        _viewportOffsetY = Math.Max(0, offsetY);
        _viewportHeight = Math.Max(1, viewportHeight);
        _viewportWidth = width;
        if (widthChanged)
            ClearTextCache();
        _virtualizingPanel?.RefreshViewport(force: widthChanged || heightChanged);
    }

    public (int First, int Last) GetVisibleRange() =>
        CalculateVisibleRange(_viewportOffsetY, _viewportHeight, Math.Max(1, _viewportWidth));

    public double GetItemTop(int index)
    {
        if (index <= 0)
            return 0;
        if (Mode == FileViewMode.Details)
            return index * DetailsRowHeight;

        var metrics = GetGridMetrics(LayoutWidth);
        return (index / Math.Max(1, metrics.Columns)) * (metrics.Height + GridSpacing);
    }

    public DriveItemModel? GetItemAt(Point point)
    {
        var index = GetIndexAt(point);
        if (index < 0 || index >= ItemsView.Count)
            return null;
        return (ItemsView[index] as VirtualDriveItemSlot)?.Item;
    }

    public IReadOnlyList<DriveItemModel> GetItemsIntersecting(Rect rect)
    {
        if (ItemsView.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return Array.Empty<DriveItemModel>();

        var result = new List<DriveItemModel>();
        var width = LayoutWidth;
        if (Mode == FileViewMode.Details)
        {
            var first = Math.Clamp((int)Math.Floor(rect.Top / DetailsRowHeight), 0, ItemsView.Count - 1);
            var last = Math.Clamp((int)Math.Floor(Math.Max(rect.Top, rect.Bottom - 0.01) / DetailsRowHeight), 0, ItemsView.Count - 1);
            for (var i = first; i <= last; i++)
            {
                var itemRect = new Rect(0, i * DetailsRowHeight, width, DetailsRowHeight);
                if (Intersects(rect, itemRect) && ItemsView[i] is VirtualDriveItemSlot { Item: { } item })
                    result.Add(item);
            }
            return result;
        }

        var metrics = GetGridMetrics(width);
        var rowPitch = metrics.Height + GridSpacing;
        var firstRow = Math.Max(0, (int)Math.Floor(rect.Top / rowPitch));
        var lastRow = Math.Max(firstRow, (int)Math.Floor(Math.Max(rect.Top, rect.Bottom - 0.01) / rowPitch));
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var col = 0; col < metrics.Columns; col++)
            {
                var index = row * metrics.Columns + col;
                if (index >= ItemsView.Count)
                    return result;
                var itemRect = GetGridItemRect(index, metrics);
                if (Intersects(rect, itemRect) && ItemsView[index] is VirtualDriveItemSlot { Item: { } item })
                    result.Add(item);
            }
        }
        return result;
    }

    internal void RenderItem(DrawingContext context, VirtualDriveItemSlot slot, int index, Size size)
    {
        // Geometry is local to each recycled item; a hover or completed thumbnail redraws one item.
        if (Mode == FileViewMode.Details)
            DrawDetailsSlot(context, slot, index, size.Width);
        else
            DrawGridSlot(context, slot, index, new Rect(size));
    }

    private void AttachViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
            return;
        _vm = vm;
        _hoverIndex = -1;
        ClearTextCache();
        RefreshLayout();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            SetHoverIndex(-1);
            ClearTextCache();
            RefreshLayout();
        }
        if (change.Property == ModeProperty || change.Property == ItemBackgroundProperty ||
            change.Property == SelectionBrushProperty)
            InvalidateRealizedItems();
    }

    private void InvalidateRealizedItems() => _virtualizingPanel?.InvalidateRealizedItems();

    private void SetHoverIndex(int index)
    {
        if (_hoverIndex == index)
            return;
        var old = _hoverIndex;
        _hoverIndex = index;
        _virtualizingPanel?.InvalidateItem(old);
        _virtualizingPanel?.InvalidateItem(index);
    }

    private void Surface_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm?.IsDesktopListScrolling != true)
            SetHoverIndex(GetIndexAt(e.GetPosition(this)));
    }

    public int GetIndexAt(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > LayoutWidth)
            return -1;

        if (Mode == FileViewMode.Details)
        {
            var detailIndex = (int)Math.Floor(point.Y / DetailsRowHeight);
            return detailIndex >= 0 && detailIndex < ItemsView.Count ? detailIndex : -1;
        }

        var metrics = GetGridMetrics(LayoutWidth);
        var pitchX = metrics.Width + GridSpacing;
        var pitchY = metrics.Height + GridSpacing;
        var col = (int)Math.Floor(point.X / pitchX);
        var row = (int)Math.Floor(point.Y / pitchY);
        if (col < 0 || col >= metrics.Columns || row < 0)
            return -1;

        var localX = point.X - col * pitchX;
        var localY = point.Y - row * pitchY;
        if (localX > metrics.Width || localY > metrics.Height)
            return -1;

        var index = row * metrics.Columns + col;
        return index >= 0 && index < ItemsView.Count ? index : -1;
    }

    private (int First, int Last) CalculateVisibleRange(double offset, double viewportHeight, double width)
    {
        var count = ItemsView.Count;
        if (count <= 0)
            return (-1, -1);

        offset = Math.Max(0, offset);
        viewportHeight = Math.Max(1, viewportHeight);
        if (Mode == FileViewMode.Details)
        {
            var first = Math.Clamp((int)Math.Floor(offset / DetailsRowHeight), 0, count - 1);
            var last = Math.Clamp((int)Math.Floor(Math.Max(offset, offset + viewportHeight - 0.01) / DetailsRowHeight), first, count - 1);
            return (first, last);
        }

        var metrics = GetGridMetrics(width);
        var pitch = metrics.Height + GridSpacing;
        var firstRow = Math.Max(0, (int)Math.Floor(offset / pitch));
        var lastRow = Math.Max(firstRow, (int)Math.Floor(Math.Max(offset, offset + viewportHeight - 0.01) / pitch));
        var firstIndex = Math.Min(count - 1, firstRow * metrics.Columns);
        var lastIndex = Math.Min(count - 1, ((lastRow + 1) * metrics.Columns) - 1);
        return (firstIndex, lastIndex);
    }

    private double CalculateExtentHeight(int count, double width)
    {
        if (count <= 0)
            return 0;
        if (Mode == FileViewMode.Details)
            return count * DetailsRowHeight;

        var metrics = GetGridMetrics(width);
        var rows = (int)Math.Ceiling(count / (double)metrics.Columns);
        return rows * metrics.Height + Math.Max(0, rows - 1) * GridSpacing;
    }

    private GridMetrics GetGridMetrics(double width)
    {
        var extra = Mode == FileViewMode.ExtraLargeIcons;
        var preferred = extra ? ExtraPreferredWidth : LargePreferredWidth;
        var minWidth = extra ? ExtraMinWidth : LargeMinWidth;
        var maxWidth = extra ? ExtraMaxWidth : LargeMaxWidth;
        var height = extra ? ExtraHeight : LargeHeight;
        var usable = Math.Max(minWidth, width);
        var columns = Math.Max(1, (int)Math.Floor((usable + GridSpacing) / (preferred + GridSpacing)));
        var cellWidth = Math.Min(Math.Max(1, width), Math.Clamp((usable - GridSpacing * (columns - 1)) / columns, minWidth, maxWidth));
        return new GridMetrics(columns, cellWidth, height);
    }

    private Rect GetGridItemRect(int index, GridMetrics metrics)
    {
        var row = index / metrics.Columns;
        var col = index % metrics.Columns;
        return new Rect(
            col * (metrics.Width + GridSpacing),
            row * (metrics.Height + GridSpacing),
            metrics.Width,
            metrics.Height);
    }

    private double LayoutWidth => Math.Max(1, Bounds.Width > 1 ? Bounds.Width : _viewportWidth);
    public int Columns => Mode == FileViewMode.Details ? 1 : GetGridMetrics(LayoutWidth).Columns;

    public Rect GetItemBounds(int index) => Mode == FileViewMode.Details
        ? new Rect(0, Math.Max(0, index) * DetailsRowHeight, LayoutWidth, DetailsRowHeight)
        : GetGridItemRect(Math.Max(0, index), GetGridMetrics(LayoutWidth));

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;
    private IBrush ItemForeground => IsDark ? DarkText : LightText;
    private IBrush MutedForeground => IsDark ? DarkMutedText : LightMutedText;

    private readonly record struct GridMetrics(int Columns, double Width, double Height);

    private void DrawDetailsSlot(DrawingContext context, VirtualDriveItemSlot slot, int index, double width)
    {
        var rect = new Rect(0, 0, width, DetailsRowHeight);
        DrawSurface(context, rect, slot, index, 5);
        if (slot.Item is not { } item)
        {
            DrawPlaceholder(context, rect, 5, details: true);
            return;
        }

        const double gap = 6;
        const double iconColumn = 42;
        const double typeWidth = 150;
        const double sizeWidth = 130;
        const double modifiedWidth = 190;
        DrawArtwork(context, item, new Rect(rect.X + 5, rect.Y + 7, 32, 32), 5);

        var fixedRight = typeWidth + sizeWidth + modifiedWidth + gap * 3;
        var nameWidth = Math.Max(24, width - iconColumn - gap - fixedRight);
        var nameX = iconColumn + gap;
        var typeX = nameX + nameWidth + gap;
        var sizeX = typeX + typeWidth + gap;
        var modifiedX = sizeX + sizeWidth + gap;

        DrawSingleLine(context, item.Name, new Rect(nameX, rect.Y, nameWidth, rect.Height), 13, ItemForeground);
        DrawSingleLine(context, item.TypeDisplay, new Rect(typeX, rect.Y, typeWidth, rect.Height), 12.5, MutedForeground);
        DrawSingleLine(context, item.SizeDisplay, new Rect(sizeX, rect.Y, sizeWidth, rect.Height), 12.5, MutedForeground);
        DrawSingleLine(context, item.ModifiedDisplay, new Rect(modifiedX, rect.Y, Math.Max(0, width - modifiedX - 4), rect.Height), 12.5, MutedForeground);
    }

    private void DrawGridSlot(DrawingContext context, VirtualDriveItemSlot slot, int index, Rect rect)
    {
        var extra = Mode == FileViewMode.ExtraLargeIcons;
        var radius = extra ? 12d : 10d;
        DrawSurface(context, rect, slot, index, radius);
        if (slot.Item is not { } item)
        {
            DrawPlaceholder(context, rect, radius, details: false);
            return;
        }

        var padding = extra ? 12d : 10d;
        var captionHeight = extra ? 23d : 21d;
        var sizeHeight = extra ? 20d : 18d;
        var artworkBottom = rect.Bottom - padding - captionHeight - sizeHeight - (extra ? 12d : 9d);
        var artworkSize = Math.Max(32, Math.Min(
            extra ? 132d : 94d,
            Math.Min(rect.Width - padding * 2, artworkBottom - rect.Top - padding)));
        var artworkX = rect.X + (rect.Width - artworkSize) / 2;
        var artworkY = Math.Max(rect.Y + padding, rect.Y + (artworkBottom - rect.Y - artworkSize + padding) / 2);
        DrawArtwork(context, item, new Rect(artworkX, artworkY, artworkSize, artworkSize), extra ? 11 : 9);

        var nameY = rect.Bottom - padding - sizeHeight - captionHeight - 2;
        DrawSingleLine(context, item.Name,
            new Rect(rect.X + padding, nameY, Math.Max(1, rect.Width - padding * 2), captionHeight),
            extra ? 13.5 : 13, ItemForeground, TextAlignment.Center, FontWeight.Medium);
        DrawSingleLine(context, item.IconMetadataDisplay,
            new Rect(rect.X + padding, rect.Bottom - padding - sizeHeight, Math.Max(1, rect.Width - padding * 2), sizeHeight),
            11.5, MutedForeground, TextAlignment.Center);
    }

    private void DrawSurface(DrawingContext context, Rect rect, VirtualDriveItemSlot slot, int index, double radius)
    {
        if (ItemBackground is not null)
            context.DrawRectangle(ItemBackground, null, rect, radius, radius);

        if (slot.Item?.IsMobileSelected == true)
            context.DrawRectangle(SelectionBrush ?? DefaultSelection, null, rect, radius, radius);
        else if (_hoverIndex == index && slot.Item is not null)
            context.DrawRectangle(IsDark ? DarkHover : LightHover, null, rect, radius, radius);
    }

    private static void DrawPlaceholder(DrawingContext context, Rect surface, double radius, bool details)
    {
        var inset = details ? new Thickness(7, 7, 8, 7) : new Thickness(7);
        var rect = new Rect(
            surface.X + inset.Left,
            surface.Y + inset.Top,
            Math.Max(0, surface.Width - inset.Left - inset.Right),
            Math.Max(0, surface.Height - inset.Top - inset.Bottom));
        if (rect.Width > 0 && rect.Height > 0)
            context.DrawRectangle(PlaceholderBrush, null, rect, Math.Max(4, radius - 2), Math.Max(4, radius - 2));
    }

    private void DrawArtwork(DrawingContext context, DriveItemModel item, Rect dest, double radius)
    {
        if (item.ThumbnailImage is { } bitmap)
        {
            context.DrawRectangle(ThumbnailBackground, null, dest, radius, radius);
            using (context.PushClip(new RoundedRect(dest, radius)))
            {
                var source = UniformToFillSource(bitmap, dest);
                context.DrawImage(bitmap, source, dest);
            }
            if (item.IsVideo)
                DrawVideoBadge(context, dest);
            return;
        }

        if (item.IsFolder)
        {
            DrawFolder(context, dest);
            return;
        }

        DrawFileBadge(context, item, dest, radius);
    }

    private static Rect UniformToFillSource(Bitmap bitmap, Rect destination)
    {
        var sourceWidth = Math.Max(1d, bitmap.Size.Width);
        var sourceHeight = Math.Max(1d, bitmap.Size.Height);
        var destinationAspect = destination.Width / Math.Max(1d, destination.Height);
        var sourceAspect = sourceWidth / sourceHeight;
        if (sourceAspect > destinationAspect)
        {
            var cropWidth = sourceHeight * destinationAspect;
            return new Rect((sourceWidth - cropWidth) / 2d, 0, cropWidth, sourceHeight);
        }
        var cropHeight = sourceWidth / destinationAspect;
        return new Rect(0, (sourceHeight - cropHeight) / 2d, sourceWidth, cropHeight);
    }

    private static void DrawFolder(DrawingContext context, Rect dest)
    {
        var x = dest.X;
        var y = dest.Y;
        var width = dest.Width;
        var height = dest.Height;
        var top = y + height * 0.20;
        var bodyHeight = height * 0.68;
        context.DrawRectangle(FolderBack, null,
            new Rect(x + width * 0.04, top, width * 0.92, bodyHeight),
            Math.Max(2, width * 0.07), Math.Max(2, width * 0.07));
        context.DrawRectangle(FolderBack, null,
            new Rect(x + width * 0.08, y + height * 0.10, width * 0.42, height * 0.18),
            Math.Max(2, width * 0.05), Math.Max(2, width * 0.05));
        context.DrawRectangle(FolderPaper, null,
            new Rect(x + width * 0.14, y + height * 0.36, width * 0.72, height * 0.30),
            Math.Max(1.5, width * 0.035), Math.Max(1.5, width * 0.035));
        context.DrawRectangle(FolderFront, null,
            new Rect(x + width * 0.04, y + height * 0.44, width * 0.92, height * 0.42),
            Math.Max(2, width * 0.07), Math.Max(2, width * 0.07));
    }

    private void DrawFileBadge(DrawingContext context, DriveItemModel item, Rect dest, double radius)
    {
        var accent = GetAccent(item);
        var body = IsDark ? DarkFileBody : LightFileBody;
        var insetX = dest.Width * 0.12;
        var insetY = dest.Height * 0.05;
        var bodyRect = new Rect(dest.X + insetX, dest.Y + insetY,
            Math.Max(8, dest.Width - insetX * 2), Math.Max(10, dest.Height - insetY * 2));
        var bodyRadius = Math.Max(3, radius * 0.75);
        context.DrawRectangle(body, null, bodyRect, bodyRadius, bodyRadius);
        var stripHeight = Math.Max(9, bodyRect.Height * 0.31);
        var strip = new Rect(bodyRect.X, bodyRect.Bottom - stripHeight, bodyRect.Width, stripHeight);
        context.DrawRectangle(accent, null, strip, bodyRadius, bodyRadius);
        var badge = item.IsImage ? "IMG" : item.IsVideo ? "VID" : item.IsAudio ? "MUS" : item.FileBadgeText;
        DrawSingleLine(context, badge, strip, Math.Clamp(stripHeight * 0.48, 6.5, 13), WhiteBrush,
            TextAlignment.Center, FontWeight.SemiBold);
    }

    private static IBrush GetAccent(DriveItemModel item)
    {
        if (item.IsImage) return ImageBrush;
        if (item.IsVideo) return VideoBrush;
        if (item.IsAudio) return AudioBrush;
        if (item.IsPdf) return PdfBrush;
        if (item.IsWord) return WordBrush;
        if (item.IsExcel) return ExcelBrush;
        if (item.IsPowerPoint) return PowerPointBrush;
        if (item.IsArchive) return ArchiveBrush;
        if (item.IsUrlShortcut) return UrlBrush;
        if (item.IsText) return TextBrush;
        return GenericBrush;
    }

    private void DrawVideoBadge(DrawingContext context, Rect dest)
    {
        var diameter = Math.Clamp(Math.Min(dest.Width, dest.Height) * 0.25, 14, 26);
        var badgeRect = new Rect(dest.Right - diameter - 2, dest.Bottom - diameter - 2, diameter, diameter);
        context.DrawEllipse(VideoBadgeBrush, null, badgeRect);
        DrawSingleLine(context, "▶", badgeRect, Math.Max(7, diameter * 0.42), WhiteBrush,
            TextAlignment.Center, FontWeight.SemiBold);
    }

    private void DrawSingleLine(
        DrawingContext context,
        string? text,
        Rect bounds,
        double fontSize,
        IBrush brush,
        TextAlignment alignment = TextAlignment.Left,
        FontWeight weight = FontWeight.Normal)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 1 || bounds.Height <= 1)
            return;

        var key = new TextCacheKey(
            text,
            (int)Math.Round(bounds.Width * 2),
            (int)Math.Round(bounds.Height * 2),
            (int)Math.Round(fontSize * 10),
            IsDark,
            alignment,
            weight,
            brush);
        if (!_textCache.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, weight, FontStretch.Normal),
                fontSize,
                brush)
            {
                MaxTextWidth = Math.Max(1, bounds.Width),
                MaxTextHeight = Math.Max(1, bounds.Height),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = alignment
            };
            _textCache[key] = formatted;
            _textCacheOrder.Enqueue(key);
            while (_textCacheOrder.Count > 2048)
            {
                var old = _textCacheOrder.Dequeue();
                _textCache.Remove(old);
            }
        }

        var y = bounds.Y + Math.Max(0, (bounds.Height - formatted.Height) / 2d);
        context.DrawText(formatted, new Point(bounds.X, y));
    }

    private void ClearTextCache()
    {
        _textCache.Clear();
        _textCacheOrder.Clear();
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}

