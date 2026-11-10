using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace TowerProcessSheet.Services;

/// <summary>One line of a Tekla "PART LIST": ITEM / QTY / SECTION / WIDTH / LENGTH / POS / WEIGHT / ST. GR.</summary>
public class PartListRow
{
    public string Pos { get; set; } = "";       // the part's own mark, e.g. "10H" — what a sheet's Part No is matched against
    public string Qty { get; set; } = "";
    public string Section { get; set; } = "";
    public string Width { get; set; } = "";
    public string Length { get; set; } = "";
    public string Weight { get; set; } = "";
    public string Grade { get; set; } = "";
    public string Remark { get; set; } = "";    // only kept when it's plain (non-Arabic) text

    /// <summary>The whole line as read from the PDF — what the search box looks through.</summary>
    public string Raw { get; set; } = "";
    /// <summary>True when the line didn't follow the Tekla column layout and is shown as plain text instead.</summary>
    public bool Loose { get; set; }
    /// <summary>The normalized values a sheet's Part No is matched against (just the POS for a Tekla row).</summary>
    public HashSet<string> Keys { get; set; } = new();
    /// <summary>Stable id of this row, so a hand-made tick survives re-uploading the same list.</summary>
    public string RowKey { get; set; } = "";
    /// <summary>True for a row read from the standard template (Mark No. / Profie / Weight / Qty/LEG / Length / Description).
    /// Mark No. is kept in Pos, Profie in Section, Description in Remark.</summary>
    public bool Template { get; set; }
}

/// <summary>Outcome of checking a Part List PDF against the standard template.</summary>
public class PartListTemplateResult
{
    public bool Ok { get; set; }
    /// <summary>The header line (all six columns, in order) was found.</summary>
    public bool HeaderFound { get; set; }
    /// <summary>Template columns that could not be found near the top of the file (when no header was found).</summary>
    public List<string> MissingColumns { get; set; } = new();
    public int RowCount { get; set; }
    public int BadLines { get; set; }
    public string? FirstBadLine { get; set; }
    public List<PartListRow> Rows { get; set; } = new();
}

/// <summary>
/// Reads the rows out of a Part List PDF. A row is recognised by its ITEM cell ("2 X 10H") and
/// then split into columns by counting what sits between SECTION and the steel grade, so the
/// WIDTH column being empty (it usually is) doesn't shift anything.
/// </summary>
public static class PartListReader
{
    // Bidirectional control characters some PDF writers wrap around every right-to-left run.
    private static readonly Regex BidiRx = new("[\u200E\u200F\u202A-\u202E\u2066-\u2069]", RegexOptions.Compiled);
    private static readonly Regex ArabicRx = new("[\u0600-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFF]", RegexOptions.Compiled);
    private static readonly Regex GradeRx = new(@"^(?:S\d{3}[A-Z0-9]{0,4}|ST\.?\d{2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadRx = new(@"^(\d+)\s*[xX×]\s*(\S+)\s+(.+)$", RegexOptions.Compiled);

    // One parsed Part List per project, re-read only when a different file is uploaded.
    private static readonly ConcurrentDictionary<int, (DateTime Stamp, List<PartListRow> Rows)> Cache = new();

    public static List<PartListRow> ReadCached(int projectId, DateTime stamp, byte[] pdf)
    {
        if (Cache.TryGetValue(projectId, out var hit) && hit.Stamp == stamp) return hit.Rows;
        var rows = Read(pdf);
        Cache[projectId] = (stamp, rows);
        return rows;
    }

    /// <summary>The standard Part List template, left to right. "Profie" is spelled as in the template ("Profile" is accepted too).</summary>
    public static readonly string[] TemplateColumns = { "Mark No.", "Profie", "Weight(kg/m)", "Qty/LEG", "Length(m)", "Description" };

    private static readonly string[][] TemplateColumnForms =
    {
        new[] { "MARKNO" }, new[] { "PROFIE", "PROFILE" }, new[] { "WEIGHTKGM" },
        new[] { "QTYLEG" }, new[] { "LENGTHM" }, new[] { "DESCRIPTION" },
    };
    private static readonly Regex NumRx = new(@"^\d+(?:[.,]\d+)?$", RegexOptions.Compiled);
    private static readonly Regex IntRx = new(@"^\d+$", RegexOptions.Compiled);

    /// <summary>Every text line of the PDF, page by page, as its words (left edge + text) from left to right.</summary>
    private static List<(double X, string T)[]> ExtractLines(byte[] pdf)
    {
        var result = new List<(double X, string T)[]>();
        try
        {
            using var doc = PdfDocument.Open(pdf);
            foreach (var page in doc.GetPages())
            {
                var lines = new List<(double Y, List<Word> Words)>();
                foreach (var word in page.GetWords().OrderByDescending(x => x.BoundingBox.Centroid.Y))
                {
                    double y = word.BoundingBox.Centroid.Y;
                    if (lines.Count > 0 && Math.Abs(lines[^1].Y - y) <= 3.5) lines[^1].Words.Add(word);
                    else lines.Add((y, new List<Word> { word }));
                }
                foreach (var line in lines)
                {
                    var words = line.Words.OrderBy(x => x.BoundingBox.Left)
                        .Select(x => (X: x.BoundingBox.Left, T: BidiRx.Replace(x.Text, "").Trim()))
                        .Where(w => w.T.Length > 0).ToArray();
                    if (words.Length > 0) result.Add(words);
                }
            }
        }
        catch
        {
            // An unreadable or scanned file just yields no lines.
        }
        return result;
    }

    // The header line: all six template columns, in order, on one line (spaces and punctuation ignored).
    private static bool IsTemplateHeader((double X, string T)[] words)
    {
        var head = Normalize(string.Concat(words.Select(w => w.T)));
        foreach (var profile in TemplateColumnForms[1])
        {
            var expected = "MARKNO" + profile + "WEIGHTKGM" + "QTYLEG" + "LENGTHM" + "DESCRIPTION";
            if (head.StartsWith(expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Left edge of each of the six columns, read off the header line (null if a header word can't be located).
    private static double[]? HeaderLefts((double X, string T)[] words)
    {
        var left = new double[6];
        left[0] = words[0].X;
        for (int c = 1; c < 6; c++)
        {
            bool found = false;
            foreach (var w in words)
            {
                var n = Normalize(w.T);
                if (n.Length > 0 && TemplateColumnForms[c].Any(f => n.StartsWith(f, StringComparison.Ordinal)))
                { left[c] = w.X; found = true; break; }
            }
            if (!found) return null;
        }
        for (int c = 1; c < 6; c++) if (left[c] <= left[c - 1]) return null;
        return left;
    }

    // A row is cut into its six cells by where each word starts — a Mark No. such as "NUMBER PLATE" or a
    // Profie such as "PL 10Hx205" contains a space, so counting words would shift every later column.
    private static PartListRow? ParseTemplateRow((double X, string T)[] words, double[]? left)
    {
        var cells = new string[6];
        if (left != null)
        {
            var parts = Enumerable.Range(0, 6).Select(_ => new List<string>()).ToArray();
            foreach (var w in words)
            {
                int c = 0;
                for (int i = 5; i >= 1; i--)
                    if (w.X >= left[i] - 4) { c = i; break; }
                parts[c].Add(w.T);
            }
            for (int i = 0; i < 6; i++) cells[i] = string.Join(" ", parts[i]);
        }
        else
        {
            // No usable header positions: fall back to counting words (works when Mark No. and Profie have no spaces).
            if (words.Length < 5) return null;
            for (int i = 0; i < 5; i++) cells[i] = words[i].T;
            cells[5] = string.Join(" ", words.Skip(5).Select(w => w.T));
        }
        if (cells[0].Length == 0 || cells[1].Length == 0) return null;
        // Weight and Qty/LEG must be numbers; Length may be empty on a row that has none.
        if (!NumRx.IsMatch(cells[2]) || !IntRx.IsMatch(cells[3]) || (cells[4].Length > 0 && !NumRx.IsMatch(cells[4]))) return null;
        var key = Normalize(cells[0]);
        if (key.Length == 0) return null;
        return new PartListRow
        {
            Template = true,
            Raw = string.Join(" ", cells.Where(x => x.Length > 0)),
            Keys = new HashSet<string> { key },
            RowKey = key,
            Pos = cells[0],
            Section = cells[1],
            Weight = cells[2],
            Qty = cells[3],
            Length = cells[4],
            Remark = cells[5],
        };
    }

    /// <summary>
    /// Checks a Part List PDF against the standard template — a header line with the columns
    /// Mark No. | Profie | Weight(kg/m) | Qty/LEG | Length(m) | Description, followed by rows in
    /// that order — and reads its rows. Rows on later pages (with or without a repeated header) are handled too.
    /// </summary>
    public static PartListTemplateResult ReadTemplate(byte[] pdf)
    {
        var res = new PartListTemplateResult();
        var lines = ExtractLines(pdf);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double[]? left = null;
        foreach (var words in lines)
        {
            if (IsTemplateHeader(words))                                          // also covers a header repeated on each page
            {
                res.HeaderFound = true;
                left = HeaderLefts(words) ?? left;
                continue;
            }
            if (!res.HeaderFound) continue;                                       // anything above the header is just a title
            var row = ParseTemplateRow(words, left);
            if (row != null)
            {
                if (seen.Add(row.RowKey)) res.Rows.Add(row);
                continue;
            }
            if (words.Length >= 3 && string.Concat(words.Select(w => w.T)).Any(char.IsDigit))
            {
                res.BadLines++;
                res.FirstBadLine ??= string.Join(" ", words.Select(w => w.T));
            }
        }
        res.RowCount = res.Rows.Count;

        if (!res.HeaderFound)
        {
            // Which template columns are not even present near the top of the file?
            var top = Normalize(string.Concat(lines.Take(12).SelectMany(x => x).Select(w => w.T)));
            for (int i = 0; i < TemplateColumns.Length; i++)
                if (!TemplateColumnForms[i].Any(f => top.Contains(f, StringComparison.Ordinal)))
                    res.MissingColumns.Add(TemplateColumns[i]);
            return res;
        }
        res.Ok = res.RowCount >= 1 && res.RowCount * 10 >= (res.RowCount + res.BadLines) * 8;
        return res;
    }

    public static List<PartListRow> Read(byte[] pdf)
    {
        // The standard template first; a list uploaded before the template existed keeps working below.
        var tpl = ReadTemplate(pdf);
        if (tpl.Ok) return tpl.Rows;
        return ReadLegacy(pdf);
    }

    private static List<PartListRow> ReadLegacy(byte[] pdf)
    {
        // Every text line of the file that could be a table row: at least three words and a digit.
        var candidates = new List<string>();
        var strict = new List<PartListRow>();
        var strictSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = PdfDocument.Open(pdf);
            foreach (var page in doc.GetPages())
            {
                // Group the page's words into text lines by vertical position. Rows sit ~12pt
                // apart while words on one row differ by well under 1pt, so 3.5pt is a safe cut.
                var lines = new List<(double Y, List<Word> Words)>();
                foreach (var word in page.GetWords().OrderByDescending(x => x.BoundingBox.Centroid.Y))
                {
                    double y = word.BoundingBox.Centroid.Y;
                    if (lines.Count > 0 && Math.Abs(lines[^1].Y - y) <= 3.5) lines[^1].Words.Add(word);
                    else lines.Add((y, new List<Word> { word }));
                }

                foreach (var line in lines)
                {
                    var tokens = line.Words.OrderBy(x => x.BoundingBox.Left)
                        .Select(x => BidiRx.Replace(x.Text, "").Trim())
                        .Where(t => t.Length > 0).ToList();
                    var text = string.Join(" ", tokens);
                    if (tokens.Count >= 3 && text.Any(char.IsDigit)) candidates.Add(text);
                    var parsed = ParseRow(text);
                    if (parsed != null && strictSeen.Add(parsed.Pos)) strict.Add(parsed);
                }
            }
        }
        catch
        {
            // An unreadable or scanned file just yields no rows — the page says so plainly.
        }

        // A normal Tekla list: nearly every candidate line is a row, and that's the result.
        if (strict.Count >= 5 && strict.Count * 10 >= candidates.Count * 4) return strict;

        // Any other layout: don't give up — every line that looks like a row is listed as plain text
        // (rows that do follow the Tekla layout keep their columns), so searching and ticking still work.
        var result = new List<PartListRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in candidates)
        {
            var row = ParseRow(text) ?? LooseRow(text);
            if (row.RowKey.Length == 0 || !seen.Add(row.RowKey)) continue;
            result.Add(row);
        }
        return result;
    }

    private static PartListRow LooseRow(string text)
    {
        var keys = new HashSet<string>();
        foreach (var piece in Regex.Split(text, @"[\s|,;]+"))
        {
            var k = Normalize(piece);
            if (k.Length == 0 || k == "X") continue;
            keys.Add(k);
        }
        var norm = Normalize(text);
        return new PartListRow
        {
            Raw = text,
            Loose = true,
            Keys = keys,
            RowKey = norm.Length == 0 ? "" : "L:" + (norm.Length > 100 ? norm[..100] : norm),
        };
    }

    private static PartListRow? ParseRow(string lineText)
    {
        var m = HeadRx.Match(lineText);
        if (!m.Success) return null;
        var line = lineText;

        // rest: QTY SECTION [WIDTH] LENGTH POS WEIGHT GRADE [REMARK…]
        var rest = m.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int g = -1;
        for (int i = 3; i < rest.Length; i++)
        {
            if (GradeRx.IsMatch(rest[i])) { g = i; break; }
        }
        if (g < 0) return null;

        var mid = rest.Skip(2).Take(g - 2).ToArray();   // everything between SECTION and the grade
        string width = "", length, pos, weight;
        if (mid.Length == 3) { length = mid[0]; pos = mid[1]; weight = mid[2]; }
        else if (mid.Length == 4) { width = mid[0]; length = mid[1]; pos = mid[2]; weight = mid[3]; }
        else return null;

        var remark = string.Join(" ", rest.Skip(g + 1));
        var nPos = Normalize(pos);
        return new PartListRow
        {
            Raw = line,
            Keys = new HashSet<string> { nPos },
            RowKey = nPos,
            Pos = pos,
            Qty = rest[0],
            Section = rest[1],
            Width = width,
            Length = length,
            Weight = weight,
            Grade = rest[g].ToUpperInvariant(),
            Remark = ArabicRx.IsMatch(remark) ? "" : remark,
        };
    }

    /// <summary>Letters and digits only, upper-case — "10h", "10 H" and "10-H" all compare equal.</summary>
    public static string Normalize(string? s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// The forms of a sheet's Part No worth looking up in the list: the whole value, then — when it
    /// carries a prefix like "AOI P30 10H" or "P30E15-10H" — just its last piece, the part's own mark.
    /// </summary>
    public static IEnumerable<string> Candidates(string? partNo)
    {
        var whole = Normalize(partNo);
        if (whole.Length > 0) yield return whole;

        var pieces = Regex.Split(partNo ?? "", @"[\s\-_/.\\]+").Where(x => x.Length > 0).ToList();
        if (pieces.Count > 1)
        {
            var last = Normalize(pieces[^1]);
            if (last.Length > 0 && last != whole) yield return last;
        }
    }
}