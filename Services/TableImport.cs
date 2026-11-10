using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TowerProcessSheet.Services;

/// <summary>
/// Reads a parts list from CSV or XLSX into rows of strings.
/// XLSX is parsed directly from the OpenXML zip, so no NuGet package is needed
/// (works offline / behind the factory firewall).
/// </summary>
public static class TableImport
{
    public static List<string[]> Read(string fileName, Stream stream)
        => fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(stream)
            : ReadXlsx(stream);

    // ---------- CSV ----------
    public static List<string[]> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();

        var rows = new List<string[]>();
        var cur = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cur.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\r') { /* skip */ }
            else if (c == '\n') { cur.Add(sb.ToString()); sb.Clear(); rows.Add(cur.ToArray()); cur = new(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString()); rows.Add(cur.ToArray()); }

        return rows.Where(r => r.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
    }

    // ---------- XLSX (minimal, dependency-free) ----------
    public static List<string[]> ReadXlsx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst != null)
        {
            using var s = sst.Open();
            var doc = XDocument.Load(s);
            foreach (var si in doc.Root!.Elements(ns + "si"))
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        }

        var sheetEntry = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => e.FullName).FirstOrDefault();
        if (sheetEntry == null) return new();

        var rows = new List<string[]>();
        using var s2 = sheetEntry.Open();
        var wdoc = XDocument.Load(s2);
        var sheetData = wdoc.Root!.Element(ns + "sheetData");
        if (sheetData == null) return new();

        foreach (var row in sheetData.Elements(ns + "row"))
        {
            var cells = new Dictionary<int, string>();
            int maxCol = -1;
            foreach (var c in row.Elements(ns + "c"))
            {
                int col = ColIndex((string?)c.Attribute("r") ?? "");
                if (col < 0) continue;
                string type = (string?)c.Attribute("t") ?? "";
                string val;
                if (type == "s")
                {
                    val = int.TryParse(c.Element(ns + "v")?.Value, out int idx) && idx >= 0 && idx < shared.Count
                        ? shared[idx] : "";
                }
                else if (type == "inlineStr")
                {
                    val = string.Concat(c.Element(ns + "is")?.Descendants(ns + "t").Select(t => t.Value)
                                        ?? Enumerable.Empty<string>());
                }
                else val = c.Element(ns + "v")?.Value ?? "";
                cells[col] = val;
                if (col > maxCol) maxCol = col;
            }
            var arr = new string[maxCol + 1];
            for (int i = 0; i <= maxCol; i++) arr[i] = cells.TryGetValue(i, out var v) ? v : "";
            rows.Add(arr);
        }

        return rows.Where(r => r.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
    }

    private static int ColIndex(string cellRef)
    {
        int idx = 0;
        foreach (char c in cellRef)
        {
            if (char.IsLetter(c)) idx = idx * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            else break;
        }
        return idx - 1;
    }
}
