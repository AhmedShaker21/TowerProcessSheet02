using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace TowerProcessSheet.Services;

/// <summary>Splits one multi-page PDF into a set of single-page PDFs, so the rest of the
/// pipeline can treat "one PDF with 500 pages" exactly like "500 separate PDFs".</summary>
public static class PdfSplitter
{
    public static int PageCount(byte[] pdfBytes)
    {
        using var ms = new MemoryStream(pdfBytes);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }

    /// <summary>Returns each page as its own single-page PDF, in page order.</summary>
    public static List<byte[]> SplitToBytes(byte[] pdfBytes)
    {
        var result = new List<byte[]>();
        using var msIn = new MemoryStream(pdfBytes);
        using var src = PdfReader.Open(msIn, PdfDocumentOpenMode.Import);

        for (int i = 0; i < src.PageCount; i++)
        {
            using var outDoc = new PdfDocument();
            outDoc.AddPage(src.Pages[i]);
            using var msOut = new MemoryStream();
            outDoc.Save(msOut, false);
            result.Add(msOut.ToArray());
        }
        return result;
    }
}
