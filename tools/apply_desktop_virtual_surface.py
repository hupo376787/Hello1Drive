from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return path.read_text(encoding='utf-8')


def write(path, text):
    path.write_text(text, encoding='utf-8')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected one match, got {count}')
    return text.replace(old, new, 1)


def method_span(text, signature):
    start = text.find(signature)
    if start < 0:
        raise RuntimeError(f'method not found: {signature}')
    brace = text.find('{', start)
    if brace < 0:
        raise RuntimeError(f'opening brace not found: {signature}')
    depth = 0
    i = brace
    while i < len(text):
        ch = text[i]
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                return start, i + 1
        i += 1
    raise RuntimeError(f'unclosed method: {signature}')


def replace_method(text, signature, replacement):
    start, end = method_span(text, signature)
    return text[:start] + replacement + text[end:]


def remove_method_if_present(text, signature):
    if signature not in text:
        return text
    start, end = method_span(text, signature)
    while end < len(text) and text[end] in '\r\n':
        end += 1
    return text[:start] + text[end:]


def remove_data_template(text, key):
    marker = f'<DataTemplate x:Key="{key}"'
    start = text.find(marker)
    if start < 0:
        return text
    line_start = text.rfind('\n', 0, start) + 1
    end_marker = '</DataTemplate>'
    end = text.find(end_marker, start)
    if end < 0:
        raise RuntimeError(f'end template not found: {key}')
    end += len(end_marker)
    while end < len(text) and text[end] in '\r\n':
        end += 1
    return text[:line_start] + text[end:]


def remove_named_scrollviewer(text, name):
    marker = f'<ScrollViewer x:Name="{name}"'
    start = text.find(marker)
    if start < 0:
        raise RuntimeError(f'scrollviewer not found: {name}')
    line_start = text.rfind('\n', 0, start) + 1
    end = text.find('</ScrollViewer>', start)
    if end < 0:
        raise RuntimeError(f'end scrollviewer not found: {name}')
    end += len('</ScrollViewer>')
    while end < len(text) and text[end] in '\r\n':
        end += 1
    return text[:line_start] + text[end:]


surface_path = ROOT / 'src/Hello1Drive.Core/Controls/DesktopVirtualFileSurface.cs'
surface_path.write_text(r'''using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Hello1Drive.Models;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Controls;

/// <summary>
/// One retained-mode desktop surface for an entire OneDrive folder. It owns no per-file controls:
/// the ScrollViewer only moves this single visual, while Render draws the current viewport plus one
/// viewport of look-behind/look-ahead. Small wheel movements therefore reuse the already-generated
/// scene instead of asking an ItemsRepeater to realize/recycle/layout another batch of controls.
/// </summary>
public sealed class DesktopVirtualFileSurface : Control
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

    private readonly HashSet<VirtualDriveItemSlot> _subscribedSlots = [];
    private readonly Dictionary<TextCacheKey, FormattedText> _textCache = [];
    private readonly Queue<TextCacheKey> _textCacheOrder = [];
    private MainViewModel? _vm;
    private double _viewportOffsetY;
    private double _viewportHeight = 800;
    private double _viewportWidth = 800;
    private int _renderFrom = -1;
    private int _renderTo = -1;
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
        ClipToBounds = false;
        Focusable = true;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as MainViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        PointerMoved += Surface_PointerMoved;
        PointerExited += (_, _) =>
        {
            if (_hoverIndex < 0)
                return;
            _hoverIndex = -1;
            InvalidateVisual();
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            ClearTextCache();
            InvalidateVisual();
        };
    }

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

    public void SetViewport(double offsetY, double viewportHeight, double viewportWidth)
    {
        var width = Math.Max(1, viewportWidth);
        var widthChanged = Math.Abs(width - _viewportWidth) > 0.5;
        _viewportOffsetY = Math.Max(0, offsetY);
        _viewportHeight = Math.Max(1, viewportHeight);
        _viewportWidth = width;

        if (widthChanged)
        {
            _renderFrom = -1;
            _renderTo = -1;
            ClearTextCache();
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        var (first, last) = CalculateVisibleRange(_viewportOffsetY, _viewportHeight, width);
        if (first < 0)
            return;

        // The retained scene already contains one viewport before/after the visible viewport.
        // As long as the user remains inside that window, scrolling is a pure compositor move.
        if (_renderFrom < 0 || first < _renderFrom || last > _renderTo)
            InvalidateVisual();
    }

    public (int First, int Last) GetVisibleRange() =>
        CalculateVisibleRange(_viewportOffsetY, _viewportHeight, Math.Max(1, _viewportWidth));

    public DriveItemModel? GetItemAt(Point point)
    {
        var index = GetIndexAt(point);
        if (_vm is null || index < 0 || index >= _vm.VirtualItems.Count)
            return null;
        return _vm.VirtualItems[index].Item;
    }

    public IReadOnlyList<DriveItemModel> GetItemsIntersecting(Rect rect)
    {
        if (_vm is null || _vm.VirtualItems.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return Array.Empty<DriveItemModel>();

        var result = new List<DriveItemModel>();
        var width = LayoutWidth;
        if (Mode == FileViewMode.Details)
        {
            var first = Math.Clamp((int)Math.Floor(rect.Top / DetailsRowHeight), 0, _vm.VirtualItems.Count - 1);
            var last = Math.Clamp((int)Math.Floor(Math.Max(rect.Top, rect.Bottom - 0.01) / DetailsRowHeight), 0, _vm.VirtualItems.Count - 1);
            for (var i = first; i <= last; i++)
            {
                var itemRect = new Rect(0, i * DetailsRowHeight, width, DetailsRowHeight);
                if (Intersects(rect, itemRect) && _vm.VirtualItems[i].Item is { } item)
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
                if (index >= _vm.VirtualItems.Count)
                    return result;
                var itemRect = GetGridItemRect(index, metrics);
                if (Intersects(rect, itemRect) && _vm.VirtualItems[index].Item is { } item)
                    result.Add(item);
            }
        }
        return result;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 1
            ? availableSize.Width
            : Math.Max(1, _viewportWidth);
        var count = _vm?.VirtualItems.Count ?? 0;
        return new Size(width, CalculateExtentHeight(count, width));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var vm = _vm;
        if (vm is null || vm.VirtualItems.Count == 0 || Bounds.Width <= 1 || Bounds.Height <= 0)
        {
            _renderFrom = -1;
            _renderTo = -1;
            return;
        }

        var width = LayoutWidth;
        var (visibleFirst, visibleLast) = CalculateVisibleRange(_viewportOffsetY, _viewportHeight, width);
        if (visibleFirst < 0)
            return;

        var visibleCount = Math.Max(1, visibleLast - visibleFirst + 1);
        var from = Math.Max(0, visibleFirst - visibleCount);
        var to = Math.Min(vm.VirtualItems.Count - 1, visibleLast + visibleCount);

        // Grid ranges should begin/end on complete rows so a sideways edge never appears halfway
        // through the retained scene when the next wheel frame is compositor-only.
        if (Mode != FileViewMode.Details)
        {
            var metrics = GetGridMetrics(width);
            from = Math.Max(0, (from / metrics.Columns) * metrics.Columns);
            to = Math.Min(vm.VirtualItems.Count - 1,
                (((to / metrics.Columns) + 1) * metrics.Columns) - 1);
        }

        _renderFrom = from;
        _renderTo = to;

        if (Mode == FileViewMode.Details)
        {
            for (var index = from; index <= to; index++)
                DrawDetailsSlot(context, vm.VirtualItems[index], index, width);
            return;
        }

        var grid = GetGridMetrics(width);
        for (var index = from; index <= to; index++)
            DrawGridSlot(context, vm.VirtualItems[index], index, grid);
    }

    private void AttachViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
            return;

        if (_vm is not null)
            _vm.VirtualItems.CollectionChanged -= VirtualItems_CollectionChanged;
        DetachAllSlots();

        _vm = vm;
        if (_vm is not null)
        {
            _vm.VirtualItems.CollectionChanged += VirtualItems_CollectionChanged;
            foreach (var slot in _vm.VirtualItems)
                AttachSlot(slot);
        }

        _renderFrom = -1;
        _renderTo = -1;
        ClearTextCache();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void VirtualItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var value in e.OldItems)
                if (value is VirtualDriveItemSlot slot)
                    DetachSlot(slot);

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            DetachAllSlots();
            if (_vm is not null)
                foreach (var slot in _vm.VirtualItems)
                    AttachSlot(slot);
        }
        else if (e.NewItems is not null)
        {
            foreach (var value in e.NewItems)
                if (value is VirtualDriveItemSlot slot)
                    AttachSlot(slot);
        }

        _renderFrom = -1;
        _renderTo = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void AttachSlot(VirtualDriveItemSlot slot)
    {
        if (_subscribedSlots.Add(slot))
            slot.PropertyChanged += Slot_PropertyChanged;
    }

    private void DetachSlot(VirtualDriveItemSlot slot)
    {
        if (_subscribedSlots.Remove(slot))
            slot.PropertyChanged -= Slot_PropertyChanged;
    }

    private void DetachAllSlots()
    {
        foreach (var slot in _subscribedSlots)
            slot.PropertyChanged -= Slot_PropertyChanged;
        _subscribedSlots.Clear();
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VirtualDriveItemSlot slot)
            return;

        // Desktop slot hydration uses one Item notification. Thumbnail/selection changes are
        // forwarded by the slot later. Redraw only when the affected slot exists in the retained
        // three-viewport scene; off-screen changes are picked up naturally on the next scene build.
        if (slot.Index >= _renderFrom && slot.Index <= _renderTo &&
            (e.PropertyName is nameof(VirtualDriveItemSlot.Item)
                or nameof(VirtualDriveItemSlot.ThumbnailImage)
                or nameof(VirtualDriveItemSlot.IsMobileSelected)
                or nameof(VirtualDriveItemSlot.IsPlaceholder)
                or nameof(VirtualDriveItemSlot.Name)
                or nameof(VirtualDriveItemSlot.SizeDisplay)))
        {
            InvalidateVisual();
        }
    }

    private void Surface_PointerMoved(object? sender, PointerEventArgs e)
    {
        var index = GetIndexAt(e.GetPosition(this));
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        InvalidateVisual();
    }

    private int GetIndexAt(Point point)
    {
        var vm = _vm;
        if (vm is null || point.X < 0 || point.Y < 0 || point.X > LayoutWidth)
            return -1;

        if (Mode == FileViewMode.Details)
        {
            var index = (int)Math.Floor(point.Y / DetailsRowHeight);
            return index >= 0 && index < vm.VirtualItems.Count ? index : -1;
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
        return index >= 0 && index < vm.VirtualItems.Count ? index : -1;
    }

    private (int First, int Last) CalculateVisibleRange(double offset, double viewportHeight, double width)
    {
        var count = _vm?.VirtualItems.Count ?? 0;
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
        var usable = Math.Max(minWidth, width - 18);
        var columns = Math.Max(1, (int)Math.Floor((usable + GridSpacing) / (preferred + GridSpacing)));
        var cellWidth = Math.Clamp((usable - GridSpacing * (columns - 1)) / columns, minWidth, maxWidth);
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
    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;
    private IBrush Foreground => IsDark ? DarkText : LightText;
    private IBrush MutedForeground => IsDark ? DarkMutedText : LightMutedText;

    private readonly record struct GridMetrics(int Columns, double Width, double Height);

    private void DrawDetailsSlot(DrawingContext context, VirtualDriveItemSlot slot, int index, double width)
    {
        var rect = new Rect(0, index * DetailsRowHeight, width, DetailsRowHeight);
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

        DrawSingleLine(context, item.Name, new Rect(nameX, rect.Y, nameWidth, rect.Height), 13, Foreground);
        DrawSingleLine(context, item.TypeDisplay, new Rect(typeX, rect.Y, typeWidth, rect.Height), 12.5, MutedForeground);
        DrawSingleLine(context, item.SizeDisplay, new Rect(sizeX, rect.Y, sizeWidth, rect.Height), 12.5, MutedForeground);
        DrawSingleLine(context, item.ModifiedDisplay, new Rect(modifiedX, rect.Y, Math.Max(0, width - modifiedX - 4), rect.Height), 12.5, MutedForeground);
    }

    private void DrawGridSlot(DrawingContext context, VirtualDriveItemSlot slot, int index, GridMetrics metrics)
    {
        var rect = GetGridItemRect(index, metrics);
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
            extra ? 13.5 : 13, Foreground, TextAlignment.Center, FontWeight.Medium);
        DrawSingleLine(context, item.SizeDisplay,
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
            while (_textCacheOrder.Count > 6000)
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
''', encoding='utf-8')

# Remove the previous per-realized-item desktop control after the XAML no longer references it.
old_control = ROOT / 'src/Hello1Drive.Core/Controls/DesktopFileItemControl.cs'
if old_control.exists():
    old_control.unlink()

# ---------------- XAML: one desktop ScrollViewer + one virtual drawing surface ----------------
xaml_path = ROOT / 'src/Hello1Drive.Core/Views/MainView.axaml'
xaml = read(xaml_path)
for key in ('DesktopDetailsSlotTemplate', 'DesktopLargeSlotTemplate', 'DesktopExtraLargeSlotTemplate'):
    xaml = remove_data_template(xaml, key)
for name in ('DesktopDetailsScrollViewer', 'DesktopLargeIconScrollViewer', 'DesktopExtraLargeIconScrollViewer'):
    xaml = remove_named_scrollviewer(xaml, name)

# Reuse the existing details header markup for the desktop-only common container. The original
# remains inside the mobile-only wrapper below, so mobile layout/behavior stays untouched.
header_start = xaml.find('          <Border x:Name="DetailsHeaderBorder"')
header_end = xaml.find('          <RefreshContainer Grid.Row="1"', header_start)
if header_start < 0 or header_end < 0:
    raise RuntimeError('details header block not found')
header = xaml[header_start:header_end].rstrip().replace(
    'x:Name="DetailsHeaderBorder"', 'x:Name="DesktopDetailsHeaderBorder"', 1)

desktop_container = f'''        <!-- Desktop hot path: exactly one ScrollViewer and one retained virtual drawing surface. -->
        <Grid IsVisible="{{OnFormFactor Desktop=True, Mobile=False}}" RowDefinitions="Auto,*">
{header}
          <ScrollViewer x:Name="DesktopVirtualScrollViewer" Grid.Row="1" Margin="0,0,4,0"
                        HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto"
                        HorizontalContentAlignment="Stretch"
                        ScrollChanged="FileListScrollViewer_ScrollChanged"
                        SizeChanged="DesktopVirtualScrollViewer_SizeChanged">
            <controls:DesktopVirtualFileSurface x:Name="DesktopFileSurface"
                                                Mode="{{Binding ViewMode}}"
                                                HorizontalAlignment="Stretch"
                                                ItemBackground="{{DynamicResource HelloFileItemBrush}}"
                                                SelectionBrush="{{DynamicResource HelloExplorerSelectionBrush}}"
                                                PointerPressed="DesktopFileSurface_PointerPressed"
                                                DoubleTapped="DesktopFileSurface_DoubleTapped"
                                                ContextRequested="DesktopFileSurface_ContextRequested" />
          </ScrollViewer>
        </Grid>

        <!-- The Avalonia mobile fallback keeps its existing three mode-specific surfaces. -->
        <Grid IsVisible="{{OnFormFactor Desktop=False, Mobile=True}}">'''

xaml = replace_once(xaml, '        <!-- Details -->', desktop_container + '\n        <!-- Details -->', 'insert desktop surface')
xaml = replace_once(
    xaml,
    '        <!-- Native mobile performance path: Android hosts RecyclerView and iOS hosts UICollectionView',
    '        </Grid>\n\n        <!-- Native mobile performance path: Android hosts RecyclerView and iOS hosts UICollectionView',
    'close mobile-only wrapper')
write(xaml_path, xaml)

# ---------------- MainView code-behind: direct arithmetic instead of realization traversal -------
view_path = ROOT / 'src/Hello1Drive.Core/Views/MainView.axaml.cs'
view = read(view_path)

view = replace_method(view, '    private ScrollViewer? GetActiveScrollViewer(MainViewModel vm)', '''    private ScrollViewer? GetActiveScrollViewer(MainViewModel vm)
    {
        if (UsesNativeMobileFileList)
            return null;

        if (!IsMobilePlatform)
            return DesktopVirtualScrollViewer;

        return vm.ViewMode switch
        {
            FileViewMode.LargeIcons => MobileLargeIconScrollViewer,
            FileViewMode.ExtraLargeIcons => MobileExtraLargeIconScrollViewer,
            _ => MobileDetailsScrollViewer
        };
    }''')

view = remove_method_if_present(view, '    private ItemsRepeater GetActiveDesktopRepeater(MainViewModel vm)')
view = remove_method_if_present(view, '    private static void ConfigureResponsiveDesktopGrid(')

view = replace_method(view, '    private void UpdateIconPanelSizing()', '''    private void UpdateIconPanelSizing()
    {
        if (IsMobilePlatform)
            return;

        DesktopFileSurface.InvalidateMeasure();
        SyncDesktopVirtualSurfaceViewport(DesktopVirtualScrollViewer);
    }''')

view = replace_method(view, '    private void HandleDesktopFileScroll(ScrollViewer scroll, MainViewModel vm)', '''    private void HandleDesktopFileScroll(ScrollViewer scroll, MainViewModel vm)
    {
        // This is the complete desktop scroll hot path. SetViewport normally does not invalidate
        // the surface while the visible viewport remains inside its retained +/-1 viewport scene.
        SyncDesktopVirtualSurfaceViewport(scroll);
        _desktopScrollLastActivityUtc = DateTime.UtcNow;
        unchecked { _desktopThumbnailIdleRecoveryVersion++; }
        vm.SetDesktopListScrolling(true);

        if (!_desktopScrollIdleTimer.IsEnabled)
            _desktopScrollIdleTimer.Start();
    }''')

view = replace_method(view, '    private void QueueRealizedDesktopThumbnails(', '''    private void QueueRealizedDesktopThumbnails(
        ScrollViewer scroll,
        MainViewModel vm,
        bool allowNetwork)
    {
        if (IsMobilePlatform || !scroll.IsVisible || !ReferenceEquals(scroll, DesktopVirtualScrollViewer))
            return;

        SyncDesktopVirtualSurfaceViewport(scroll);
        var (visibleFirst, visibleLast) = DesktopFileSurface.GetVisibleRange();
        if (visibleFirst < 0 || visibleLast < visibleFirst || vm.VirtualItems.Count == 0)
            return;

        var visibleCount = Math.Max(1, visibleLast - visibleFirst + 1);
        var windowFrom = Math.Max(0, visibleFirst - visibleCount);
        var windowTo = Math.Min(vm.VirtualItems.Count - 1, visibleLast + visibleCount);
        var indices = new List<int>(windowTo - windowFrom + 1);
        var items = new List<DriveItemModel>(windowTo - windowFrom + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddRange(int from, int to)
        {
            if (from > to)
                return;
            for (var index = from; index <= to; index++)
            {
                indices.Add(index);
                if (vm.VirtualItems[index].Item is { } item &&
                    !string.IsNullOrWhiteSpace(item.Id) && seen.Add(item.Id))
                {
                    items.Add(item);
                }
            }
        }

        // Current viewport first, then previous viewport, then next viewport. The VM preserves this
        // ordering when it enters the two-worker thumbnail gate, so what the user sees always wins.
        AddRange(visibleFirst, visibleLast);
        AddRange(windowFrom, visibleFirst - 1);
        AddRange(visibleLast + 1, windowTo);
        vm.UpdateDesktopRealizedThumbnails(indices, items, allowNetwork);
    }''')

# Add desktop surface helpers and input bridge immediately before the existing ScrollChanged handler.
insert_marker = '    private void FileListScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)'
if insert_marker not in view:
    raise RuntimeError('scroll handler marker not found')
helpers = r'''    private void SyncDesktopVirtualSurfaceViewport(ScrollViewer scroll)
    {
        if (IsMobilePlatform || !ReferenceEquals(scroll, DesktopVirtualScrollViewer))
            return;

        var viewportWidth = scroll.Viewport.Width > 1 ? scroll.Viewport.Width : scroll.Bounds.Width;
        var viewportHeight = scroll.Viewport.Height > 1 ? scroll.Viewport.Height : scroll.Bounds.Height;
        DesktopFileSurface.SetViewport(scroll.Offset.Y, viewportHeight, viewportWidth);
    }

    private void DesktopVirtualScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsMobilePlatform || sender is not ScrollViewer scroll)
            return;

        SyncDesktopVirtualSurfaceViewport(scroll);
        if (DataContext is MainViewModel vm && !vm.IsDesktopListScrolling)
            Dispatcher.UIThread.Post(() => QueueRealizedDesktopThumbnails(scroll, vm, allowNetwork: true), DispatcherPriority.Background);
    }

    private void DesktopFileSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMobilePlatform || sender is not DesktopVirtualFileSurface surface || DataContext is not MainViewModel vm)
            return;

        var item = surface.GetItemAt(e.GetPosition(surface));
        if (item is null)
            return;

        _contextItem = item;
        var point = e.GetCurrentPoint(surface);
        if (point.Properties.IsLeftButtonPressed)
            ApplyDesktopPointerSelection(vm, item, e.KeyModifiers);
        else if (point.Properties.IsRightButtonPressed)
            SelectContextItem(item);
    }

    private async void DesktopFileSurface_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsMobilePlatform || sender is not DesktopVirtualFileSurface surface || DataContext is not MainViewModel vm)
            return;

        var item = surface.GetItemAt(e.GetPosition(surface));
        if (item is null)
            return;

        _contextItem = item;
        _desktopSelectionAnchorId = item.Id;
        _desktopSelectedIds.Clear();
        _desktopSelectedIds.Add(item.Id);
        ApplyDesktopSelection(vm);
        e.Handled = true;
        await OpenDriveItemAsync(vm, item);
    }

    private void DesktopFileSurface_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IsMobilePlatform || sender is not DesktopVirtualFileSurface surface || _contextItem is not { } item)
            return;

        SelectContextItem(item);
        var menu = GetOrCreateDesktopFileItemContextMenu();
        if (_desktopOpenWebMenuItem is not null)
            _desktopOpenWebMenuItem.IsVisible = item.HasWebUrl;
        menu.Open(surface);
        e.Handled = true;
    }

'''
view = view.replace(insert_marker, helpers + insert_marker, 1)

# Do not begin marquee selection when the tunnel event is over a real item on the single surface.
needle = '''        // Do not steal pointer capture from real controls. In particular the ListBox scrollbar
        // thumb must remain draggable, and header buttons must receive Click for sorting.
        if (ShouldSuppressMarqueeStart(e.Source))
            return;'''
replacement = '''        // The desktop file area is one control, so DataContext alone can no longer tell whether
        // the pointer is over an item or an empty gap. Ask the virtual surface directly.
        if (e.Source is DesktopVirtualFileSurface desktopSurface &&
            desktopSurface.GetItemAt(e.GetPosition(desktopSurface)) is not null)
            return;

        // Do not steal pointer capture from real controls. In particular the scrollbar thumb and
        // header buttons must keep their own input gestures.
        if (ShouldSuppressMarqueeStart(e.Source))
            return;'''
view = replace_once(view, needle, replacement, 'marquee item suppression')

# Marquee hit testing is arithmetic on the same single surface; no visual-tree traversal remains.
old_marquee = '''        var selectionRect = new Rect(x, y, width, height);
        var selectedIds = new HashSet<string>(_marqueeBaseSelection, StringComparer.Ordinal);
        var repeater = GetActiveDesktopRepeater(vm);
        foreach (var container in repeater.GetVisualChildren().OfType<Control>())
        {
            if (GetDriveItemFromDataContext(container.DataContext) is not { } item)
                continue;
            var origin = container.TranslatePoint(new Point(0, 0), FileArea);
            if (origin is null)
                continue;

            var itemRect = new Rect(origin.Value.X, origin.Value.Y, container.Bounds.Width, container.Bounds.Height);
            var intersects = selectionRect.Left < itemRect.Right && selectionRect.Right > itemRect.Left &&
                             selectionRect.Top < itemRect.Bottom && selectionRect.Bottom > itemRect.Top;
            if (intersects)
                selectedIds.Add(item.Id);
        }'''
new_marquee = '''        var selectionRect = new Rect(x, y, width, height);
        var selectedIds = new HashSet<string>(_marqueeBaseSelection, StringComparer.Ordinal);
        var topLeft = FileArea.TranslatePoint(new Point(selectionRect.Left, selectionRect.Top), DesktopFileSurface);
        var bottomRight = FileArea.TranslatePoint(new Point(selectionRect.Right, selectionRect.Bottom), DesktopFileSurface);
        if (topLeft is { } a && bottomRight is { } b)
        {
            var surfaceRect = new Rect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(b.X - a.X),
                Math.Abs(b.Y - a.Y));
            foreach (var item in DesktopFileSurface.GetItemsIntersecting(surfaceRect))
                selectedIds.Add(item.Id);
        }'''
view = replace_once(view, old_marquee, new_marquee, 'marquee arithmetic hit test')

# Make comments accurately describe the new surface.
view = view.replace('destination ItemsRepeater to realize its first viewport', 'destination virtual surface to receive its first viewport')
view = view.replace('destination ItemsRepeater', 'destination virtual surface')

write(view_path, view)

# No desktop ItemsRepeater/old ScrollViewer references may remain after this migration.
remaining = []
for token in (
    'DesktopDetailsRepeater', 'DesktopLargeIconRepeater', 'DesktopExtraLargeIconRepeater',
    'DesktopDetailsScrollViewer', 'DesktopLargeIconScrollViewer', 'DesktopExtraLargeIconScrollViewer',
    'GetActiveDesktopRepeater'):
    for path in (xaml_path, view_path):
        if token in read(path):
            remaining.append(f'{path.name}:{token}')
if remaining:
    raise RuntimeError('stale desktop repeater references: ' + ', '.join(remaining))
