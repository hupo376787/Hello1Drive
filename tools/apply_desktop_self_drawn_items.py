from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


def replace_template(path: Path, key: str, replacement: str) -> None:
    text = path.read_text(encoding="utf-8")
    marker = f'<DataTemplate x:Key="{key}"'
    start = text.find(marker)
    if start < 0:
        raise RuntimeError(f"template {key}: start marker not found")
    # Include indentation before the template so the replacement stays tidy.
    line_start = text.rfind("\n", 0, start) + 1
    end_marker = "</DataTemplate>"
    end = text.find(end_marker, start)
    if end < 0:
        raise RuntimeError(f"template {key}: end marker not found")
    end += len(end_marker)
    path.write_text(text[:line_start] + replacement + text[end:], encoding="utf-8")


# ---------------------------------------------------------------------------
# 1) One lightweight cross-platform Avalonia visual for every desktop item.
# ---------------------------------------------------------------------------
control_path = ROOT / "src/Hello1Drive.Core/Controls/DesktopFileItemControl.cs"
control_path.write_text(r'''using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Hello1Drive.Models;

namespace Hello1Drive.Controls;

/// <summary>
/// Desktop file item rendered as one Avalonia visual. The old XAML templates created dozens of
/// Grid/Border/Viewbox/Path/TextBlock nodes for every realized item, including hidden file-type
/// artwork. Keeping the item as a single Control makes ItemsRepeater recycling and scrolling much
/// cheaper on Windows, Linux and macOS while preserving the shared Avalonia surface/overlays.
/// </summary>
public sealed class DesktopFileItemControl : Control
{
    public static readonly StyledProperty<FileViewMode> ModeProperty =
        AvaloniaProperty.Register<DesktopFileItemControl, FileViewMode>(nameof(Mode), FileViewMode.Details);

    public static readonly StyledProperty<IBrush?> ItemBackgroundProperty =
        AvaloniaProperty.Register<DesktopFileItemControl, IBrush?>(nameof(ItemBackground));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<DesktopFileItemControl, IBrush?>(nameof(SelectionBrush));

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

    private VirtualDriveItemSlot? _slot;
    private bool _hovered;

    static DesktopFileItemControl()
    {
        AffectsRender<DesktopFileItemControl>(ModeProperty, ItemBackgroundProperty, SelectionBrushProperty);
    }

    public DesktopFileItemControl()
    {
        ClipToBounds = true;
        Focusable = true;
        DataContextChanged += (_, _) => AttachSlot(DataContext as VirtualDriveItemSlot);
        AttachedToVisualTree += (_, _) => AttachSlot(DataContext as VirtualDriveItemSlot);
        DetachedFromVisualTree += (_, _) => AttachSlot(null);
        PointerEntered += (_, _) =>
        {
            _hovered = true;
            InvalidateVisual();
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            InvalidateVisual();
        };
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
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

    private void AttachSlot(VirtualDriveItemSlot? slot)
    {
        if (ReferenceEquals(_slot, slot))
            return;

        if (_slot is not null)
            _slot.PropertyChanged -= Slot_PropertyChanged;

        _slot = slot;

        if (_slot is not null)
            _slot.PropertyChanged += Slot_PropertyChanged;

        InvalidateVisual();
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1)
            return;

        var radius = Mode switch
        {
            FileViewMode.ExtraLargeIcons => 12d,
            FileViewMode.LargeIcons => 10d,
            _ => 5d
        };
        var surface = new Rect(0, 0, width, height);

        if (ItemBackground is not null)
            context.DrawRectangle(ItemBackground, null, surface, radius, radius);

        var item = _slot?.Item;
        if (item?.IsMobileSelected == true)
            context.DrawRectangle(SelectionBrush ?? DefaultSelection, null, surface, radius, radius);
        else if (_hovered && item is not null)
            context.DrawRectangle(IsDark ? DarkHover : LightHover, null, surface, radius, radius);

        if (_slot is null || _slot.IsPlaceholder || item is null)
        {
            DrawPlaceholder(context, surface, radius);
            return;
        }

        switch (Mode)
        {
            case FileViewMode.LargeIcons:
                DrawIconMode(context, item, surface, extraLarge: false);
                break;
            case FileViewMode.ExtraLargeIcons:
                DrawIconMode(context, item, surface, extraLarge: true);
                break;
            default:
                DrawDetails(context, item, surface);
                break;
        }
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;
    private IBrush Foreground => IsDark ? DarkText : LightText;
    private IBrush MutedForeground => IsDark ? DarkMutedText : LightMutedText;

    private void DrawPlaceholder(DrawingContext context, Rect surface, double radius)
    {
        var inset = Mode == FileViewMode.Details ? new Thickness(7, 7, 8, 7) : new Thickness(7);
        var rect = new Rect(
            inset.Left,
            inset.Top,
            Math.Max(0, surface.Width - inset.Left - inset.Right),
            Math.Max(0, surface.Height - inset.Top - inset.Bottom));
        if (rect.Width > 0 && rect.Height > 0)
            context.DrawRectangle(PlaceholderBrush, null, rect, Math.Max(4, radius - 2), Math.Max(4, radius - 2));
    }

    private void DrawDetails(DrawingContext context, DriveItemModel item, Rect surface)
    {
        const double gap = 6;
        const double iconColumn = 42;
        const double typeWidth = 150;
        const double sizeWidth = 130;
        const double modifiedWidth = 190;

        var iconRect = new Rect(5, 7, 32, 32);
        DrawArtwork(context, item, iconRect, 5);

        var fixedRight = typeWidth + sizeWidth + modifiedWidth + gap * 3;
        var nameWidth = Math.Max(24, surface.Width - iconColumn - gap - fixedRight);
        var nameX = iconColumn + gap;
        var typeX = nameX + nameWidth + gap;
        var sizeX = typeX + typeWidth + gap;
        var modifiedX = sizeX + sizeWidth + gap;

        DrawSingleLine(context, item.Name, new Rect(nameX, 0, nameWidth, surface.Height), 13, Foreground);
        DrawSingleLine(context, item.TypeDisplay, new Rect(typeX, 0, typeWidth, surface.Height), 12.5, MutedForeground);
        DrawSingleLine(context, item.SizeDisplay, new Rect(sizeX, 0, sizeWidth, surface.Height), 12.5, MutedForeground);
        DrawSingleLine(context, item.ModifiedDisplay, new Rect(modifiedX, 0, Math.Max(0, surface.Width - modifiedX - 4), surface.Height), 12.5, MutedForeground);
    }

    private void DrawIconMode(DrawingContext context, DriveItemModel item, Rect surface, bool extraLarge)
    {
        var padding = extraLarge ? 12d : 10d;
        var captionHeight = extraLarge ? 23d : 21d;
        var sizeHeight = extraLarge ? 20d : 18d;
        var artworkBottom = surface.Height - padding - captionHeight - sizeHeight - (extraLarge ? 12d : 9d);
        var artworkSize = Math.Max(32, Math.Min(
            extraLarge ? 132d : 94d,
            Math.Min(surface.Width - padding * 2, artworkBottom - padding)));
        var artworkX = (surface.Width - artworkSize) / 2;
        var artworkY = Math.Max(padding, (artworkBottom - artworkSize + padding) / 2);

        DrawArtwork(context, item, new Rect(artworkX, artworkY, artworkSize, artworkSize), extraLarge ? 11 : 9);

        var nameY = surface.Height - padding - sizeHeight - captionHeight - 2;
        DrawSingleLine(
            context,
            item.Name,
            new Rect(padding, nameY, Math.Max(1, surface.Width - padding * 2), captionHeight),
            extraLarge ? 13.5 : 13,
            Foreground,
            TextAlignment.Center,
            FontWeight.Medium);
        DrawSingleLine(
            context,
            item.SizeDisplay,
            new Rect(padding, surface.Height - padding - sizeHeight, Math.Max(1, surface.Width - padding * 2), sizeHeight),
            11.5,
            MutedForeground,
            TextAlignment.Center);
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
        var tabWidth = width * 0.42;
        var tabHeight = height * 0.18;

        context.DrawRectangle(FolderBack, null,
            new Rect(x + width * 0.04, top, width * 0.92, bodyHeight),
            Math.Max(2, width * 0.07), Math.Max(2, width * 0.07));
        context.DrawRectangle(FolderBack, null,
            new Rect(x + width * 0.08, y + height * 0.10, tabWidth, tabHeight),
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
        var bodyRect = new Rect(
            dest.X + insetX,
            dest.Y + insetY,
            Math.Max(8, dest.Width - insetX * 2),
            Math.Max(10, dest.Height - insetY * 2));
        var bodyRadius = Math.Max(3, radius * 0.75);
        context.DrawRectangle(body, null, bodyRect, bodyRadius, bodyRadius);

        var stripHeight = Math.Max(9, bodyRect.Height * 0.31);
        var strip = new Rect(bodyRect.X, bodyRect.Bottom - stripHeight, bodyRect.Width, stripHeight);
        context.DrawRectangle(accent, null, strip, bodyRadius, bodyRadius);

        var badge = item.IsImage ? "IMG" : item.IsVideo ? "VID" : item.IsAudio ? "MUS" : item.FileBadgeText;
        DrawSingleLine(context, badge, strip, Math.Clamp(stripHeight * 0.48, 6.5, 13), WhiteBrush, TextAlignment.Center, FontWeight.SemiBold);
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

    private static void DrawVideoBadge(DrawingContext context, Rect dest)
    {
        var diameter = Math.Clamp(Math.Min(dest.Width, dest.Height) * 0.25, 14, 26);
        var badgeRect = new Rect(dest.Right - diameter - 2, dest.Bottom - diameter - 2, diameter, diameter);
        context.DrawEllipse(VideoBadgeBrush, null, badgeRect);
        DrawSingleLine(context, "▶", badgeRect, Math.Max(7, diameter * 0.42), WhiteBrush, TextAlignment.Center, FontWeight.SemiBold);
    }

    private static void DrawSingleLine(
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

        var formatted = new FormattedText(
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

        var y = bounds.Y + Math.Max(0, (bounds.Height - formatted.Height) / 2d);
        context.DrawText(formatted, new Point(bounds.X, y));
    }
}
''', encoding="utf-8")


# ---------------------------------------------------------------------------
# 2) Replace all three desktop item trees with one self-drawn control each.
# ---------------------------------------------------------------------------
xaml = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml"
replace_template(xaml, "DesktopDetailsSlotTemplate", '''    <DataTemplate x:Key="DesktopDetailsSlotTemplate" x:DataType="models:VirtualDriveItemSlot">
      <controls:DesktopFileItemControl Mode="Details"
                                       Height="46"
                                       HorizontalAlignment="Stretch"
                                       VerticalAlignment="Stretch"
                                       ItemBackground="{DynamicResource HelloFileItemBrush}"
                                       SelectionBrush="{DynamicResource HelloExplorerSelectionBrush}"
                                       PointerPressed="FileItem_PointerPressed"
                                       DoubleTapped="FileItem_DoubleTapped"
                                       ContextRequested="FileItem_ContextRequested" />
    </DataTemplate>''')
replace_template(xaml, "DesktopLargeSlotTemplate", '''    <DataTemplate x:Key="DesktopLargeSlotTemplate" x:DataType="models:VirtualDriveItemSlot">
      <controls:DesktopFileItemControl Mode="LargeIcons"
                                       Height="162"
                                       HorizontalAlignment="Stretch"
                                       VerticalAlignment="Stretch"
                                       ItemBackground="{DynamicResource HelloFileItemBrush}"
                                       SelectionBrush="{DynamicResource HelloExplorerSelectionBrush}"
                                       PointerPressed="FileItem_PointerPressed"
                                       DoubleTapped="FileItem_DoubleTapped"
                                       ContextRequested="FileItem_ContextRequested" />
    </DataTemplate>''')
replace_template(xaml, "DesktopExtraLargeSlotTemplate", '''    <DataTemplate x:Key="DesktopExtraLargeSlotTemplate" x:DataType="models:VirtualDriveItemSlot">
      <controls:DesktopFileItemControl Mode="ExtraLargeIcons"
                                       Height="212"
                                       HorizontalAlignment="Stretch"
                                       VerticalAlignment="Stretch"
                                       ItemBackground="{DynamicResource HelloFileItemBrush}"
                                       SelectionBrush="{DynamicResource HelloExplorerSelectionBrush}"
                                       PointerPressed="FileItem_PointerPressed"
                                       DoubleTapped="FileItem_DoubleTapped"
                                       ContextRequested="FileItem_ContextRequested" />
    </DataTemplate>''')


# ---------------------------------------------------------------------------
# 3) Make the desktop scroll hot path do no thumbnail scanning/cancellation callbacks.
# ---------------------------------------------------------------------------
view = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"
replace_once(
    view,
    '''    private readonly HashSet<ScrollViewer> _hookedScrollViewers = [];
    private TopLevel? _topLevel;''',
    '''    private TopLevel? _topLevel;''',
    "remove desktop wheel-hook state",
)
replace_once(
    view,
    '''    private DateTime _desktopScrollLastActivityUtc;
    private DateTime _desktopThumbnailLastQueueUtc;
    private int _desktopThumbnailIdleRecoveryVersion;''',
    '''    private DateTime _desktopScrollLastActivityUtc;
    private int _desktopThumbnailIdleRecoveryVersion;''',
    "remove desktop per-scroll thumbnail timestamp",
)
replace_once(
    view,
    '''    private CancellationTokenSource? _backgroundUrlApplyCts;
    private IDisposable? _backgroundScrimBinding;''',
    '''    private CancellationTokenSource? _backgroundUrlApplyCts;
    private ContextMenu? _desktopFileItemContextMenu;
    private MenuItem? _desktopOpenWebMenuItem;
    private IDisposable? _backgroundScrimBinding;''',
    "desktop shared context menu fields",
)
replace_once(
    view,
    '''    private void HookListScrollViewers()
    {
        if (IsMobilePlatform)
            return; // Mobile ScrollViewers are explicit in XAML and use touch gesture handlers.

        foreach (var scroll in new[] { DesktopDetailsScrollViewer, DesktopLargeIconScrollViewer, DesktopExtraLargeIconScrollViewer })
        {
            if (_hookedScrollViewers.Add(scroll))
            {
                scroll.AddHandler(
                    InputElement.PointerWheelChangedEvent,
                    DesktopFileList_PointerWheelChanged,
                    RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                    true);
            }
        }
    }

    private void DesktopFileList_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (IsMobilePlatform || sender is not ScrollViewer scroll ||
            e.KeyModifiers.HasFlag(KeyModifiers.Control) || Math.Abs(e.Delta.Y) < 0.001)
            return;

        // Avalonia's default wheel step feels noticeably shorter than Explorer for these rows.
        // Keep precision touchpad deltas smooth, but make a regular mouse-wheel notch roughly
        // equal to 2.5-3 detail rows.
        var pixelsPerWheelUnit = Math.Abs(e.Delta.Y) < 0.9 ? 82.0 : 124.0;
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var targetY = Math.Clamp(scroll.Offset.Y - e.Delta.Y * pixelsPerWheelUnit, 0, maxY);
        if (Math.Abs(targetY - scroll.Offset.Y) < 0.01)
            return;

        scroll.Offset = new Vector(scroll.Offset.X, targetY);
        e.Handled = true;
    }''',
    '''    private void HookListScrollViewers()
    {
        // Intentionally empty. Let ScrollViewer handle mouse-wheel / precision-touchpad input
        // through Avalonia's normal scrolling path. Manually assigning Offset for every wheel
        // event forced synchronous realization/layout and made the desktop list feel stepped.
    }''',
    "restore default desktop wheel path",
)
replace_once(
    view,
    '''    private void HandleDesktopFileScroll(ScrollViewer scroll, MainViewModel vm)
    {
        var now = DateTime.UtcNow;
        _desktopScrollLastActivityUtc = now;
        unchecked { _desktopThumbnailIdleRecoveryVersion++; }
        vm.SetDesktopListScrolling(true);

        if (!_desktopScrollIdleTimer.IsEnabled)
            _desktopScrollIdleTimer.Start();

        // While the scrollbar thumb/wheel is moving, only decode thumbnails that already exist on
        // disk. Network work waits for the final viewport, so a long jump cannot build another
        // off-screen queue behind the six desktop workers.
        if ((now - _desktopThumbnailLastQueueUtc).TotalMilliseconds >= 90)
        {
            _desktopThumbnailLastQueueUtc = now;
            QueueRealizedDesktopThumbnails(scroll, vm, allowNetwork: false);
        }
    }''',
    '''    private void HandleDesktopFileScroll(ScrollViewer scroll, MainViewModel vm)
    {
        _desktopScrollLastActivityUtc = DateTime.UtcNow;
        unchecked { _desktopThumbnailIdleRecoveryVersion++; }
        vm.SetDesktopListScrolling(true);

        if (!_desktopScrollIdleTimer.IsEnabled)
            _desktopScrollIdleTimer.Start();

        // Do no thumbnail traversal, disk probing or bitmap decoding in the active-scroll path.
        // The idle handler warms current +/- one viewport after 150 ms. Already decoded thumbnails
        // remain attached to their DriveItemModel and therefore continue drawing while scrolling.
    }''',
    "strip thumbnail work from desktop scroll frames",
)
replace_once(
    view,
    '''            var slot = element.DataContext as VirtualDriveItemSlot
                ?? element.GetVisualDescendants().OfType<Control>()
                    .Select(static control => control.DataContext as VirtualDriveItemSlot)
                    .FirstOrDefault(static candidate => candidate is not null);
            if (slot is null)
                continue;''',
    '''            // Desktop templates are now a single self-drawn Control whose inherited
            // DataContext is the slot itself. Avoid walking a deep visual subtree on every scan.
            if (element.DataContext is not VirtualDriveItemSlot slot)
                continue;''',
    "avoid desktop visual descendant traversal",
)

old_context = '''    private void FileItem_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Mobile uses custom long-press selection; never allow the platform context-menu gesture
        // to compete with scrolling. Desktop keeps the normal right-click menu.
        if (IsMobilePlatform)
            e.Handled = true;
    }'''
new_context = '''    private void FileItem_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Mobile uses custom long-press selection; never allow the platform context-menu gesture
        // to compete with scrolling.
        if (IsMobilePlatform)
        {
            e.Handled = true;
            return;
        }

        if (sender is not Control control || GetDriveItemFromDataContext(control.DataContext) is not { } item)
            return;

        _contextItem = item;
        SelectContextItem(item);
        var menu = GetOrCreateDesktopFileItemContextMenu();
        if (_desktopOpenWebMenuItem is not null)
            _desktopOpenWebMenuItem.IsVisible = item.HasWebUrl;
        menu.Open(control);
        e.Handled = true;
    }

    private ContextMenu GetOrCreateDesktopFileItemContextMenu()
    {
        if (_desktopFileItemContextMenu is not null)
            return _desktopFileItemContextMenu;

        var open = new MenuItem { Header = "打开" };
        open.Click += FileContext_Open_Click;
        var download = new MenuItem { Header = "下载" };
        download.Click += FileContext_Download_Click;
        var cache = new MenuItem { Header = "缓存" };
        cache.Click += FileContext_Cache_Click;
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += FileContext_Rename_Click;
        var delete = new MenuItem { Header = "删除" };
        delete.Click += FileContext_Delete_Click;
        _desktopOpenWebMenuItem = new MenuItem { Header = "在 OneDrive 网页中打开" };
        _desktopOpenWebMenuItem.Click += FileContext_OpenWeb_Click;

        _desktopFileItemContextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                open,
                new Separator(),
                download,
                cache,
                rename,
                delete,
                new Separator(),
                _desktopOpenWebMenuItem
            }
        };
        return _desktopFileItemContextMenu;
    }'''
replace_once(view, old_context, new_context, "shared desktop item context menu")


# ---------------------------------------------------------------------------
# 4) Detach CancellationToken callbacks from the first desktop scroll frame.
# ---------------------------------------------------------------------------
vm = ROOT / "src/Hello1Drive.Core/ViewModels/MainViewModel.cs"
replace_once(
    vm,
    '''            CancelThumbnailLoading();
        }
    }

    public int GetMobileItemIndex''',
    '''            CancelThumbnailLoading(deferCallbacks: true);
        }
    }

    public int GetMobileItemIndex''',
    "defer desktop thumbnail cancellation callbacks",
)
replace_once(
    vm,
    '''    private void CancelThumbnailLoading()
    {
        // Move to a fresh logical generation before cancelling. StartThumbnailLoading can then
        // replace stale in-flight markers immediately instead of waiting for cancelled workers
        // to unwind their network/decode awaits.
        Interlocked.Increment(ref _thumbnailLoadGeneration);
        var cts = _thumbnailLoadCts;
        _thumbnailLoadCts = null;
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }''',
    '''    private void CancelThumbnailLoading(bool deferCallbacks = false)
    {
        // Move to a fresh logical generation immediately. Stale workers will discard their result
        // even if cancellation callbacks are dispatched asynchronously a moment later.
        Interlocked.Increment(ref _thumbnailLoadGeneration);
        var cts = _thumbnailLoadCts;
        _thumbnailLoadCts = null;
        if (cts is null)
            return;

        if (deferCallbacks)
        {
            // CancellationTokenSource.Cancel invokes registered callbacks synchronously. Doing that
            // on the first ScrollChanged frame can visibly hitch if HTTP/decode work is active.
            _ = Task.Run(() =>
            {
                try { cts.Cancel(); }
                catch { }
                finally { cts.Dispose(); }
            });
            return;
        }

        try { cts.Cancel(); }
        finally { cts.Dispose(); }
    }''',
    "non-blocking desktop thumbnail cancellation",
)

print("Desktop file items now use a single self-drawn control and a minimal scroll hot path.")
