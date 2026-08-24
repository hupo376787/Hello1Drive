from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_regex(relative_path: str, pattern: str, replacement: str) -> None:
    path = ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"Expected exactly one match in {relative_path}, got {count}")
    path.write_text(updated, encoding="utf-8")


replace_regex(
    "src/Hello1Drive.Core/ViewModels/MainViewModel.cs",
    r"    public TransferItemModel RegisterTransfer\(string fileName, TransferDirection direction\)\n    \{.*?\n    \}\n\n    public void SetTransferResumeInfo",
    r'''    private string GetTransferDisplayName(string fileName)
    {
        var normalized = (fileName ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
            return $"{segments[^2]}/{segments[^1]}";

        var parentName = Breadcrumbs.LastOrDefault()?.Name?.Trim();
        return string.IsNullOrWhiteSpace(parentName)
            ? segments[0]
            : $"{parentName}/{segments[0]}";
    }

    public TransferItemModel RegisterTransfer(string fileName, TransferDirection direction)
    {
        var transfer = new TransferItemModel
        {
            FileName = GetTransferDisplayName(fileName),
            Direction = direction,
            State = TransferState.Waiting,
            Message = direction switch
            {
                TransferDirection.Upload => "等待上传",
                TransferDirection.Download => "等待下载",
                TransferDirection.Cache => "等待缓存",
                _ => "等待中"
            }
        };
        AttachTransfer(transfer);
        Transfers.Insert(0, transfer);
        RaiseTransferSummary();
        ScheduleTransferPersistence();
        return transfer;
    }

    public void SetTransferResumeInfo''',
)

replace_regex(
    "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs",
    r"    private void DrawFileBadge\(Canvas canvas, DriveItemModel item, RectF rect\)\n    \{.*?\n    \}\n\n    private void DrawPlayBadge",
    r'''    private void DrawFileBadge(Canvas canvas, DriveItemModel item, RectF rect)
    {
        if (item.IsImage)
        {
            DrawImagePlaceholder(canvas, rect);
            return;
        }

        if (item.IsVideo)
        {
            DrawVideoPlaceholder(canvas, rect);
            return;
        }

        if (item.IsAudio)
        {
            DrawAudioPlaceholder(canvas, rect);
            return;
        }

        DrawDocumentPlaceholder(canvas, item, rect);
    }

    private void DrawImagePlaceholder(Canvas canvas, RectF rect)
    {
        float X(float value) => rect.Left + rect.Width() * value;
        float Y(float value) => rect.Top + rect.Height() * value;
        var radius = Math.Min(rect.Width(), rect.Height()) * 0.22f;

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(238, 247, 255);
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(Dp(1), rect.Width() * 0.04f);
        _paint.Color = Color.Rgb(142, 197, 255);
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _folderPath.Reset();
        _folderPath.MoveTo(X(0.18f), Y(0.75f));
        _folderPath.LineTo(X(0.36f), Y(0.53f));
        _folderPath.LineTo(X(0.50f), Y(0.65f));
        _folderPath.LineTo(X(0.64f), Y(0.50f));
        _folderPath.LineTo(X(0.82f), Y(0.75f));
        _paint.Color = Color.Rgb(59, 130, 246);
        _paint.StrokeWidth = Math.Max(Dp(1.4f), rect.Width() * 0.055f);
        _paint.StrokeCap = Paint.Cap.Round;
        _paint.StrokeJoin = Paint.Join.Round;
        canvas.DrawPath(_folderPath, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(253, 186, 116);
        canvas.DrawCircle(X(0.31f), Y(0.31f), Math.Min(rect.Width(), rect.Height()) * 0.085f, _paint);
    }

    private void DrawVideoPlaceholder(Canvas canvas, RectF rect)
    {
        var radius = Math.Min(rect.Width(), rect.Height()) * 0.22f;
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(238, 242, 255);
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(Dp(1), rect.Width() * 0.035f);
        _paint.Color = Color.Rgb(165, 180, 252);
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(124, 58, 237);
        var playRadius = Math.Min(rect.Width(), rect.Height()) * 0.285f;
        canvas.DrawCircle(rect.CenterX(), rect.CenterY(), playRadius, _paint);

        _folderPath.Reset();
        _folderPath.MoveTo(rect.CenterX() - playRadius * 0.28f, rect.CenterY() - playRadius * 0.48f);
        _folderPath.LineTo(rect.CenterX() + playRadius * 0.55f, rect.CenterY());
        _folderPath.LineTo(rect.CenterX() - playRadius * 0.28f, rect.CenterY() + playRadius * 0.48f);
        _folderPath.Close();
        _paint.Color = Color.White;
        canvas.DrawPath(_folderPath, _paint);
    }

    private void DrawAudioPlaceholder(Canvas canvas, RectF rect)
    {
        float X(float value) => rect.Left + rect.Width() * value;
        float Y(float value) => rect.Top + rect.Height() * value;
        var min = Math.Min(rect.Width(), rect.Height());

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Rgb(96, 165, 250);
        canvas.DrawCircle(X(0.42f), Y(0.58f), min * 0.36f, _paint);
        _paint.Color = Color.Rgb(251, 113, 133);
        canvas.DrawCircle(X(0.58f), Y(0.42f), min * 0.36f, _paint);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.Color = Color.White;
        _paint.StrokeWidth = Math.Max(Dp(1.6f), min * 0.07f);
        _paint.StrokeCap = Paint.Cap.Round;
        _paint.StrokeJoin = Paint.Join.Round;
        _folderPath.Reset();
        _folderPath.MoveTo(X(0.40f), Y(0.30f));
        _folderPath.LineTo(X(0.40f), Y(0.70f));
        _folderPath.MoveTo(X(0.40f), Y(0.30f));
        _folderPath.LineTo(X(0.70f), Y(0.23f));
        _folderPath.LineTo(X(0.70f), Y(0.60f));
        canvas.DrawPath(_folderPath, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        canvas.DrawOval(new RectF(X(0.23f), Y(0.64f), X(0.46f), Y(0.82f)), _paint);
        canvas.DrawOval(new RectF(X(0.53f), Y(0.54f), X(0.76f), Y(0.72f)), _paint);
    }

    private void DrawDocumentPlaceholder(Canvas canvas, DriveItemModel item, RectF rect)
    {
        var background = item.IsPdf ? Color.Rgb(255, 245, 245)
            : item.IsWord ? Color.Rgb(239, 246, 255)
            : item.IsExcel ? Color.Rgb(240, 253, 244)
            : item.IsPowerPoint ? Color.Rgb(255, 247, 237)
            : item.IsArchive ? Color.Rgb(245, 243, 255)
            : item.IsUrlShortcut ? Color.Rgb(240, 253, 250)
            : Color.Rgb(248, 250, 252);
        var border = item.IsPdf ? Color.Rgb(240, 160, 168)
            : item.IsWord ? Color.Rgb(147, 197, 253)
            : item.IsExcel ? Color.Rgb(134, 239, 172)
            : item.IsPowerPoint ? Color.Rgb(253, 186, 116)
            : item.IsArchive ? Color.Rgb(196, 181, 253)
            : item.IsUrlShortcut ? Color.Rgb(94, 234, 212)
            : Color.Rgb(203, 213, 225);
        var accent = item.IsPdf ? Color.Rgb(239, 68, 68)
            : item.IsWord ? Color.Rgb(37, 99, 235)
            : item.IsExcel ? Color.Rgb(22, 163, 74)
            : item.IsPowerPoint ? Color.Rgb(249, 115, 22)
            : item.IsArchive ? Color.Rgb(139, 92, 246)
            : item.IsUrlShortcut ? Color.Rgb(14, 165, 164)
            : item.IsText ? Color.Rgb(100, 116, 139)
            : Color.Rgb(96, 165, 250);

        var radius = Math.Min(rect.Width(), rect.Height()) * 0.18f;
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = background;
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(Dp(1), rect.Width() * 0.035f);
        _paint.Color = border;
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = accent;
        var stripTop = rect.Bottom - rect.Height() * 0.30f;
        var strip = new RectF(rect.Left, stripTop, rect.Right, rect.Bottom);
        canvas.DrawRoundRect(strip, radius * 0.65f, radius * 0.65f, _paint);
        canvas.DrawRect(rect.Left, stripTop, rect.Right, stripTop + radius * 0.65f, _paint);

        _textPaint.Color = Color.White;
        _textPaint.TextAlign = Paint.Align.Center;
        _textPaint.TextSize = Math.Min(rect.Height() * 0.16f, Sp(8));
        _textPaint.SetTypeface(Typeface.DefaultBold);
        var labelY = stripTop + (rect.Bottom - stripTop) / 2f - (_textPaint.Ascent() + _textPaint.Descent()) / 2f;
        canvas.DrawText(item.FileBadgeText, rect.CenterX(), labelY, _textPaint);
        _textPaint.TextAlign = Paint.Align.Left;
    }

    private void DrawPlayBadge''',
)

replace_regex(
    "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs",
    r"    private static void DrawFileBadge\(CGRect rect, DriveItemModel item\)\n    \{.*?\n    \}\n\n    private static void DrawPlayBadge",
    r'''    private static void DrawFileBadge(CGRect rect, DriveItemModel item)
    {
        if (item.IsImage)
        {
            DrawImagePlaceholder(rect);
            return;
        }

        if (item.IsVideo)
        {
            DrawVideoPlaceholder(rect);
            return;
        }

        if (item.IsAudio)
        {
            DrawAudioPlaceholder(rect);
            return;
        }

        DrawDocumentPlaceholder(rect, item);
    }

    private static void DrawImagePlaceholder(CGRect rect)
    {
        double X(double value) => (double)rect.Left + (double)rect.Width * value;
        double Y(double value) => (double)rect.Top + (double)rect.Height * value;
        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.22;

        UIColor.FromRGB(238, 247, 255).SetFill();
        using (var background = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
            background.Fill();
        UIColor.FromRGB(142, 197, 255).SetStroke();
        using (var border = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
        {
            border.LineWidth = (nfloat)Math.Max(1, (double)rect.Width * 0.04);
            border.Stroke();
        }

        UIColor.FromRGB(59, 130, 246).SetStroke();
        using (var mountain = new UIBezierPath
        {
            LineWidth = (nfloat)Math.Max(1.4, (double)rect.Width * 0.055),
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        })
        {
            mountain.MoveTo(new CGPoint(X(0.18), Y(0.75)));
            mountain.AddLineTo(new CGPoint(X(0.36), Y(0.53)));
            mountain.AddLineTo(new CGPoint(X(0.50), Y(0.65)));
            mountain.AddLineTo(new CGPoint(X(0.64), Y(0.50)));
            mountain.AddLineTo(new CGPoint(X(0.82), Y(0.75)));
            mountain.Stroke();
        }

        UIColor.FromRGB(253, 186, 116).SetFill();
        var sunRadius = Math.Min((double)rect.Width, (double)rect.Height) * 0.085;
        using var sun = UIBezierPath.FromOval(new CGRect(X(0.31) - sunRadius, Y(0.31) - sunRadius, sunRadius * 2, sunRadius * 2));
        sun.Fill();
    }

    private static void DrawVideoPlaceholder(CGRect rect)
    {
        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.22;
        UIColor.FromRGB(238, 242, 255).SetFill();
        using (var background = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
            background.Fill();
        UIColor.FromRGB(165, 180, 252).SetStroke();
        using (var border = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
        {
            border.LineWidth = (nfloat)Math.Max(1, (double)rect.Width * 0.035);
            border.Stroke();
        }

        var playRadius = Math.Min((double)rect.Width, (double)rect.Height) * 0.285;
        var center = new CGPoint(rect.GetMidX(), rect.GetMidY());
        UIColor.FromRGB(124, 58, 237).SetFill();
        using (var circle = UIBezierPath.FromOval(new CGRect(center.X - playRadius, center.Y - playRadius, playRadius * 2, playRadius * 2)))
            circle.Fill();

        UIColor.White.SetFill();
        using var triangle = new UIBezierPath();
        triangle.MoveTo(new CGPoint(center.X - playRadius * 0.28, center.Y - playRadius * 0.48));
        triangle.AddLineTo(new CGPoint(center.X + playRadius * 0.55, center.Y));
        triangle.AddLineTo(new CGPoint(center.X - playRadius * 0.28, center.Y + playRadius * 0.48));
        triangle.ClosePath();
        triangle.Fill();
    }

    private static void DrawAudioPlaceholder(CGRect rect)
    {
        double X(double value) => (double)rect.Left + (double)rect.Width * value;
        double Y(double value) => (double)rect.Top + (double)rect.Height * value;
        var min = Math.Min((double)rect.Width, (double)rect.Height);

        UIColor.FromRGB(96, 165, 250).SetFill();
        using (var leftCircle = UIBezierPath.FromOval(new CGRect(X(0.42) - min * 0.36, Y(0.58) - min * 0.36, min * 0.72, min * 0.72)))
            leftCircle.Fill();
        UIColor.FromRGB(251, 113, 133).SetFill();
        using (var rightCircle = UIBezierPath.FromOval(new CGRect(X(0.58) - min * 0.36, Y(0.42) - min * 0.36, min * 0.72, min * 0.72)))
            rightCircle.Fill();

        UIColor.White.SetStroke();
        using (var note = new UIBezierPath
        {
            LineWidth = (nfloat)Math.Max(1.6, min * 0.07),
            LineCapStyle = CGLineCap.Round,
            LineJoinStyle = CGLineJoin.Round
        })
        {
            note.MoveTo(new CGPoint(X(0.40), Y(0.30)));
            note.AddLineTo(new CGPoint(X(0.40), Y(0.70)));
            note.MoveTo(new CGPoint(X(0.40), Y(0.30)));
            note.AddLineTo(new CGPoint(X(0.70), Y(0.23)));
            note.AddLineTo(new CGPoint(X(0.70), Y(0.60)));
            note.Stroke();
        }

        UIColor.White.SetFill();
        using (var leftHead = UIBezierPath.FromOval(new CGRect(X(0.23), Y(0.64), X(0.46) - X(0.23), Y(0.82) - Y(0.64))))
            leftHead.Fill();
        using var rightHead = UIBezierPath.FromOval(new CGRect(X(0.53), Y(0.54), X(0.76) - X(0.53), Y(0.72) - Y(0.54)));
        rightHead.Fill();
    }

    private static void DrawDocumentPlaceholder(CGRect rect, DriveItemModel item)
    {
        var background = item.IsPdf ? UIColor.FromRGB(255, 245, 245)
            : item.IsWord ? UIColor.FromRGB(239, 246, 255)
            : item.IsExcel ? UIColor.FromRGB(240, 253, 244)
            : item.IsPowerPoint ? UIColor.FromRGB(255, 247, 237)
            : item.IsArchive ? UIColor.FromRGB(245, 243, 255)
            : item.IsUrlShortcut ? UIColor.FromRGB(240, 253, 250)
            : UIColor.FromRGB(248, 250, 252);
        var borderColor = item.IsPdf ? UIColor.FromRGB(240, 160, 168)
            : item.IsWord ? UIColor.FromRGB(147, 197, 253)
            : item.IsExcel ? UIColor.FromRGB(134, 239, 172)
            : item.IsPowerPoint ? UIColor.FromRGB(253, 186, 116)
            : item.IsArchive ? UIColor.FromRGB(196, 181, 253)
            : item.IsUrlShortcut ? UIColor.FromRGB(94, 234, 212)
            : UIColor.FromRGB(203, 213, 225);
        var accent = item.IsPdf ? UIColor.FromRGB(239, 68, 68)
            : item.IsWord ? UIColor.FromRGB(37, 99, 235)
            : item.IsExcel ? UIColor.FromRGB(22, 163, 74)
            : item.IsPowerPoint ? UIColor.FromRGB(249, 115, 22)
            : item.IsArchive ? UIColor.FromRGB(139, 92, 246)
            : item.IsUrlShortcut ? UIColor.FromRGB(14, 165, 164)
            : item.IsText ? UIColor.FromRGB(100, 116, 139)
            : UIColor.FromRGB(96, 165, 250);

        var radius = Math.Min((double)rect.Width, (double)rect.Height) * 0.18;
        background.SetFill();
        using (var card = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
            card.Fill();
        borderColor.SetStroke();
        using (var border = UIBezierPath.FromRoundedRect(rect, (nfloat)radius))
        {
            border.LineWidth = (nfloat)Math.Max(1, (double)rect.Width * 0.035);
            border.Stroke();
        }

        var stripHeight = (double)rect.Height * 0.30;
        var stripRect = new CGRect(rect.Left, rect.Bottom - stripHeight, rect.Width, stripHeight);
        accent.SetFill();
        using (var strip = UIBezierPath.FromRoundedRect(stripRect, (nfloat)(radius * 0.65)))
            strip.Fill();
        new UIBezierPath(new CGRect(rect.Left, stripRect.Top, rect.Width, (nfloat)(radius * 0.65))).Fill();

        using var text = new NSString(item.FileBadgeText);
        var font = UIFont.BoldSystemFontOfSize((nfloat)Math.Min(8, Math.Max(5, (double)rect.Height * 0.16)));
        var attrs = new UIStringAttributes
        {
            ForegroundColor = UIColor.White,
            Font = font
        };
        var size = text.GetSizeUsingAttributes(attrs);
        text.DrawString(new CGPoint(rect.GetMidX() - size.Width / 2, stripRect.GetMidY() - size.Height / 2), attrs);
    }

    private static void DrawPlayBadge''',
)

# This is a one-shot maintenance patch. Remove the helper and workflow from the resulting source commit.
for relative in [
    "tools/apply_transfer_path_and_file_art_fix.py",
    ".github/workflows/apply-transfer-path-and-file-art-fix.yml",
]:
    path = ROOT / relative
    if path.exists():
        path.unlink()
