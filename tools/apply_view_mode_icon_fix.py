from pathlib import Path
import re

path = Path(__file__).resolve().parents[1] / "src/Hello1Drive.Core/Views/MainView.axaml"
text = path.read_text(encoding="utf-8")

new_data = (
    "M1.5,2.8 H5 M1.5,7 H5 M1.5,11.2 H5 "
    "M7.2,2 H9.6 V4.4 H7.2 Z M10.5,2 H12.9 V4.4 H10.5 Z "
    "M7.2,8 H9.6 V10.4 H7.2 Z M10.5,8 H12.9 V10.4 H10.5 Z"
)

pattern = re.compile(
    r'(<Button Classes="viewMode" ToolTip\.Tip="查看方式"[^>]*>\s*'
    r'<Path Classes="toolbarIcon iconView opticalUp" Data=")[^"]+(" />)'
)

text, count = pattern.subn(lambda m: m.group(1) + new_data + m.group(2), text)
if count != 2:
    raise RuntimeError(f"Expected exactly 2 view-mode toolbar icons, found {count}")

path.write_text(text, encoding="utf-8")
print("Updated desktop and mobile view-mode icons to list+grid hybrid glyph.")
