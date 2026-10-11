using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using System.Reflection;
using Hello1Drive.Services;
using Hello1Drive.Views;
using Hello1Drive.ViewModels;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Hello1Drive.Controls;
using Hello1Drive.Models;

AppServices.Configure(new NoAuthentication());
AppBuilder.Configure<Hello1Drive.App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .SetupWithoutStarting();

foreach (var mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.ExtraLargeIcons })
foreach (var count in new[] { 1000, 5000, 10000 })
    CheckFolder(mode, count);
CheckIncrementalSlots();
CheckSourceReplacementAndOverlays();
CheckDesktopInput();
Console.WriteLine("PASS: all desktop virtualization regressions");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static DriveItemModel Item(int index) => new() { Id = $"file-{index}", Name = $"文件 {index}.txt", Size = index };

static ObservableCollection<VirtualDriveItemSlot> Slots(int count, bool hydrate = true) =>
    new(Enumerable.Range(0, count).Select(index => new VirtualDriveItemSlot(index, hydrate ? Item(index) : null)));

static void Pump(Window window, ScrollViewer scroll, DesktopVirtualFileSurface surface)
{
    for (var i = 0; i < 3; i++)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        surface.SetViewport(scroll.Offset.Y, scroll.Viewport.Height, scroll.Viewport.Width);
        surface.UpdateLayout();
    }
}

static (Window Window, ScrollViewer Scroll, DesktopVirtualFileSurface Surface) Create(
    FileViewMode mode, ObservableCollection<VirtualDriveItemSlot> slots)
{
    var surface = new DesktopVirtualFileSurface { Mode = mode, ItemsSource = slots };
    var scroll = new ScrollViewer
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        Content = surface
    };
    var window = new Window { Width = 1040, Height = 600, Content = scroll };
    window.Show();
    Pump(window, scroll, surface);
    return (window, scroll, surface);
}

static void CheckFolder(FileViewMode mode, int count)
{
    var slots = Slots(count);
    var (window, scroll, surface) = Create(mode, slots);
    try
    {
        var firstBatch = surface.GetRealizedContainers().ToHashSet();
        Assert(firstBatch.Count > 0 && firstBatch.Count < 500, $"{mode}/{count}: bounded first realization");
        Assert(surface.ContainerFromIndex(0)?.DataContext == slots[0], "first item binding");

        scroll.Offset = new Vector(0, surface.GetItemTop(count / 2));
        Pump(window, scroll, surface);
        var secondBatch = surface.GetRealizedContainers().ToHashSet();
        Assert(secondBatch.Count < 500 && secondBatch.Overlaps(firstBatch), "containers recycled on scrollbar jump");
        Assert(surface.ContainerFromIndex(count / 2)?.DataContext == slots[count / 2], "recycled container uses current slot");
        Assert(surface.ContainerFromIndex(0) is null, "off-screen container released");

        scroll.Offset = new Vector(0, scroll.ScrollBarMaximum.Y);
        Pump(window, scroll, surface);
        var last = surface.ContainerFromIndex(count - 1);
        Assert(last is not null && last.DataContext == slots[count - 1], "last item is realized");
        var lastRect = surface.GetItemBounds(count - 1);
        Assert(lastRect.Bottom <= scroll.Extent.Height + 0.01, "last item fits exact extent");
        Assert(lastRect.Bottom <= scroll.Offset.Y + scroll.Viewport.Height + 0.01, "last item fully visible at bottom");
        var bottomOffset = scroll.Offset.Y;
        scroll.Offset = new Vector(0, bottomOffset + 500);
        Pump(window, scroll, surface);
        Assert(Math.Abs(scroll.Offset.Y - bottomOffset) < 0.01, "scrolling beyond bottom does not jump");

        var center = lastRect.Center;
        Assert(surface.GetItemAt(center)?.Id == slots[^1].Id, "geometry hit test matches last card");
        Assert(surface.GetItemsIntersecting(lastRect).Any(item => item.Id == slots[^1].Id), "marquee includes last card");
        window.Width = 460;
        Pump(window, scroll, surface);
        Assert(surface.GetRealizedContainers().Count() < 500, "resize remains virtualized");
        surface.Mode = mode == FileViewMode.Details ? FileViewMode.ExtraLargeIcons : FileViewMode.Details;
        surface.RefreshLayout();
        Pump(window, scroll, surface);
        scroll.Offset = new Vector(0, scroll.ScrollBarMaximum.Y);
        Pump(window, scroll, surface);
        Assert(surface.ContainerFromIndex(count - 1) is not null, "view-mode change retains complete extent");
        Console.WriteLine($"PASS: {mode}, {count} items, {firstBatch.Count}/{secondBatch.Count} realized");
    }
    finally
    {
        window.Close();
        foreach (var slot in slots) slot.Dispose();
    }
}

static void CheckIncrementalSlots()
{
    var slots = Slots(10000, hydrate: false);
    var (window, scroll, surface) = Create(FileViewMode.LargeIcons, slots);
    try
    {
        var container = surface.ContainerFromIndex(0);
        var extent = scroll.Extent.Height;
        for (var i = 0; i < 200; i++) slots[i].SetItem(Item(i), compactNotification: true);
        Pump(window, scroll, surface);
        Assert(ReferenceEquals(container, surface.ContainerFromIndex(0)), "page hydration preserves container");
        Assert(Math.Abs(scroll.Extent.Height - extent) < 0.01, "hydration preserves fixed slots and extent");
        Assert(surface.GetItemAt(surface.GetItemBounds(0).Center)?.Id == "file-0", "placeholder becomes selectable");
        scroll.Offset = new Vector(0, surface.GetItemTop(5000));
        Pump(window, scroll, surface);
        for (var i = 5000; i < 5200; i++) slots[i].SetItem(Item(i), compactNotification: true);
        Pump(window, scroll, surface);
        Assert(surface.GetItemAt(surface.GetItemBounds(5000).Center)?.Id == "file-5000", "later page uses its fixed slots");
        slots.Add(new VirtualDriveItemSlot(slots.Count, Item(slots.Count)));
        Pump(window, scroll, surface);
        Assert(scroll.Extent.Height >= extent && surface.GetRealizedContainers().Count() < 500, "append stays virtualized");
        Console.WriteLine("PASS: placeholder hydration and page append");
    }
    finally
    {
        window.Close();
        foreach (var slot in slots) slot.Dispose();
    }
}

static void CheckSourceReplacementAndOverlays()
{
    var slots = Slots(1000);
    var (window, scroll, surface) = Create(FileViewMode.ExtraLargeIcons, slots);
    var replacement = Slots(30);
    try
    {
        var root = new Grid();
        window.Content = null;
        root.Children.Add(scroll);
        var overlay = new Border { IsVisible = false };
        root.Children.Add(overlay);
        window.Content = root;
        Pump(window, scroll, surface);
        scroll.Offset = new Vector(0, surface.GetItemTop(400));
        Pump(window, scroll, surface);
        var offset = scroll.Offset;
        var containers = surface.GetRealizedContainers().ToArray();
        overlay.IsVisible = true;
        Pump(window, scroll, surface);
        overlay.IsVisible = false;
        Pump(window, scroll, surface);
        Assert(scroll.Offset == offset && surface.GetRealizedContainers().SequenceEqual(containers), "overlay does not swap or reset list");
        surface.ItemsSource = replacement;
        scroll.Offset = default;
        Pump(window, scroll, surface);
        Assert(surface.ContainerFromIndex(0)?.DataContext == replacement[0], "source replacement clears old bindings");
        surface.ItemsSource = Array.Empty<VirtualDriveItemSlot>();
        Pump(window, scroll, surface);
        Assert(!surface.GetRealizedContainers().Any(), "empty folder has no realized items");
        Console.WriteLine("PASS: overlays, source replacement, empty folder");
    }
    finally
    {
        window.Close();
        foreach (var slot in slots.Concat(replacement)) slot.Dispose();
    }
}

static void CheckDesktopInput()
{
    var vm = new MainViewModel(AppServices.OneDrive, AppServices.Authentication, AppServices.Settings,
        AppServices.FileCache, AppServices.ThumbnailCache, AppServices.TransferPersistence,
        AppServices.StartupSnapshot, AppServices.LocalDriveIndex);
    vm.InitializeAsync().GetAwaiter().GetResult();
    vm.IsAuthenticated = true;
    vm.ViewMode = FileViewMode.Details;
    var items = Enumerable.Range(0, 300)
        .Select(i => new DriveItemModel { Id = $"input-{i}", Name = $"{i:D4}.bin" }).ToArray();
    typeof(MainViewModel).GetMethod("ApplyFolderItems", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(vm, [items, items.Length]);
    var view = new MainView { DataContext = vm };
    var window = new Window { Width = 1040, Height = 700, Content = view };
    window.Show();
    view.FindControl<Control>("StartupSplashOverlay")!.IsVisible = false;
    var surface = view.FindControl<DesktopVirtualFileSurface>("DesktopFileSurface")!;
    var scroll = view.FindControl<ScrollViewer>("DesktopVirtualScrollViewer")!;
    try
    {
        Pump(window, scroll, surface);
        void Click(int index, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
        {
            var point = surface.TranslatePoint(surface.GetItemBounds(index).Center, window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, button, modifiers);
            window.MouseUp(point, button, modifiers);
            Pump(window, scroll, surface);
        }
        Click(0);
        Assert(vm.SelectionCount == 1 && vm.SelectedItem?.Id == "input-0", "single pointer selection");
        Click(1, RawInputModifiers.Control);
        Assert(vm.SelectionCount == 2, "Ctrl pointer toggles additional item");
        Click(4, RawInputModifiers.Shift);
        Assert(vm.SelectionCount == 4 && vm.SelectedItemsSnapshot.All(item => item.IsMobileSelected), "Shift pointer range selection");
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Pump(window, scroll, surface);
        Assert(vm.SelectionCount == 1 && vm.SelectedItem?.Id == "input-5", "keyboard arrow selects next item");
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        window.KeyRelease(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Pump(window, scroll, surface);
        Assert(vm.SelectedItem?.Id == "input-299" && surface.ContainerFromIndex(299) is not null, "keyboard End selects and reveals last item");
        Click(299, button: MouseButton.Right);
        Assert(vm.SelectedItem?.Id == "input-299", "right click preserves current selection");
        var offset = scroll.Offset;
        vm.IsTransferPanelVisible = true;
        Pump(window, scroll, surface);
        vm.IsTransferPanelVisible = false;
        Pump(window, scroll, surface);
        Assert(scroll.IsVisible && scroll.Offset == offset, "real transfer panel leaves same Avalonia list and scroll position");
        Console.WriteLine("PASS: MainView pointer, Ctrl/Shift selection, keyboard and transfer overlay");
    }
    finally
    {
        window.Close();
        foreach (var slot in vm.VirtualItems) slot.Dispose();
    }
}

public sealed class NoAuthentication : IAuthenticationService
{
    public Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool TryHandleProtocolActivation(Uri uri) => false;
}
