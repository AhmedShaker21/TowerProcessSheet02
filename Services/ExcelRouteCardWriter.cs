using ClosedXML.Excel;
using TowerProcessSheet.Models;

namespace TowerProcessSheet.Services;

/// <summary>Builds a native .xlsx where each process sheet is a formatted worksheet
/// (merged cells, borders, column widths) with the drawing embedded as a real picture.</summary>
public static class ExcelRouteCardWriter
{
    public static byte[] Build(IEnumerable<ProcessSheet> sheets)
    {
        using var wb = new XLWorkbook();
        int n = 0;
        var used = new HashSet<string>();

        foreach (var sheet in sheets)
        {
            n++;
            string name = SafeSheetName(sheet.DrawingNo, n, used);
            var ws = wb.AddWorksheet(name);
            BuildSheet(ws, sheet);
        }
        if (n == 0) wb.AddWorksheet("Empty");

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildSheet(IXLWorksheet ws, ProcessSheet s)
    {
        var rows = OperationCatalog.Generate(s);

        // 13 columns: A cost, B place, C setting, D time, E op, F description, G QTY-width part of
        // "Process Description", H operator, I+J check-for-first-part (two sub-columns),
        // K accepted, L rejected, M inspector.
        double[] widths = { 9, 6, 7, 7, 6, 34, 12, 10, 6, 6, 8, 8, 10 };
        for (int c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];

        var accepted = (s.Reviews ?? new List<SheetReview>()).Where(r => r.Decision == ReviewDecision.Accepted).ToList();
        string? opCode = accepted.Where(r => r.Slot == SignOffSlot.Operator).OrderByDescending(r => r.ReviewedAt)
            .Select(r => r.ReviewerCode ?? r.ReviewerName).FirstOrDefault();
        string? inspCode = accepted.Where(r => r.Slot == SignOffSlot.Inspector).OrderByDescending(r => r.ReviewedAt)
            .Select(r => r.ReviewerCode ?? r.ReviewerName).FirstOrDefault();
        var compiled = accepted.FirstOrDefault(r => r.Slot == SignOffSlot.CompiledBy);
        var checkedR = accepted.FirstOrDefault(r => r.Slot == SignOffSlot.CheckedBy);
        var approved = accepted.FirstOrDefault(r => r.Slot == SignOffSlot.ApprovedBy);

        int r0 = 1;

        // --- Title block ---
        Set(ws, r0, 1, "Contract No.  " + (s.ContractNo ?? "")); Merge(ws, r0, 1, r0, 5, left: true);
        Set(ws, r0, 6, "Project Name:.  " + s.ProjectName); Merge(ws, r0, 6, r0 + 1, 8, bold: true, size: 13);
        Set(ws, r0, 9, "Work Order No.:  " + (s.WorkOrderNo ?? "")); Merge(ws, r0, 9, r0 + 1, 13, left: true);

        Set(ws, r0 + 1, 1, "Part No.:  " + s.PartNo); Merge(ws, r0 + 1, 1, r0 + 1, 5, left: true, bold: true, size: 13);

        Set(ws, r0 + 2, 1, "Drawing No.:  " + s.DrawingNo); Merge(ws, r0 + 2, 1, r0 + 3, 5, left: true, bold: true, size: 13);
        Set(ws, r0 + 2, 6, "DESCRIPTION"); Center(ws, r0 + 2, 6);
        Set(ws, r0 + 2, 7, "QTY"); Center(ws, r0 + 2, 7);
        Set(ws, r0 + 2, 8, "Total QTY"); Center(ws, r0 + 2, 8);
        Set(ws, r0 + 2, 9, "ASS'D ON"); Merge(ws, r0 + 2, 9, r0 + 2, 10);
        Set(ws, r0 + 2, 11, "Quality Insp." + (string.IsNullOrWhiteSpace(s.QualityInsp) ? "" : "\n" + s.QualityInsp));
        Merge(ws, r0 + 2, 11, r0 + 3, 13);
        ws.Cell(r0 + 2, 11).Style.Alignment.WrapText = true;
        ws.Cell(r0 + 2, 11).Style.Font.Bold = true;

        Set(ws, r0 + 3, 6, s.Section); Center(ws, r0 + 3, 6, bold: true);
        Set(ws, r0 + 3, 7, s.Qty.ToString()); Center(ws, r0 + 3, 7, bold: true);
        Set(ws, r0 + 3, 8, s.TotalQty ?? ""); Center(ws, r0 + 3, 8);
        Set(ws, r0 + 3, 9, s.AssembledOn ?? ""); Merge(ws, r0 + 3, 9, r0 + 3, 10);

        // --- Column header band (two rows) ---
        int h1 = r0 + 4, h2 = r0 + 5;
        Set(ws, h1, 1, "Deliver To"); Center(ws, h1, 1);
        Set(ws, h1, 2, "Place Of ST."); Merge(ws, h1, 2, h2, 2);
        Set(ws, h1, 3, "TOTAL - QTY/Assy"); Merge(ws, h1, 3, h1, 4);
        Set(ws, h1, 5, "UNIT"); Center(ws, h1, 5);
        Set(ws, h1, 6, "Process Description"); Merge(ws, h1, 6, h2, 7);
        Set(ws, h1, 8, "Operator"); Merge(ws, h1, 8, h2, 8);
        Set(ws, h1, 9, "Check for First Part"); Merge(ws, h1, 9, h1, 10);
        Set(ws, h1, 11, "Accepted Qty"); Merge(ws, h1, 11, h2, 11);
        Set(ws, h1, 12, "Rejected Qty"); Merge(ws, h1, 12, h2, 12);
        Set(ws, h1, 13, "Inspector"); Merge(ws, h1, 13, h2, 13);
        Set(ws, h2, 1, "COST. CENT"); Center(ws, h2, 1);
        Set(ws, h2, 3, "Setting"); Center(ws, h2, 3);
        Set(ws, h2, 4, "TIME ALL'D"); Center(ws, h2, 4);
        Set(ws, h2, 5, "OP. NO"); Center(ws, h2, 5);
        Set(ws, h2, 9, "Inspector"); Merge(ws, h2, 9, h2, 10);

        // --- Operation rows ---
        int r = h2 + 1;
        foreach (var row in rows)
        {
            Set(ws, r, 1, row.CostCenter); Center(ws, r, 1, bold: true);
            Set(ws, r, 3, row.Setting); Center(ws, r, 3);
            Set(ws, r, 4, row.TimeAllowed); Center(ws, r, 4);
            Set(ws, r, 5, row.OpNo); Center(ws, r, 5);
            Set(ws, r, 6, row.Description);
            ws.Range(r, 6, r, 7).Merge();
            ws.Cell(r, 6).Style.Alignment.WrapText = true;
            ws.Cell(r, 6).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Cell(r, 6).Style.Font.Bold = true;

            bool isOp = !row.IsInspection && row.Setting != "W";
            if (row.IsInspection && inspCode != null) { Set(ws, r, 13, inspCode); RedStamp(ws, r, 13); }
            if (isOp && opCode != null) { Set(ws, r, 8, opCode); RedStamp(ws, r, 8); }

            ws.Row(r).Height = row.Description.Contains('\n') ? 30 : 20;
            r++;
        }

        int lastRow = r - 1;
        // Borders around the whole grid.
        var grid = ws.Range(r0, 1, lastRow, 13);
        grid.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        grid.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        grid.Style.Font.FontSize = 11;

        // --- Diagram image ---
        if (s.DiagramImage != null && s.DiagramImage.Length > 0)
        {
            r++;
            using var imgMs = new MemoryStream(s.DiagramImage);
            var pic = ws.AddPicture(imgMs, "diagram").MoveTo(ws.Cell(r, 1));
            pic.WithSize(720, 200);
            r += 12;   // leave space for the picture
        }

        // --- Footer ---
        r += 1;
        Set(ws, r, 1, "Complied by : " + (compiled?.ReviewerName ?? s.CompiledBy ?? "")); Merge(ws, r, 1, r, 5, left: true, bold: true);
        Set(ws, r, 6, "Sheet No.  " + (s.SheetNo ?? "1/1")); Merge(ws, r, 6, r, 8, left: true, bold: true);
        Set(ws, r, 9, "Checked : " + (checkedR?.ReviewerName ?? s.CheckedBy ?? "")); Merge(ws, r, 9, r, 13, left: true, bold: true);
        r++;
        Set(ws, r, 1, "Date : " + (compiled != null ? compiled.ReviewedAt.ToString("d/M/yyyy") : s.SheetDate ?? "")); Merge(ws, r, 1, r, 5, left: true, bold: true);
        Set(ws, r, 6, "No. of sheets " + (s.NoOfSheets ?? "1")); Merge(ws, r, 6, r, 7, left: true, bold: true);
        Set(ws, r, 8, "Written By : " + (s.WrittenBy ?? "")); Merge(ws, r, 8, r, 10, left: true, bold: true);
        Set(ws, r, 11, "Approved: " + (approved?.ReviewerName ?? s.Approved ?? "")); Merge(ws, r, 11, r, 13, left: true, bold: true);
        r++;
        Set(ws, r, 1, "Batch No.  " + (s.Batch?.Name ?? "")); Merge(ws, r, 1, r, 5, left: true, bold: true);
        Set(ws, r, 6, $"Process sheet Issue : {s.ProcessSheetIssue ?? "1"}   Drawing Issue: {s.DrawingIssue ?? "1"}"); Merge(ws, r, 6, r, 10, left: true, bold: true);
        Set(ws, r, 11, s.DocRef ?? ""); Merge(ws, r, 11, r, 13, bold: true);

        ws.PageSetup.PagesWide = 1;
    }

    // ---- helpers ----
    private static void Set(IXLWorksheet ws, int r, int c, string v) => ws.Cell(r, c).Value = v;

    private static void Merge(IXLWorksheet ws, int r1, int c1, int r2, int c2,
        bool left = false, bool bold = false, double size = 0)
    {
        var rng = ws.Range(r1, c1, r2, c2);
        rng.Merge();
        rng.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        rng.Style.Alignment.Horizontal = left ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Center;
        if (bold || size > 0) { ws.Cell(r1, c1).Style.Font.Bold = bold || size > 0; }
        if (size > 0) ws.Cell(r1, c1).Style.Font.FontSize = size;
    }

    private static void Center(IXLWorksheet ws, int r, int c, bool bold = false)
    {
        var st = ws.Cell(r, c).Style;
        st.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        st.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        st.Alignment.WrapText = true;
        st.Font.Bold = bold || true;
        st.Font.FontSize = 10;
    }

    private static void RedStamp(IXLWorksheet ws, int r, int c)
    {
        var st = ws.Cell(r, c).Style;
        st.Font.FontColor = XLColor.Red;
        st.Font.Bold = true;
        st.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        st.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static string SafeSheetName(string? drawing, int n, HashSet<string> used)
    {
        string baseName = string.Concat((drawing ?? $"Sheet{n}")
            .Where(ch => ch != '\\' && ch != '/' && ch != '?' && ch != '*' && ch != '[' && ch != ']' && ch != ':'));
        if (baseName.Length > 27) baseName = baseName[..27];
        string name = $"{n}-{baseName}";
        if (name.Length > 31) name = name[..31];
        int suffix = 1;
        while (!used.Add(name)) { name = $"{n}-{suffix++}"; }
        return name;
    }
}
