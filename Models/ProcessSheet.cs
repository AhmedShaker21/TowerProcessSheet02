using System.ComponentModel.DataAnnotations;

namespace TowerProcessSheet.Models;

/// <summary>
/// The route card. The user enters the drawing title-block data and picks the
/// fabrication operations; the full step list is generated (see OperationCatalog).
/// </summary>
public class ProcessSheet
{
    public int Id { get; set; }

    // ---- Workflow: which batch this sheet belongs to, and its per-role reviews ----
    public int? BatchId { get; set; }
    public Batch? Batch { get; set; }
    public List<SheetReview> Reviews { get; set; } = new();

    // ---- Title-block header (from the drawing) ----
    [Display(Name = "Contract No.")]
    [StringLength(60)]
    public string? ContractNo { get; set; }

    [Required, Display(Name = "Part No.")]
    [StringLength(60)]
    public string PartNo { get; set; } = string.Empty;

    [Required, Display(Name = "Drawing No.")]
    [StringLength(60)]
    public string DrawingNo { get; set; } = string.Empty;

    [Required, Display(Name = "Project Name")]
    [StringLength(100)]
    public string ProjectName { get; set; } = string.Empty;   // e.g. "S2  220 kv"

    [Display(Name = "Work Order No.")]
    [StringLength(60)]
    public string? WorkOrderNo { get; set; }
    public string? ContractNotificationNo { get; set; }   // "36/2026" style — set by Programs & Operation
    public DateTime? ProgramsFinalizedAt { get; set; }     // when the current Contract/WO numbers were set
    public string? ResearchGroupKey { get; set; }          // shared across every destination's copy of the same original drawing
    public bool IsUnclassified { get; set; }                // came from Research's "Other" bucket - Section couldn't be read automatically
    public bool IsScannedImage { get; set; }                 // no text layer at all - no box position could ever read this automatically
    public string? ProgramsIssue { get; set; }
    public string? ProgramsDate { get; set; }
    public DateTime? SentToQualityAt { get; set; }
    public DateTime? ResentForReviewAt { get; set; }   // set when Planning re-sends a Rejected sheet after fixing it

    [Required, Display(Name = "Section")]
    [StringLength(60)]
    public string Section { get; set; } = string.Empty;       // e.g. "L50*5"

    [Display(Name = "Qty")]
    [Range(0, int.MaxValue)]
    public int Qty { get; set; }

    [Required, Display(Name = "Cut length (mm)")]
    [StringLength(30)]
    public string CutLength { get; set; } = string.Empty;     // e.g. "1643"

    [Required, Display(Name = "Material")]
    [StringLength(40)]
    public string Material { get; set; } = string.Empty;      // e.g. "S355JR / ST.52"

    [Display(Name = "Auto-add Hot-Dip Galvanizing at end (if not in operations)")]
    public bool IncludeGalvanizing { get; set; } = true;

    // ---- Optional header cells ----
    [Display(Name = "Total Qty")][StringLength(30)] public string? TotalQty { get; set; }
    [Display(Name = "Ass'd On")][StringLength(60)] public string? AssembledOn { get; set; }
    [Display(Name = "Quality Insp.")][StringLength(60)] public string? QualityInsp { get; set; }

    // ---- Optional footer ----
    [Display(Name = "Compiled By")][StringLength(80)] public string? CompiledBy { get; set; }
    [Display(Name = "Checked By")][StringLength(80)] public string? CheckedBy { get; set; }
    [Display(Name = "Sheet No.")][StringLength(20)] public string? SheetNo { get; set; }
    [Display(Name = "Date")][StringLength(30)] public string? SheetDate { get; set; }

    // ---- Extra footer fields (auto-filled with defaults) ----
    [StringLength(20)] public string NoOfSheets { get; set; } = "1";
    [StringLength(80)] public string? WrittenBy { get; set; }
    [StringLength(80)] public string? Approved { get; set; }
    [StringLength(20)] public string ProcessSheetIssue { get; set; } = "1";
    [StringLength(20)] public string DrawingIssue { get; set; } = "1";
    [StringLength(40)] public string DocRef { get; set; } = "E/206/401/ن";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Cropped diagram image (rendered from the uploaded drawing PDF on save).
    public byte[]? DiagramImage { get; set; }
    public string? DiagramMime { get; set; }
    public int DiagramScalePct { get; set; } = 100;   // display size, independent of the crop itself

    // The whole uploaded drawing page (title block, BOM tables, everything) — separate
    // from the small cropped DiagramImage embedded on the sheet, so a reviewer can open
    // the complete original drawing at full size.
    public byte[]? FullDrawingImage { get; set; }
    public string? FullDrawingMime { get; set; }

    // Research's Document Control stamp, baked onto a copy of the drawing (the fields
    // above stay unstamped, so one sheet can be re-stamped without needing the original
    // re-uploaded). When present, viewers see this instead of the plain drawing.
    public byte[]? StampedDrawingImage { get; set; }

    // The actual PDF bytes, kept only for sheets Research creates — Research only reads
    // Part No + Section, so Planning needs the real file later to pull the rest of the
    // fields (Drawing No, Qty, Material) via the normal calibration, without a re-upload.
    public byte[]? OriginalPdf { get; set; }
    public bool ConvertedToProcess { get; set; }
    public int? SentToQualityUserId { get; set; }   // sheet stays right where it is — this just tracks who it was sent to for review
    public int? AssignedToUserId { get; set; }   // who's responsible for this specific sheet — set by a coordinator distributing work, without moving the sheet out of its batch
    public int? CompliedByUserId { get; set; }   // whoever actually converted this drawing
    public double StampNormX { get; set; } = 0.03;
    public double StampNormY { get; set; } = 0.05;
    public bool StampAnchorRight { get; set; } = true;
    public bool StampAnchorBottom { get; set; } = true;
    public double StampWidthPct { get; set; } = 22;
    public double StampHeightPct { get; set; } = 9;
    public double StampFontScale { get; set; } = 1.0;
    public string? StampSector { get; set; }
    public string? StampDate { get; set; }
    public string? StampIssue { get; set; }
    public string? StampCopyNo { get; set; }

    // Cost-center overrides for the fixed rows every sheet has (Material, Final Insp, Final Store).
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string? CustomMaterialCostCenter { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string? CustomIncomingInspCostCenter { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string? CustomFinalInspCostCenter { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string? CustomFinalStoreCostCenter { get; set; }

    // The fabrication operations the user selected (inspections are generated, not stored).
    public List<ProcessOperation> Operations { get; set; } = new();
}