using UglyToad.PdfPig;

namespace TowerProcessSheet.Services;

/// <summary>
/// Reads the field values that fall inside user-calibrated rectangles on a drawing PDF.
/// Rectangles are normalized (0..1, top-left origin) — the same convention used by
/// DrawingImageExtractor's crop preview, so a rectangle drawn on the on-screen page
/// image maps directly onto the underlying PDF text positions.
/// </summary>
public static class DrawingRegionExtractor
{
    public sealed record NormRect(double X, double Y, double W, double H);

    /// <summary>For each named field, returns the text found inside its rectangle (reading order).</summary>
    public static Dictionary<string, string> ExtractFields(byte[] pdfBytes, Dictionary<string, NormRect> fields)
        => ExtractFields(pdfBytes, fields, out _);

    /// <summary>
    /// Same, and any field whose box holds no text at all (the title block is a picture, or the page is a scan)
    /// is read from the page image by OCR instead. <paramref name="ocrFields"/> lists the fields read that way,
    /// so the person can double-check them.
    /// </summary>
    public static Dictionary<string, string> ExtractFields(byte[] pdfBytes, Dictionary<string, NormRect> fields, out HashSet<string> ocrFields)
    {
        ocrFields = new HashSet<string>();
        var result = new Dictionary<string, string>();
        using var doc = PdfDocument.Open(pdfBytes);
        if (doc.NumberOfPages == 0) return result;
        var page = doc.GetPage(1);
        double pageW = page.Width, pageH = page.Height;
        var words = PdfWords.Get(page);   // stacked old/new text collapsed to the visible one

        foreach (var (name, r) in fields)
        {
            double left = r.X * pageW;
            double right = (r.X + r.W) * pageW;
            double top = pageH * (1 - r.Y);              // normalized top -> PDF Y-up
            double bottom = pageH * (1 - r.Y - r.H);

            var hit = words.Where(w =>
            {
                double cx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                double cy = (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2;
                return cx >= left && cx <= right && cy >= bottom && cy <= top;
            })
            // reading order: top row first, left to right
            .OrderByDescending(w => Math.Round(w.BoundingBox.Top / 3))
            .ThenBy(w => w.BoundingBox.Left)
            .Select(w => w.Text)
            .ToList();

            result[name] = string.Join(" ", hit).Trim();
        }

        // OCR only for the boxes that came back empty.
        var empty = result.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList();
        if (empty.Count > 0)
        {
            try
            {
                var read = DrawingOcr.ReadRegions(pdfBytes, empty.ToDictionary(k => k, k => fields[k]));
                foreach (var (name, text) in read)
                {
                    result[name] = text;
                    ocrFields.Add(name);
                }
            }
            catch { /* OCR is a bonus — never let it break the normal reading */ }
        }
        return result;
    }

    /// <summary>
    /// True if the page has any text at all anywhere on it. A scanned/photographed
    /// drawing with no text layer — as opposed to one where the marked box simply
    /// isn't over the right spot — will always come back false here, regardless of
    /// where any rectangle is drawn, since there's nothing for PdfPig to find at all.
    /// </summary>
    public static bool HasAnyText(byte[] pdfBytes)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            if (doc.NumberOfPages == 0) return false;
            return doc.GetPage(1).GetWords().Any();
        }
        catch { return false; }
    }
}