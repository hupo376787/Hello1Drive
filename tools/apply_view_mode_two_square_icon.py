from pathlib import Path

path = Path(__file__).resolve().parents[1] / "src/Hello1Drive.Core/Views/MainView.axaml"
text = path.read_text(encoding="utf-8")
old = 'Data="M1.5,2.8 H5 M1.5,7 H5 M1.5,11.2 H5 M7.2,2 H9.6 V4.4 H7.2 Z M10.5,2 H12.9 V4.4 H10.5 Z M7.2,8 H9.6 V10.4 H7.2 Z M10.5,8 H12.9 V10.4 H10.5 Z"'
new = 'Data="M1.5,2.4 H5.4 M1.5,7 H5.4 M1.5,11.6 H5.4 M8.4,2.4 H11.8 V5.8 H8.4 Z M8.4,8.2 H11.8 V11.6 H8.4 Z"'
count = text.count(old)
if count != 2:
    raise RuntimeError(f"Expected exactly 2 current view-mode toolbar icons, found {count}")
text = text.replace(old, new)
path.write_text(text, encoding="utf-8")
print("Updated both view-mode toolbar icons to three lines plus two hollow squares.")