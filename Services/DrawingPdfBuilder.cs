using System.IO.Compression;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace TowerProcessSheet.Services;

/// <summary>
/// Every drawing is stored as an image (the stamped result, or the plain drawing if
/// nothing has stamped it). This turns those images back into real PDF files for
/// download — either one combined multi-page PDF, or a zip of separate single-page PDFs,
/// so a workshop/quality/planning account can grab everything they've received at once.
/// </summary>
public static class DrawingPdfBuilder
{
    /// <summary>One drawing to include: a display name (used for the file name inside a
    /// zip) and its image bytes (PNG).</summary>
    public record Item(string Name, byte[] ImageBytes);

    public static byte[] BuildSinglePdf(List<Item> items)
    {
        using var doc = new PdfDocument();
        foreach (var item in items)
        {
            AddImagePage(doc, item.ImageBytes);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    public static byte[] BuildZipOfPdfs(List<Item> items)
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                using var doc = new PdfDocument();
                AddImagePage(doc, item.ImageBytes);
                using var pdfMs = new MemoryStream();
                doc.Save(pdfMs, false);

                string baseName = SanitizeFileName(item.Name);
                string fileName = baseName + ".pdf";
                int n = 2;
                while (!usedNames.Add(fileName)) { fileName = $"{baseName} ({n}).pdf"; n++; }

                var entry = zip.CreateEntry(fileName, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                pdfMs.Position = 0;
                pdfMs.CopyTo(entryStream);
            }
        }
        return zipStream.ToArray();
    }

    private static void AddImagePage(PdfDocument doc, byte[] pngBytes)
    {
        // PdfSharp's image loader calls stream.GetBuffer() internally — that throws
        // UnauthorizedAccessException unless the MemoryStream was constructed with its
        // buffer explicitly exposed. The plain `new MemoryStream(byte[])` constructor
        // does NOT do this; it must be built this way instead.
        using var pngStream = new MemoryStream(pngBytes, 0, pngBytes.Length, writable: false, publiclyVisible: true);
        using var xImage = XImage.FromStream(pngStream);
        var page = doc.AddPage();
        // Fit the page to the image's own proportions, at a normal print resolution,
        // so nothing gets stretched or cropped. Programmatically-rendered PNGs often
        // carry no DPI metadata at all, so fall back to a sane 96 DPI rather than
        // dividing by zero.
        double hRes = xImage.HorizontalResolution > 0 ? xImage.HorizontalResolution : 96;
        double vRes = xImage.VerticalResolution > 0 ? xImage.VerticalResolution : 96;
        double widthPt = xImage.PixelWidth * 72.0 / hRes;
        double heightPt = xImage.PixelHeight * 72.0 / vRes;
        if (widthPt <= 0 || heightPt <= 0) { widthPt = 842; heightPt = 595; }
        page.Width = XUnit.FromPoint(widthPt);
        page.Height = XUnit.FromPoint(heightPt);

        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(xImage, 0, 0, widthPt, heightPt);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "drawing" : cleaned;
    }
}
