from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, got {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


surface = ROOT / "src/Hello1Drive.Core/Controls/DesktopVirtualFileSurface.cs"
replace_once(
    surface,
    "    private int _renderTo = -1;\n    private int _hoverIndex = -1;\n",
    "    private int _renderTo = -1;\n    private int _hoverIndex = -1;\n    private bool _deferredHoverVisualRefresh;\n",
    "surface hover field",
)
replace_once(
    surface,
    """    public void ClearHoverForScroll()\n    {\n        if (_hoverIndex < 0)\n            return;\n        _hoverIndex = -1;\n        InvalidateVisual();\n    }\n""",
    """    public void ClearHoverForScroll()\n    {\n        if (_hoverIndex < 0)\n            return;\n\n        // Do not redraw the three-viewport retained scene on the first wheel/scrollbar frame.\n        // The old hover highlight can remain in the already-recorded scene for the short active\n        // scroll interval; it is cleared once input becomes idle or by an unrelated scene rebuild.\n        _hoverIndex = -1;\n        _deferredHoverVisualRefresh = true;\n    }\n\n    public void FlushDeferredHoverVisual()\n    {\n        if (!_deferredHoverVisualRefresh)\n            return;\n\n        _deferredHoverVisualRefresh = false;\n        InvalidateVisual();\n    }\n""",
    "defer hover redraw",
)
replace_once(
    surface,
    """        PointerExited += (_, _) =>\n        {\n            if (_hoverIndex < 0)\n                return;\n            _hoverIndex = -1;\n            InvalidateVisual();\n        };\n""",
    """        PointerExited += (_, _) =>\n        {\n            if (_hoverIndex < 0)\n                return;\n            _hoverIndex = -1;\n            _deferredHoverVisualRefresh = false;\n            InvalidateVisual();\n        };\n""",
    "pointer exit hover",
)
replace_once(
    surface,
    """    public override void Render(DrawingContext context)\n    {\n        base.Render(context);\n        var vm = _vm;\n""",
    """    public override void Render(DrawingContext context)\n    {\n        base.Render(context);\n        // Any real scene rebuild has already removed a hover that was deferred at scroll start.\n        _deferredHoverVisualRefresh = false;\n        var vm = _vm;\n""",
    "render clears deferred hover",
)
replace_once(
    surface,
    """    private void Surface_PointerMoved(object? sender, PointerEventArgs e)\n    {\n        var index = GetIndexAt(e.GetPosition(this));\n        if (index == _hoverIndex)\n            return;\n        _hoverIndex = index;\n        InvalidateVisual();\n    }\n""",
    """    private void Surface_PointerMoved(object? sender, PointerEventArgs e)\n    {\n        var index = GetIndexAt(e.GetPosition(this));\n        if (index == _hoverIndex)\n            return;\n        _hoverIndex = index;\n        _deferredHoverVisualRefresh = false;\n        InvalidateVisual();\n    }\n""",
    "pointer moved hover",
)

main = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"
replace_once(
    main,
    """        _desktopScrollIdleTimer.Stop();\n        vm.SetDesktopListScrolling(false);\n        var scroll = GetActiveScrollViewer(vm);\n""",
    """        _desktopScrollIdleTimer.Stop();\n        vm.SetDesktopListScrolling(false);\n        // Hover cleanup is intentionally deferred until scrolling is quiet. Invalidating the\n        // retained file surface on the very first wheel frame makes that first movement compete\n        // with a full three-viewport redraw.\n        DesktopFileSurface.FlushDeferredHoverVisual();\n        var scroll = GetActiveScrollViewer(vm);\n""",
    "idle hover flush",
)

input_service = ROOT / "src/Hello1Drive.Desktop/Services/DesktopInputSettingsService.cs"
input_service.write_text(
    """using System.Runtime.InteropServices;\nusing Hello1Drive.Services;\n\nnamespace Hello1Drive.Desktop.Services;\n\ninternal sealed class DesktopInputSettingsService : IDesktopInputSettingsService\n{\n    private const uint SpiGetWheelScrollLines = 0x0068;\n    private const int DefaultWindowsWheelLines = 3;\n\n    // Read the Windows preference once while the desktop services are created. Mouse-wheel input\n    // must never synchronously cross the user32 boundary after the user has started scrolling.\n    private readonly int _wheelLines = ReadWheelLines();\n\n    public int GetMouseWheelScrollLines() => _wheelLines;\n\n    private static int ReadWheelLines()\n    {\n        if (!OperatingSystem.IsWindows())\n            return DesktopScrollSettings.UseFrameworkDefault;\n\n        if (!SystemParametersInfo(SpiGetWheelScrollLines, 0, out var lines, 0))\n            return DefaultWindowsWheelLines;\n\n        return lines == uint.MaxValue\n            ? DesktopScrollSettings.ScrollByPage\n            : (int)Math.Min(lines, 100u);\n    }\n\n    [DllImport(\"user32.dll\", EntryPoint = \"SystemParametersInfoW\", SetLastError = true)]\n    [return: MarshalAs(UnmanagedType.Bool)]\n    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);\n}\n""",
    encoding="utf-8",
)
