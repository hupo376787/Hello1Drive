from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def replace_regex(relative_path: str, pattern: str, replacement: str, expected_min: int = 1) -> int:
    path = ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    updated, count = re.subn(pattern, replacement, text, flags=re.S)
    if count < expected_min:
        raise RuntimeError(f"Expected at least {expected_min} matches in {relative_path}, got {count}")
    path.write_text(updated, encoding="utf-8")
    print(f"{relative_path}: replaced {count} block(s)")
    return count


# Avalonia: replace every file-list folder canvas that still contains the old Windows 11 artwork.
xaml = "src/Hello1Drive.Core/Views/MainView.axaml"
new_xaml_folder = '''<Canvas Width="32" Height="26">
                          <!-- Layered yellow folder matched to the supplied OneDrive folder artwork. -->
                          <Path Fill="#F7BC0F"
                                Data="M0,7.5 C0,5.1 1.9,3.1 4.3,3.1 H9.7 C10.7,3.1 11.4,3.4 12.2,4 L15.2,6.3 C16.2,7.1 17.3,7.4 18.7,7.4 H29.5 C30.9,7.4 32,8.5 32,9.9 V22.3 C32,24.3 30.3,26 28.3,26 H3.7 C1.7,26 0,24.3 0,22.3 Z" />
                          <Border Width="24.5" Height="12.6" Canvas.Left="4.4" Canvas.Top="8.8"
                                  Background="#FFD28D" CornerRadius="1.2" />
                          <Border Width="24.5" Height="11.8" Canvas.Left="3.2" Canvas.Top="10.1"
                                  Background="#FFF2D6" CornerRadius="1.2" />
                          <Border Width="32" Height="14.5" Canvas.Left="0" Canvas.Top="11.5"
                                  Background="#FFD76B" CornerRadius="2.1" />
                          <Border Width="28" Height="1.2" Canvas.Left="2" Canvas.Top="12.2"
                                  Background="#55FFE9B0" CornerRadius="0.6" />
                        </Canvas>'''
replace_regex(
    xaml,
    r'<Canvas Width="20" Height="18">\s*<!-- Windows 11 style: raised tab/back plate \+ brighter rounded front face\. -->.*?</Canvas>',
    new_xaml_folder,
    expected_min=1)

# Android native RecyclerView: use the same four-layer folder silhouette and colors.
android = "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"
new_android_folder = '''    private void DrawFolder(Canvas canvas, RectF rect)
    {
        // Four-layer yellow folder matched to the supplied OneDrive folder artwork.
        // Keep the hot path allocation-free: the same reusable Path/Paint are used for every cell.
        const float sourceWidth = 32f;
        const float sourceHeight = 26f;
        var scale = Math.Min(rect.Width() / sourceWidth, rect.Height() / sourceHeight);
        var left = rect.CenterX() - sourceWidth * scale / 2f;
        var top = rect.CenterY() - sourceHeight * scale / 2f;
        float X(float x) => left + x * scale;
        float Y(float y) => top + y * scale;

        // Golden rear shell with the long sloped tab from the reference image.
        _folderPath.Reset();
        _folderPath.MoveTo(X(0f), Y(7.5f));
        _folderPath.CubicTo(X(0f), Y(5.1f), X(1.9f), Y(3.1f), X(4.3f), Y(3.1f));
        _folderPath.LineTo(X(9.7f), Y(3.1f));
        _folderPath.CubicTo(X(10.7f), Y(3.1f), X(11.4f), Y(3.4f), X(12.2f), Y(4f));
        _folderPath.LineTo(X(15.2f), Y(6.3f));
        _folderPath.CubicTo(X(16.2f), Y(7.1f), X(17.3f), Y(7.4f), X(18.7f), Y(7.4f));
        _folderPath.LineTo(X(29.5f), Y(7.4f));
        _folderPath.CubicTo(X(30.9f), Y(7.4f), X(32f), Y(8.5f), X(32f), Y(9.9f));
        _folderPath.LineTo(X(32f), Y(22.3f));
        _folderPath.CubicTo(X(32f), Y(24.3f), X(30.3f), Y(26f), X(28.3f), Y(26f));
        _folderPath.LineTo(X(3.7f), Y(26f));
        _folderPath.CubicTo(X(1.7f), Y(26f), X(0f), Y(24.3f), X(0f), Y(22.3f));
        _folderPath.Close();
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(247, 188, 15);
        canvas.DrawPath(_folderPath, _paint);

        // Peach rear insert.
        _paint.Color = Color.Rgb(255, 210, 141);
        canvas.DrawRoundRect(X(4.4f), Y(8.8f), X(28.9f), Y(21.4f),
            Math.Max(1f, 1.2f * scale), Math.Max(1f, 1.2f * scale), _paint);

        // Cream inner sheet.
        _paint.Color = Color.Rgb(255, 242, 214);
        canvas.DrawRoundRect(X(3.2f), Y(10.1f), X(27.7f), Y(21.9f),
            Math.Max(1f, 1.2f * scale), Math.Max(1f, 1.2f * scale), _paint);

        // Broad pale-yellow front cover.
        _paint.Color = Color.Rgb(255, 215, 107);
        canvas.DrawRoundRect(X(0f), Y(11.5f), X(32f), Y(26f),
            Math.Max(1f, 2.1f * scale), Math.Max(1f, 2.1f * scale), _paint);

        // Very soft top sheen preserves the light-at-the-top look of the supplied artwork.
        _paint.Color = Color.Argb(88, 255, 233, 176);
        canvas.DrawRoundRect(X(2f), Y(12.2f), X(30f), Y(13.4f),
            Math.Max(1f, 0.6f * scale), Math.Max(1f, 0.6f * scale), _paint);
    }

    private void DrawFileBadge'''
replace_regex(
    android,
    r'    private void DrawFolder\(Canvas canvas, RectF rect\)\n    \{.*?\n    \}\n\n    private void DrawFileBadge',
    new_android_folder,
    expected_min=1)

# iOS native UICollectionView: mirror the exact same geometry and fixed colors.
ios = "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs"
ios_path = ROOT / ios
ios_text = ios_path.read_text(encoding="utf-8")
ios_text = ios_text.replace(
    "/// The folder artwork uses the same Windows-11-style layered geometry as the Android native list.",
    "/// The folder artwork uses the same supplied layered-yellow geometry as the Android native list.")
ios_text = ios_text.replace("DrawWindows11FolderGlyph(artRect);", "DrawLayeredFolderGlyph(artRect);")
ios_path.write_text(ios_text, encoding="utf-8")

new_ios_folder = '''    private static void DrawLayeredFolderGlyph(CGRect rect)
    {
        const double sourceWidth = 32.0;
        const double sourceHeight = 26.0;
        var scale = Math.Min((double)rect.Width / sourceWidth, (double)rect.Height / sourceHeight);
        var left = (double)rect.GetMidX() - sourceWidth * scale / 2.0;
        var top = (double)rect.GetMidY() - sourceHeight * scale / 2.0;
        CGPoint P(double x, double y) => new(left + x * scale, top + y * scale);

        // Golden rear shell with the long sloped tab from the supplied reference image.
        UIColor.FromRGB(247, 188, 15).SetFill();
        using (var back = new UIBezierPath())
        {
            back.MoveTo(P(0, 7.5));
            back.AddCurveToPoint(P(4.3, 3.1), P(0, 5.1), P(1.9, 3.1));
            back.AddLineTo(P(9.7, 3.1));
            back.AddCurveToPoint(P(12.2, 4.0), P(10.7, 3.1), P(11.4, 3.4));
            back.AddLineTo(P(15.2, 6.3));
            back.AddCurveToPoint(P(18.7, 7.4), P(16.2, 7.1), P(17.3, 7.4));
            back.AddLineTo(P(29.5, 7.4));
            back.AddCurveToPoint(P(32.0, 9.9), P(30.9, 7.4), P(32.0, 8.5));
            back.AddLineTo(P(32.0, 22.3));
            back.AddCurveToPoint(P(28.3, 26.0), P(32.0, 24.3), P(30.3, 26.0));
            back.AddLineTo(P(3.7, 26.0));
            back.AddCurveToPoint(P(0, 22.3), P(1.7, 26.0), P(0, 24.3));
            back.ClosePath();
            back.Fill();
        }

        UIColor.FromRGB(255, 210, 141).SetFill();
        using (var rearInsert = UIBezierPath.FromRoundedRect(
            new CGRect(left + 4.4 * scale, top + 8.8 * scale, 24.5 * scale, 12.6 * scale),
            (nfloat)Math.Max(0.6, 1.2 * scale)))
            rearInsert.Fill();

        UIColor.FromRGB(255, 242, 214).SetFill();
        using (var innerSheet = UIBezierPath.FromRoundedRect(
            new CGRect(left + 3.2 * scale, top + 10.1 * scale, 24.5 * scale, 11.8 * scale),
            (nfloat)Math.Max(0.6, 1.2 * scale)))
            innerSheet.Fill();

        UIColor.FromRGB(255, 215, 107).SetFill();
        using (var front = UIBezierPath.FromRoundedRect(
            new CGRect(left, top + 11.5 * scale, 32.0 * scale, 14.5 * scale),
            (nfloat)Math.Max(0.8, 2.1 * scale)))
            front.Fill();

        UIColor.FromRGBA(255, 233, 176, 88).SetFill();
        using var sheen = UIBezierPath.FromRoundedRect(
            new CGRect(left + 2.0 * scale, top + 12.2 * scale, 28.0 * scale, 1.2 * scale),
            (nfloat)Math.Max(0.4, 0.6 * scale));
        sheen.Fill();
    }

    private static void DrawFileBadge'''
replace_regex(
    ios,
    r'    private void DrawWindows11FolderGlyph\(CGRect rect\)\n    \{.*?\n    \}\n\n    private static void DrawFileBadge',
    new_ios_folder,
    expected_min=1)

# Sanity checks: no old folder branding should remain in the three rendering paths.
assert "Windows 11 style: raised tab/back plate" not in (ROOT / xaml).read_text(encoding="utf-8")
assert "private void DrawFolder(Canvas canvas, RectF rect)" in (ROOT / android).read_text(encoding="utf-8")
assert "DrawLayeredFolderGlyph(artRect);" in (ROOT / ios).read_text(encoding="utf-8")
print("Layered folder icon patch applied successfully.")
