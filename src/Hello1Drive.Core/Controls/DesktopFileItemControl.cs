using System.ComponentModel;
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
    private DriveItemModel? _item;
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

        AttachItem(null);
        _slot = slot;

        if (_slot is not null)
        {
            _slot.PropertyChanged += Slot_PropertyChanged;
            AttachItem(_slot.Item);
        }

        InvalidateVisual();
    }

    private void AttachItem(DriveItemModel? item)
    {
        if (ReferenceEquals(_item, item))
            return;

        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;

        _item = item;
        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetItem raises Item first. Attach once to the new model and ignore all legacy forwarded
        // property notifications; the self-drawn desktop control reads the model directly.
        if (e.PropertyName == nameof(VirtualDriveItemSlot.Item))
        {
            AttachItem(_slot?.Item);
            InvalidateVisual();
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ThumbnailImage already implies HasThumbnailImage/HasNoThumbnailImage/video-badge state.
        // Reacting to those forwarded companion notifications caused four redraw invalidations for
        // one decoded bitmap. Selection is the only other live visual state used on desktop.
        if (e.PropertyName is nameof(DriveItemModel.ThumbnailImage) or nameof(DriveItemModel.IsMobileSelected))
            InvalidateVisual();
    }

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
