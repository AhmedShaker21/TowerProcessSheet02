namespace TowerProcessSheet.Models;

/// <summary>One generated line of the final route card.</summary>
public class GeneratedRow
{
    public string CostCenter { get; set; } = "";
    public string Setting { get; set; } = "";
    public string TimeAllowed { get; set; } = "";
    public string OpNo { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsInspection { get; set; }
}

/// <summary>A fabrication operation the user can pick.</summary>
public class OperationDef
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";        // dropdown label
    public string CostCenter { get; set; } = "";
    public string Setting { get; set; } = "";
    public string TimeAllowed { get; set; } = "";
    public string InspectionCostCenter { get; set; } = "5130"; // INSP. row after this op
    public string Pattern { get; set; } = "";      // may use {length} {project} {drawing}
}

public static class OperationCatalog
{
    public const string FixedBarLength = "12000";   // L= 12000mm is fixed
    public const string GalvanizeMicron = "85";     // 85 Micron is fixed

    // Cost centers used by the generated (non-pickable) steps.
    private const string CcMaterial      = "4730";
    private const string CcIncomingInsp  = "5110";
    private const string CcFinalInsp     = "5150";
    private const string CcFinalStore    = "4411";

    /// <summary>Operations the user selects, in the order they are performed.</summary>
    public static readonly List<OperationDef> Operations = new()
    {
        new OperationDef
        {
            Key = "CUT_DRILL", Name = "Cutting & Drilling & No CNC",
            CostCenter = "4730", Setting = "5", TimeAllowed = "5",
            Pattern = "Cutting & Drilling & No  CNC\nCut the part with length = {length} mm\nas per PRG No.O {project}  {drawing}"
        },
        new OperationDef
        {
            Key = "CUTTING", Name = "Cutting",
            CostCenter = "4730", Setting = "20", TimeAllowed = "10",
            Pattern = "Cutting\nCutting The Part To correcting as per drawing"
        },
        new OperationDef
        {
            Key = "MILLING", Name = "Milling",
            CostCenter = "4212", Setting = "20", TimeAllowed = "10",
            Pattern = "Milling\nMilling The Part To correcting as per drawing"
        },
        new OperationDef
        {
            Key = "GALVANIZE", Name = "Hot Deep Galvanized",
            CostCenter = "4620", Setting = "5", TimeAllowed = "5",
            InspectionCostCenter = "5140",
            Pattern = "Hot Deep Galvanized\nContribution up to 85 Micron"
        },
    };

    public static OperationDef? GetOperation(string? key) =>
        Operations.FirstOrDefault(o => o.Key == key);

    /// <summary>Material grade + ST designation, picked from a dropdown.</summary>
    public static readonly List<string> MaterialOptions = new()
    {
        "S235JR / ST.37",
        "S355JR / ST.52",
        "S235J0 / ST.37",
        "S355J0 / ST.52",
        "S235J2 / ST.37",
        "S355J2 / ST.52",
    };

    /// <summary>Smart default for the material dropdown: Drawing No. with 'H' => S355/ST.52.</summary>
    public static string DefaultMaterial(string? drawingNo)
    {
        var hasH = (drawingNo ?? "").ToUpperInvariant().Contains('H');
        return hasH ? "S355JR / ST.52" : "S235JR / ST.37";
    }

    /// <summary>Selectable equal-angle sections (leg*thickness).</summary>
    public static readonly List<string> AngleSections = new()
    {
        "L45*5", "L50*5", "L60*5", "L60*6", "L70*5", "L100*10", "L120*10"
    };

    /// <summary>Standard plate sheet size; thickness is appended (e.g. 1000*3000*6).</summary>
    public const string PlateSize = "1000*3000";
    public static readonly List<string> PlateThicknesses = new() { "6", "8", "10", "12", "16" };
    public static List<string> PlateSections =>
        PlateThicknesses.Select(t => $"{PlateSize}*{t}").ToList();

    /// <summary>A plate size looks like 1000*3000*6 (three numbers); an angle is L45*5.</summary>
    public static bool IsPlate(string? section) =>
        !string.IsNullOrWhiteSpace(section) &&
        System.Text.RegularExpressions.Regex.IsMatch(section.Trim(), @"^\d+\*\d+\*\d+$");

    /// <summary>"L50*5" => "50*50*5" (equal angle). Unequal/odd inputs pass through.</summary>
    public static string ExpandSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return "";
        var s = section.Trim();
        if (s.StartsWith("L", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);

        var parts = s.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2) return $"{parts[0]}*{parts[0]}*{parts[1]}";
        return string.Join("*", parts);
    }

    public static (string Grade, string St) SplitMaterial(string? material)
    {
        if (string.IsNullOrWhiteSpace(material)) return ("", "");
        var i = material.IndexOf('/');
        return i < 0
            ? (material.Trim(), "")
            : (material[..i].Trim(), material[(i + 1)..].Trim());
    }

    /// <summary>
    /// Builds the complete route card from the header + selected operations.
    /// This is the single source of truth used by the Details view.
    /// </summary>
    public static List<GeneratedRow> Generate(ProcessSheet sheet)
    {
        var rows = new List<GeneratedRow>();
        int op = 0;
        string NextOp() { op += 10; return op.ToString("000"); }

        var (grade, st) = SplitMaterial(sheet.Material);

        // 1) Material (no Op. No) — angle uses a 12000mm bar, plate uses the standard sheet
        string materialDesc = IsPlate(sheet.Section)
            ? $"MATERIAL  {grade} / {st}\nPlate  {sheet.Section}"
            : $"MATERIAL  {grade} / {st}\nAngle  {ExpandSection(sheet.Section)}   L= {FixedBarLength}mm";

        rows.Add(new GeneratedRow
        {
            CostCenter = string.IsNullOrWhiteSpace(sheet.CustomMaterialCostCenter) ? CcMaterial : sheet.CustomMaterialCostCenter,
            Setting = "W", TimeAllowed = "W", OpNo = "",
            Description = materialDesc
        });

        // 2) Incoming inspection (fixed sentence)
        rows.Add(new GeneratedRow
        {
            CostCenter = string.IsNullOrWhiteSpace(sheet.CustomIncomingInspCostCenter) ? CcIncomingInsp : sheet.CustomIncomingInspCostCenter,
            Setting = "W", TimeAllowed = "W", OpNo = NextOp(),
            IsInspection = true,
            Description = "Insp\nCheck the incoming Material and Dimension"
        });

        // 3) Each selected operation, followed by its inspection.
        //    Galvanizing is treated as an operation: if "Auto-add galvanizing" is on
        //    and the user did not place one themselves, append it at the end.
        var ops = sheet.Operations.OrderBy(x => x.SortOrder).ToList();
        bool hasGalvanize = ops.Any(o => o.OperationKey == "GALVANIZE");
        if (sheet.IncludeGalvanizing && !hasGalvanize)
            ops.Add(new ProcessOperation { OperationKey = "GALVANIZE" });

        foreach (var o in ops)
        {
            var def = GetOperation(o.OperationKey);
            if (def == null) continue;

            var desc = def.Pattern
                .Replace("{length}", sheet.CutLength ?? "")
                .Replace("{project}", sheet.ProjectName ?? "")
                .Replace("{drawing}", sheet.DrawingNo ?? "");
            if (!string.IsNullOrWhiteSpace(o.CustomDescription)) desc = o.CustomDescription;

            string setting = string.IsNullOrWhiteSpace(o.CustomSetting) ? def.Setting : o.CustomSetting;
            string timeAllowed = string.IsNullOrWhiteSpace(o.CustomTimeAllowed) ? def.TimeAllowed : o.CustomTimeAllowed;
            rows.Add(new GeneratedRow
            {
                CostCenter = string.IsNullOrWhiteSpace(o.CustomCostCenter) ? def.CostCenter : o.CustomCostCenter,
                Setting = setting == "0" ? "W" : setting,
                TimeAllowed = timeAllowed == "0" ? "W" : timeAllowed,
                OpNo = NextOp(), Description = desc
            });
            if (!string.IsNullOrEmpty(def.InspectionCostCenter))
            {
                rows.Add(new GeneratedRow
                {
                    CostCenter = string.IsNullOrWhiteSpace(o.CustomInspCostCenter) ? def.InspectionCostCenter : o.CustomInspCostCenter,
                    Setting = "W", TimeAllowed = "W", OpNo = "",
                    IsInspection = true, Description = "INSP."
                });
            }
        }

        // 4) Final inspection + final store
        rows.Add(new GeneratedRow
        {
            CostCenter = string.IsNullOrWhiteSpace(sheet.CustomFinalInspCostCenter) ? CcFinalInsp : sheet.CustomFinalInspCostCenter,
            Setting = "W", TimeAllowed = "W", OpNo = NextOp(),
            IsInspection = true, Description = "F. Insp."
        });
        rows.Add(new GeneratedRow
        {
            CostCenter = string.IsNullOrWhiteSpace(sheet.CustomFinalStoreCostCenter) ? CcFinalStore : sheet.CustomFinalStoreCostCenter,
            Setting = "W", TimeAllowed = "W", OpNo = NextOp(),
            Description = "F. Store"
        });

        return rows;
    }
}
