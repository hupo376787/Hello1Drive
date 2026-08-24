from pathlib import Path

path = Path(__file__).resolve().parents[1] / "src/Hello1Drive.Core/Views/MainView.axaml"
text = path.read_text(encoding="utf-8")
start = text.index('    <!-- Mobile view-mode action panel.')
end = text.index('    <!-- Mobile sort action panel.', start)
block = text[start:end]
old = '''              Background="#F0444444"
              BorderBrush="#28FFFFFF" BorderThickness="1">'''
new = '''              Background="#F0444444"
              BorderBrush="#28FFFFFF" BorderThickness="1"
              PointerPressed="OverlayContent_PointerPressed">'''
if block.count(old) != 1:
    raise RuntimeError(f"Expected one view-mode content border, found {block.count(old)}")
block = block.replace(old, new, 1)
text = text[:start] + block + text[end:]
path.write_text(text, encoding="utf-8")
print("View-mode overlay pointer routing fixed.")
