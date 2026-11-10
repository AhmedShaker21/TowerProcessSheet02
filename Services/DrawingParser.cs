using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace TowerProcessSheet.Services;

public class DrawingData
{
    public string? Item { get; set; }
    public string? Qty { get; set; }
    public string? Section { get; set; }
    public string? Length { get; set; }
    public string? Material { get; set; }        // grade as printed, e.g. "S235JR"
    public string? MaterialOption { get; set; }  // "S235JR / ST.37" (matches the dropdown)
    public string? Description { get; set; }
    public string? ProjectName { get; set; }     // "S2  220 kv"
    public bool Found { get; set; }
}

/// <summary>
/// Reads the title-block table (ITEM / QTY / SECTION / LENGTH / MATERIAL / DESCRIPTION)
/// from an engineering-drawing PDF. The section token (e.g. L45*5) also appears in the
/// part label, so the correct row is the one that ALSO contains a material token (S###).
/// </summary>
public static class DrawingParser
{
    private static readonly Regex SectionRx = new(@"^L\d+(?:\*\d+){1,2}$", RegexOptions.IgnoreCase);
    private static readonly Regex MaterialRx = new(@"^S\d{3}[A-Z0-9]*$", RegexOptions.IgnoreCase);

    public static DrawingData Parse(Stream pdfStream)
    {
        var result = new DrawingData();
        var fullText = new StringBuilder();

        using var doc = PdfDocument.Open(pdfStream);

        foreach (var page in doc.GetPages())
        {
            var words = PdfWords.Get(page);
            fullText.Append(' ').Append(string.Join(" ", words.Select(w => w.Text)));

            if (result.Found) continue;

            foreach (var sec in words.Where(w => SectionRx.IsMatch(w.Text)))
            {
                double cy = sec.BoundingBox.Centroid.Y;
                double tol = Math.Max(3.0, sec.BoundingBox.Height * 0.7);

                var row = words
                    .Where(w => Math.Abs(w.BoundingBox.Centroid.Y - cy) <= tol)
                    .OrderBy(w => w.BoundingBox.Left)
                    .ToList();

                int secIdx = row.FindIndex(w => ReferenceEquals(w, sec));
                int matIdx = row.FindIndex(w => MaterialRx.IsMatch(w.Text));
                if (secIdx < 0 || matIdx < 0 || matIdx <= secIdx) continue; // need MATERIAL after SECTION

                result.Section = row[secIdx].Text;
                if (secIdx - 1 >= 0) result.Qty = OnlyDigits(row[secIdx - 1].Text);
                if (secIdx - 2 >= 0) result.Item = row[secIdx - 2].Text;
                if (secIdx + 1 < matIdx) result.Length = OnlyDigits(row[secIdx + 1].Text);
                result.Material = row[matIdx].Text;
                if (matIdx + 1 < row.Count)
                    result.Description = string.Join(" ", row.Skip(matIdx + 1).Select(w => w.Text)).Trim();

                result.Found = true;
                break;
            }
        }

        var text = fullText.ToString();
        result.ProjectName = BuildProject(text);
        result.MaterialOption = ToMaterialOption(result.Material);
        return result;
    }

    private static string OnlyDigits(string s) => new(s.Where(char.IsDigit).ToArray());

    private static string? BuildProject(string text)
    {
        var tower = Regex.Match(text, @"TOWER\s*TYPE\s*\(?\s*([A-Z0-9]+)\s*\)?", RegexOptions.IgnoreCase);
        var kv = Regex.Match(text, @"(\d{2,3})\s*KV", RegexOptions.IgnoreCase);
        var t = tower.Success ? tower.Groups[1].Value.ToUpperInvariant() : null;
        var k = kv.Success ? kv.Groups[1].Value : null;

        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(k)) return $"{t}  {k} kv";
        if (!string.IsNullOrEmpty(k)) return $"{k} kv";
        return null;
    }

    /// <summary>Pairs the grade with its ST designation (S355/S420/S460 => ST.52, else ST.37).</summary>
    public static string? ToMaterialOption(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade)) return null;
        var g = grade.Trim().ToUpperInvariant();
        var st = (g.StartsWith("S355") || g.StartsWith("S420") || g.StartsWith("S460")) ? "ST.52" : "ST.37";
        return $"{g} / {st}";
    }
}