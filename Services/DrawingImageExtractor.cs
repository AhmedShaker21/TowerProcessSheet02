using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;
using UglyToad.PdfPig;

namespace TowerProcessSheet.Services;

/// <summary>
/// Renders a drawing PDF for an interactive crop. The UI gets a full-page preview
/// plus a suggested crop box (the diagram area, found from the title-block/stamp
/// text positions). The user can resize it; on save the chosen region (normalized
/// 0..1, top-left origin) is rendered at high resolution and cropped.
/// </summary>
public static class DrawingImageExtractor
{
    private static readonly HashSet<string> BandMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ITEM", "LEGEND", "HOLE", "CLIENT", "PROJECT", "CONTRACTOR", "SCALE", "DRAWN",
        "APPR", "EGYPTIAN", "ELECTRIC", "ENGINEERING", "SECTOR", "EPS", "CIVIL",
        "DESCRIPTION", "SECTION", "LENGTH", "MATERIAL", "DRAWING", "TITLE", "RELEASE", "NOTES"
    };

    private const double CropScale = 3.0;   // ~216 DPI for the final embedded image
    private const double SideMargin = 12.0; // points, for the suggested box
    private const double TopMargin = 20.0;
    private const double Gap = 8.0;

    public sealed record NormRect(double X, double Y, double W, double H);
    public sealed record PreviewResult(byte[] Png, int Width, int Height, NormRect Suggested);

    /// <summary>Full-page preview PNG + a suggested crop (normalized, top-left origin).
    /// Throws on render failure so the caller can report why.</summary>
    public static PreviewResult RenderPreview(byte[] pdfBytes, int maxWidth = 1600)
    {
        var suggested = GetSuggestedNormalized(pdfBytes, out double pageW, out double pageH)
                        ?? new NormRect(0.05, 0.06, 0.90, 0.45);

        double scale = (double)maxWidth / pageW;

        var lib = DocLib.Instance; // singleton - never dispose
        using var docReader = lib.GetDocReader(pdfBytes, new PageDimensions(scale));
        using var pageReader = docReader.GetPageReader(0);

        var raw = pageReader.GetImage();
        int pw = pageReader.GetPageWidth();
        int ph = pageReader.GetPageHeight();

        var png = BgraToPng(raw, pw, ph, null)
                  ?? throw new InvalidOperationException("Failed to encode preview image.");
        return new PreviewResult(png, pw, ph, suggested);
    }

    /// <summary>Renders at high resolution and crops to a normalized rectangle.</summary>
    public static byte[]? CropNormalized(byte[] pdfBytes, NormRect r)
    {
        try
        {
            double nx = Clamp01(r.X), ny = Clamp01(r.Y);
            double nw = Clamp01(r.W), nh = Clamp01(r.H);
            if (nw <= 0.005 || nh <= 0.005) return null;

            var lib = DocLib.Instance;
            using var docReader = lib.GetDocReader(pdfBytes, new PageDimensions(CropScale));
            using var pageReader = docReader.GetPageReader(0);

            var raw = pageReader.GetImage();
            int pw = pageReader.GetPageWidth();
            int ph = pageReader.GetPageHeight();

            int x = (int)Math.Floor(nx * pw);
            int y = (int)Math.Floor(ny * ph);
            int w = (int)Math.Round(nw * pw);
            int h = (int)Math.Round(nh * ph);

            x = Math.Clamp(x, 0, pw - 1);
            y = Math.Clamp(y, 0, ph - 1);
            w = Math.Clamp(w, 1, pw - x);
            h = Math.Clamp(h, 1, ph - y);

            return BgraToPng(raw, pw, ph, SKRectI.Create(x, y, w, h));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Renders the page ONCE at high resolution and crops several normalized rectangles from it
    /// (null for a rectangle that is too small or can't be cut) — used by the OCR fallback.</summary>
    public static List<byte[]?> CropManyNormalized(byte[] pdfBytes, IReadOnlyList<NormRect> rects)
    {
        var result = new List<byte[]?>();
        try
        {
            var lib = DocLib.Instance;
            using var docReader = lib.GetDocReader(pdfBytes, new PageDimensions(CropScale));
            using var pageReader = docReader.GetPageReader(0);

            var raw = pageReader.GetImage();
            int pw = pageReader.GetPageWidth();
            int ph = pageReader.GetPageHeight();

            foreach (var r in rects)
            {
                double nw = Clamp01(r.W), nh = Clamp01(r.H);
                if (nw <= 0.005 || nh <= 0.005) { result.Add(null); continue; }
                int x = Math.Clamp((int)Math.Floor(Clamp01(r.X) * pw), 0, pw - 1);
                int y = Math.Clamp((int)Math.Floor(Clamp01(r.Y) * ph), 0, ph - 1);
                int w = Math.Clamp((int)Math.Round(nw * pw), 1, pw - x);
                int h = Math.Clamp((int)Math.Round(nh * ph), 1, ph - y);
                result.Add(BgraToPng(raw, pw, ph, SKRectI.Create(x, y, w, h)));
            }
        }
        catch
        {
            // fall through: whatever could not be cut stays null
        }
        while (result.Count < rects.Count) result.Add(null);
        return result;
    }

    /// <summary>Auto crop using the suggested box (used when the user didn't adjust it).</summary>
    public static byte[]? CropDiagram(byte[] pdfBytes)
    {
        var s = GetSuggestedNormalized(pdfBytes, out _, out _);
        return s == null ? null : CropNormalized(pdfBytes, s);
    }

    // ---------- internals ----------

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    private static NormRect? GetSuggestedNormalized(byte[] pdfBytes, out double pageW, out double pageH)
    {
        pageW = pageH = 0;
        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);
        pageW = page.Width;
        pageH = page.Height;

        var words = page.GetWords().ToList();
        if (words.Count == 0) return null;

        var markerTops = words
            .Where(w => BandMarkers.Contains(w.Text.Trim('.', ':', ',')))
            .Select(w => w.BoundingBox.Top)
            .ToList();

        double boundary = markerTops.Count > 0 ? markerTops.Max() : pageH * 0.33;

        var drawing = words.Where(w => w.BoundingBox.Bottom >= boundary + Gap).ToList();
        if (drawing.Count == 0) return null;

        double left = Math.Clamp(drawing.Min(w => w.BoundingBox.Left) - SideMargin, 0, pageW);
        double right = Math.Clamp(drawing.Max(w => w.BoundingBox.Right) + SideMargin, 0, pageW);
        double top = Math.Clamp(drawing.Max(w => w.BoundingBox.Top) + TopMargin, 0, pageH);      // Y-up
        double bottom = Math.Clamp(Math.Max(boundary + Gap, drawing.Min(w => w.BoundingBox.Bottom) - SideMargin), 0, pageH);

        if (right - left < 5 || top - bottom < 5) return null;

        // PDF (Y-up) -> normalized, top-left origin
        double nx = left / pageW;
        double nw = (right - left) / pageW;
        double ny = (pageH - top) / pageH;
        double nh = (top - bottom) / pageH;

        return new NormRect(nx, ny, nw, nh);
    }

    /// <summary>Flatten BGRA pixels onto white and encode PNG, optionally cropping first.</summary>
    private static byte[]? BgraToPng(byte[] raw, int pw, int ph, SKRectI? crop)
    {
        var info = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
        var gch = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try
        {
            using var full = SKImage.FromPixels(info, gch.AddrOfPinnedObject(), info.RowBytes);

            SKImage src = full;
            SKImage? sub = null;
            if (crop.HasValue)
            {
                sub = full.Subset(crop.Value);
                if (sub == null) return null;
                src = sub;
            }

            int w = src.Width, h = src.Height;
            using (var surface = SKSurface.Create(new SKImageInfo(w, h)))
            {
                surface.Canvas.Clear(SKColors.White);
                surface.Canvas.DrawImage(src, 0, 0);
                using var snapshot = surface.Snapshot();
                using var data = snapshot.Encode(SKEncodedImageFormat.Png, 92);
                sub?.Dispose();
                return data.ToArray();
            }
        }
        finally
        {
            gch.Free();
        }
    }
}