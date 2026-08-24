from pathlib import Path

path = Path(__file__).resolve().parents[1] / "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"
text = path.read_text(encoding="utf-8")
old = "        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? Dp(6);"
new = "        _touchSlop = (float)(ViewConfiguration.Get(context)?.ScaledTouchSlop ?? (int)MathF.Round(Dp(6)));"
if text.count(old) != 1:
    raise RuntimeError(f"Expected one touch-slop line, found {text.count(old)}")
path.write_text(text.replace(old, new), encoding="utf-8")
print("Fixed Android native FAB touch-slop type compatibility.")
