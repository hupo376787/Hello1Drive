using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hello1Drive.Models;

namespace Hello1Drive.Controls;

public sealed partial class DesktopVirtualFileSurface
{
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new DesktopFileItem(this);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        ((DesktopFileItem)container).Bind(item as VirtualDriveItemSlot, index);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        ((DesktopFileItem)container).Bind(null, -1);
        base.ClearContainerForItemOverride(container);
    }

    // One lightweight retained visual per realized file. Recycling detaches the old slot before
    // subscribing to the new one, so off-screen metadata/images cannot invalidate recycled cards.
    private sealed class DesktopFileItem(DesktopVirtualFileSurface owner) : Control
    {
        public VirtualDriveItemSlot? Slot { get; private set; }
        public int Index { get; private set; } = -1;

        public void Bind(VirtualDriveItemSlot? slot, int index)
        {
            if (ReferenceEquals(Slot, slot) && Index == index)
                return;
            if (Slot is not null)
                Slot.PropertyChanged -= Slot_PropertyChanged;
            Slot = slot;
            Index = index;
            if (slot is not null)
                slot.PropertyChanged += Slot_PropertyChanged;
            InvalidateVisual();
        }

        private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is
                nameof(VirtualDriveItemSlot.Item) or nameof(VirtualDriveItemSlot.ThumbnailImage) or
                nameof(VirtualDriveItemSlot.IsMobileSelected) or nameof(VirtualDriveItemSlot.IsPlaceholder) or
                nameof(VirtualDriveItemSlot.Name) or nameof(VirtualDriveItemSlot.IconMetadataDisplay) or
                nameof(VirtualDriveItemSlot.SizeDisplay) or nameof(DriveItemModel.TypeDisplay) or
                nameof(DriveItemModel.ModifiedDisplay))
                InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => availableSize;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Slot is not null)
            {
                using (context.PushClip(new Rect(Bounds.Size)))
                    owner.RenderItem(context, Slot, Index, Bounds.Size);
            }
        }
    }

    // Exact fixed-size geometry avoids estimated extents and bottom-edge scrollbar jumps. The
    // same panel handles single-column details and multi-column icon views without rebuilding
    // the ItemsSource. Realization is independent of how many Graph metadata pages are loaded.
    private sealed class DesktopFileVirtualizingPanel : VirtualizingPanel
    {
        private readonly DesktopVirtualFileSurface _owner;
        private readonly Dictionary<int, DesktopFileItem> _realized = [];
        private readonly Stack<DesktopFileItem> _pool = [];
        private int _first = -1;
        private int _last = -1;

        public DesktopFileVirtualizingPanel(DesktopVirtualFileSurface owner)
        {
            _owner = owner;
            DetachedFromVisualTree += (_, _) =>
            {
                foreach (var item in _realized.Values)
                    item.Bind(null, -1);
                _first = _last = -1;
            };
            AttachedToVisualTree += (_, _) => RefreshViewport(force: true);
        }

        public void RefreshViewport(bool force)
        {
            var (first, last) = _owner.GetVisibleRange();
            if (force || _first < 0 || first < _first || last > _last || Items.Count == 0)
                InvalidateMeasure();
        }

        public void InvalidateItem(int index)
        {
            if (_realized.TryGetValue(index, out var item))
                item.InvalidateVisual();
        }

        public void InvalidateRealizedItems()
        {
            foreach (var item in _realized.Values)
                item.InvalidateVisual();
        }

        protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
        {
            // A continuation page appended to the tail does not change existing containers.
            var appendOnly = e.Action == NotifyCollectionChangedAction.Add &&
                e.NewItems is { Count: > 0 } && e.NewStartingIndex + e.NewItems.Count == items.Count;
            var removeTail = e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex == items.Count;
            foreach (var index in _realized.Keys.ToArray())
            {
                if ((!appendOnly && !removeTail) || index >= items.Count)
                    Recycle(index);
            }
            _first = _last = -1;
            InvalidateMeasure();
        }

        protected override void OnItemsControlChanged(ItemsControl? oldValue)
        {
            foreach (var item in _realized.Values)
                item.Bind(null, -1);
            _realized.Clear();
            _pool.Clear();
            _first = _last = -1;
            base.OnItemsControlChanged(oldValue);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) && availableSize.Width > 1
                ? availableSize.Width : _owner._viewportWidth;
            var (first, last) = _owner.CalculateVisibleRange(_owner._viewportOffsetY, _owner._viewportHeight, width);
            if (first >= 0 && last >= first)
            {
                var visibleCount = last - first + 1;
                first = Math.Max(0, first - visibleCount);
                last = Math.Min(Items.Count - 1, last + visibleCount);
                if (_owner.Mode != FileViewMode.Details)
                {
                    var columns = _owner.GetGridMetrics(width).Columns;
                    first = first / columns * columns;
                    last = Math.Min(Items.Count - 1, ((last / columns + 1) * columns) - 1);
                }
            }
            else
                first = last = -1;

            foreach (var index in _realized.Keys.ToArray())
            {
                if (index < first || index > last || index >= Items.Count)
                    Recycle(index);
            }
            for (var index = first; index >= 0 && index <= last; index++)
            {
                if (!_realized.TryGetValue(index, out var container))
                {
                    var generator = ItemContainerGenerator!;
                    generator.NeedsContainer(Items[index], index, out var key);
                    container = _pool.Count > 0 ? _pool.Pop() : (DesktopFileItem)generator.CreateContainer(Items[index], index, key);
                    generator.PrepareItemContainer(container, Items[index], index);
                    container.IsVisible = true;
                    if (!Children.Contains(container))
                        AddInternalChild(container);
                    _realized.Add(index, container);
                    generator.ItemContainerPrepared(container, Items[index], index);
                }
                else if (!ReferenceEquals(container.Slot, Items[index]))
                    ItemContainerGenerator!.PrepareItemContainer(container, Items[index], index);

                var rect = ItemRect(index, width);
                container.Measure(rect.Size);
            }
            // A previous very large window must not keep thousands of hidden visuals alive.
            while (_pool.Count > 32)
                RemoveInternalChild(_pool.Pop());
            _first = first;
            _last = last;
            return new Size(width, _owner.CalculateExtentHeight(Items.Count, width));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var (index, container) in _realized)
                container.Arrange(ItemRect(index, finalSize.Width));
            return finalSize;
        }

        private Rect ItemRect(int index, double width) => _owner.Mode == FileViewMode.Details
            ? new Rect(0, index * DetailsRowHeight, width, DetailsRowHeight)
            : _owner.GetGridItemRect(index, _owner.GetGridMetrics(width));

        private void Recycle(int index)
        {
            var container = _realized[index];
            _realized.Remove(index);
            ItemContainerGenerator!.ClearItemContainer(container);
            container.IsVisible = false;
            _pool.Push(container);
        }

        protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);
        protected override int IndexFromContainer(Control container) => container is DesktopFileItem item ? item.Index : -1;
        protected override IEnumerable<Control> GetRealizedContainers() => _realized.Values;

        protected override Control? ScrollIntoView(int index)
        {
            if (index < 0 || index >= Items.Count)
                return null;
            var rect = _owner.GetItemBounds(index);
            if (_owner.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            {
                var offset = scroll.Offset.Y;
                if (rect.Top < offset)
                    offset = rect.Top;
                else if (rect.Bottom > offset + scroll.Viewport.Height)
                    offset = Math.Max(0, rect.Bottom - scroll.Viewport.Height);
                scroll.Offset = new Vector(0, offset);
                _owner.SetViewport(offset, scroll.Viewport.Height, scroll.Viewport.Width);
            }
            RefreshViewport(force: true);
            _owner.UpdateLayout();
            return ContainerFromIndex(index);
        }

        protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
        {
            var index = from is Control control ? IndexFromContainer(control) : -1;
            var columns = _owner.Columns;
            var target = direction switch
            {
                NavigationDirection.First => 0,
                NavigationDirection.Last => Items.Count - 1,
                NavigationDirection.Up => index - columns,
                NavigationDirection.Down => index + columns,
                NavigationDirection.Previous or NavigationDirection.Left => index - 1,
                _ => index + 1
            };
            return ScrollIntoView(Math.Clamp(target, 0, Math.Max(0, Items.Count - 1)));
        }
    }
}
