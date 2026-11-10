using SkiaSharp;

namespace TowerProcessSheet.Services;

/// <summary>
/// Draws the "Document Control" stamp server-side, for bulk sends where nobody is
/// interactively placing anything — e.g. Research splitting a pile of drawings and
/// sending each group off with the destination's own stamp code already applied.
/// Visual match: red border, red title + sector, blue date/issue, red "Copy No." label
/// with the destination's code in large blue.
///
/// The whole layout (canvas size, spacing, every position) scales together with
/// fontScale, not just the individual text draw calls — this is what makes "bigger/
/// smaller text" a real, visible difference instead of lines merging into each other
/// at the high end or being imperceptible at the low end.
/// </summary>
public static class DocControlStampRenderer
{
    public static byte[] Render(string sector, string date, string issue, string copyNo, double fontScale = 1.0)
    {
        float fs = (float)Math.Clamp(fontScale, 0.4, 2.5);
        var red = new SKColor(0xE1, 0x11, 0x11);
        var blue = new SKColor(0x17, 0x46, 0xC7);

        // Every measurement below is a multiple of fs, so growing the font also grows the
        // spacing between lines and the canvas itself — nothing overlaps, and the effect
        // is unmistakable once the stamp is placed on a drawing.
        int w = 620;
        float pad = 22 * fs;
        float titleSize = 40 * fs, sectorSize = 32 * fs, labelSize = 26 * fs, codeSize = 42 * fs;
        float lineGap = 46 * fs;
        float yTitle = pad + titleSize * 0.75f;
        float ySector = yTitle + lineGap;
        float yDate = ySector + lineGap * 1.15f;
        float yCopy = yDate + lineGap * 1.15f;
        int h = (int)(yCopy + labelSize * 0.6f + pad);

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using (var border = new SKPaint { Color = red, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(4, 8 * fs) })
        using (var backing = new SKPaint { Color = new SKColor(255, 255, 255, 60), IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            var rect = new SKRect(6, 6, w - 6, h - 6);
            canvas.DrawRect(rect, backing);
            canvas.DrawRect(rect, border);
        }

        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var titlePaint = new SKPaint { Color = red, IsAntialias = true, Typeface = typeface, TextSize = titleSize, TextAlign = SKTextAlign.Center };
        using var sectorPaint = new SKPaint { Color = red, IsAntialias = true, Typeface = typeface, TextSize = sectorSize, TextAlign = SKTextAlign.Center };
        using var redLabel = new SKPaint { Color = red, IsAntialias = true, Typeface = typeface, TextSize = labelSize, TextAlign = SKTextAlign.Left };
        using var blueLabel = new SKPaint { Color = blue, IsAntialias = true, Typeface = typeface, TextSize = labelSize, TextAlign = SKTextAlign.Left };
        using var blueCode = new SKPaint { Color = blue, IsAntialias = true, Typeface = typeface, TextSize = codeSize, TextAlign = SKTextAlign.Left };

        float cx = w / 2f;
        canvas.DrawText("Document Control", cx, yTitle, titlePaint);
        canvas.DrawText(sector, cx, ySector, sectorPaint);

        // "Date : <blue date>      Issue: <blue issue>" on one line, centered as a group.
        string dateLabel = "Date : ", dateVal = date + "   ", issueLabel = "Issue: ";
        float lineW = redLabel.MeasureText(dateLabel) + blueLabel.MeasureText(dateVal)
                    + redLabel.MeasureText(issueLabel) + blueLabel.MeasureText(issue);
        float x = cx - lineW / 2;
        x = DrawLeft(canvas, dateLabel, x, yDate, redLabel);
        x = DrawLeft(canvas, dateVal, x, yDate, blueLabel);
        x = DrawLeft(canvas, issueLabel, x, yDate, redLabel);
        DrawLeft(canvas, issue, x, yDate, blueLabel);

        // "Copy No. : <big blue code>"
        string copyLabel = "Copy No. : ";
        float copyLabelW = redLabel.MeasureText(copyLabel);
        float copyValW = blueCode.MeasureText(copyNo);
        float x2 = cx - (copyLabelW + copyValW) / 2;
        x2 = DrawLeft(canvas, copyLabel, x2, yCopy, redLabel);
        DrawLeft(canvas, copyNo, x2, yCopy + 6 * fs, blueCode);

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static float DrawLeft(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        canvas.DrawText(text, x, y, paint);
        return x + paint.MeasureText(text);
    }

    /// <summary>Composites the stamp image onto the drawing image at a normalized position
    /// (0..1, from whichever edge is anchored), sized to a chosen width as a percentage of
    /// the drawing's own width — height follows the stamp's own natural proportions
    /// automatically, so a bigger font (which makes the stamp naturally taller) is never
    /// squashed back down by an independently-chosen height. Returns a new PNG with the
    /// stamp baked in.</summary>
    public static byte[] Composite(byte[] drawingPng, byte[] stampPng,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct)
    {
        using var drawing = SKBitmap.Decode(drawingPng);
        using var stamp = SKBitmap.Decode(stampPng);
        if (drawing == null || stamp == null) return drawingPng;

        float stampW = (float)(drawing.Width * widthPct / 100.0);
        float stampH = stampW * stamp.Height / stamp.Width;
        float x = anchorRight ? drawing.Width - (float)(drawing.Width * normX) - stampW : (float)(drawing.Width * normX);
        float y = anchorBottom ? drawing.Height - (float)(drawing.Height * normY) - stampH : (float)(drawing.Height * normY);

        using var surface = SKSurface.Create(new SKImageInfo(drawing.Width, drawing.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(drawing, 0, 0);
        canvas.DrawBitmap(stamp, new SKRect(x, y, x + stampW, y + stampH));

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }
}
