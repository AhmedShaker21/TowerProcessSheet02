using System.ComponentModel.DataAnnotations;

namespace TowerProcessSheet.Models;

/// <summary>Which box on the sheet a person's stamp fills (their function).</summary>
public enum SignOffSlot
{
    Operator = 0,
    Inspector = 1,
    QualityInspector = 2,
    CompiledBy = 3,
    CheckedBy = 4,
    ApprovedBy = 5,
}

public enum BatchStatus
{
    Draft = 0,       // being assembled, not yet sent for review
    InReview = 1,    // sitting with a reviewer
    Approved = 2,    // fully signed off
    Rejected = 3,    // sent back with rejections
}

public enum ReviewDecision
{
    Accepted = 0,
    Rejected = 1,
}

/// <summary>A person who logs in, reviews sheets and stamps them.</summary>
public class AppUser
{
    public int Id { get; set; }

    [Required, StringLength(60)]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string FullName { get; set; } = string.Empty;

    [StringLength(80)]
    public string? Department { get; set; }

    public SignOffSlot Slot { get; set; } = SignOffSlot.Operator;

    // Short code shown in the stamp oval, e.g. "HAF 59".
    [StringLength(20)]
    public string? StampCode { get; set; }

    // For the big "Document Control" box (Quality slot): sector line + issue number.
    [StringLength(60)]
    public string? Sector { get; set; }          // e.g. "R & D Sector" / "Production Sector"
    [System.ComponentModel.DataAnnotations.StringLength(9)]
    public string DocControlColor { get; set; } = "#e11111";   // this user's own Document-Control stamp color

    // Where this user's AUTO-GENERATED Document Control stamp goes (Research's split &
    // send flow) — set once by dragging it on a sample sheet, then reused every time.
    public double AutoStampNormX { get; set; } = 0.03;
    public double AutoStampNormY { get; set; } = 0.05;
    public bool AutoStampAnchorRight { get; set; } = true;
    public bool AutoStampAnchorBottom { get; set; } = true;
    public double AutoStampWidthPct { get; set; } = 22;
    public double AutoStampHeightPct { get; set; } = 9;
    public double AutoStampFontScale { get; set; } = 1.0;

    // Custom text shown on the sheet footer when this user signs as Complied-by / Checked
    // (falls back to FullName if left blank).
    public string? CompliedByName { get; set; }
    public string? CheckedByName { get; set; }
    [StringLength(20)]
    public string? DocIssue { get; set; }         // e.g. "1"

    public bool IsAdmin { get; set; }
    public bool IsPlanner { get; set; }   // may create batches / bulk-import sheets
    public bool IsCoordinator { get; set; }    // may reassign a batch to a different planner
    public bool IsSectorManager { get; set; }  // read-only cross-team workload dashboard
    public bool IsAngleWorkshop { get; set; }  // Research sends every Angle group to whoever has this on
    public bool IsPlateWorkshop { get; set; }  // Research sends every Plate group to whoever has this on
    public DateTime? LastSeenInboxAt { get; set; }  // for the "new in your inbox" notification
    public bool IsDisplay { get; set; }   // wall-screen account: sees the live pipeline only
    public bool IsActive { get; set; } = true;

    public string PasswordHash { get; set; } = string.Empty;

    // The user's stamp/seal image, uploaded once (ideally a transparent PNG).
    public byte[]? StampImage { get; set; }
    public string? StampMime { get; set; }

    // Scanned handwritten signature — separate from the stamp; a user may have either or both.
    public byte[]? SignatureImage { get; set; }
    public string? SignatureMime { get; set; }

    // Saved template for stamps that carry Date / Issue / Copy No. fields:
    // JSON {"hasFields":true,"size":8,"fields":[{"k":"date","x":0.3,"y":0.6},...]} — positions normalized to the stamp image.
    public string? StampFieldsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool HasStamp => StampImage != null && StampImage.Length > 0;
    public bool HasSignature => SignatureImage != null && SignatureImage.Length > 0;
}

/// <summary>One of a user's saved signatures — a profile can hold several (e.g. two different pens/scans).</summary>
public class UserSignature
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }

    [System.ComponentModel.DataAnnotations.StringLength(60)]
    public string? Label { get; set; }   // e.g. "Signature 1", "Official"

    public byte[] Image { get; set; } = Array.Empty<byte>();
    public string? Mime { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>A department/administration the admin can manage (beyond Planning & Operation).</summary>
public class Department
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(80)]
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>The last-used drawing calibration (which field sits where on the page).
/// Saved once, then reused automatically for every future import — no re-marking
/// unless the drawing layout genuinely changes and the user chooses to recalibrate.</summary>
public class SavedCalibration
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(80)]
    public string Name { get; set; } = "Default";     // lets more than one layout be remembered later
    public string RectsJson { get; set; } = "{}";      // field key -> [x, y, w, h] normalized
    [System.ComponentModel.DataAnnotations.StringLength(40)]
    public string? NameField { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

// One fixed spot per signature, shared across the whole system — set once, applied to
// A named folder grouping several batches together (e.g. all the Angle + Plate batches
// that belong to the same real-world project) — purely organizational.
public class Project
{
    public int Id { get; set; }
    [Required, StringLength(100)]
    public string Name { get; set; } = "";
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// The reference Part List PDF (Tekla "PART LIST": ITEM / QTY / SECTION / WIDTH / LENGTH /
// POS / WEIGHT / ST. GR.) for a project — at most one per project. Kept in its own table
// rather than on Project so the Projects page, which loads every project row, never drags
// the PDF bytes along with it.
public class ProjectPartList
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    [Required, StringLength(260)]
    public string FileName { get; set; } = "";
    public byte[] Pdf { get; set; } = Array.Empty<byte>();
    [StringLength(120)]
    public string? UploadedByName { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
}

// A Part List row ticked by hand — for rows the automatic check couldn't match (the sheet
// is named differently, the drawing came another way, ...). RowKey is the row's stable id
// from the reader (its normalized POS), so the tick survives re-uploading the same list.
public class PartListManualMark
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    [Required, StringLength(120)]
    public string RowKey { get; set; } = "";
    [StringLength(120)]
    public string? MarkedByName { get; set; }
    public DateTime MarkedAt { get; set; } = DateTime.Now;
}

// Archive of every past Contract Notification No. / Work Order No. a batch has ever
// had — a new row is added here (not overwritten) every time Programs & Operation
// changes an already-finalized batch's numbers, so the old numbers and exactly when
// they were active stay on record permanently.
public class ProgramsFinalizeHistory
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public string BatchName { get; set; } = "";      // snapshot, in case the batch is renamed/removed later
    public string? ContractNotificationNo { get; set; }
    public string? WorkOrderNo { get; set; }
    public DateTime ActiveFrom { get; set; }
    public DateTime? ActiveTo { get; set; }           // null = these are the current, still-active numbers
}

// One fixed spot per signature, shared across the whole system — set once, applied to
// every converted process sheet from then on. There's only ever one row of this.
public class SignatureSettings
{
    public int Id { get; set; }
    // NormX/NormY measure distance from whichever edge is anchored — bottom-anchored by
    // default, since the footer (where all four signatures live) sits a fixed distance
    // from the BOTTOM of the card, not the top. The operations table above it grows
    // taller with more Steps, which would otherwise push a top-anchored position further
    // down than intended on any sheet with a different number of rows than the sample
    // used while setting the position.
    public double CheckedByNormX { get; set; } = 0.26;
    public double CheckedByNormY { get; set; } = 0.10;
    public double CheckedByWidthPct { get; set; } = 12;
    public bool CheckedByAnchorRight { get; set; } = false;
    public bool CheckedByAnchorBottom { get; set; } = true;

    public double CompliedByNormX { get; set; } = 0.10;
    public double CompliedByNormY { get; set; } = 0.10;
    public double CompliedByWidthPct { get; set; } = 12;
    public bool CompliedByAnchorRight { get; set; } = false;
    public bool CompliedByAnchorBottom { get; set; } = true;

    public double ApprovedNormX { get; set; } = 0.02;
    public double ApprovedNormY { get; set; } = 0.10;
    public double ApprovedWidthPct { get; set; } = 12;
    public bool ApprovedAnchorRight { get; set; } = true;
    public bool ApprovedAnchorBottom { get; set; } = true;

    public double WrittenByNormX { get; set; } = 0.38;
    public double WrittenByNormY { get; set; } = 0.10;
    public double WrittenByWidthPct { get; set; } = 12;
    public bool WrittenByAnchorRight { get; set; } = false;
    public bool WrittenByAnchorBottom { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>An admin-defined process step added to the operation catalog (COST. CENT / Setting / TIME ALL'D).</summary>
public class CustomStep
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(80)]
    public string Name { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string CostCenter { get; set; } = "";
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string Setting { get; set; } = "";
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string TimeAllowed { get; set; } = "";
    [System.ComponentModel.DataAnnotations.StringLength(400)]
    public string Description { get; set; } = "";   // shown on the sheet; may use {length} {project} {drawing}
    public bool AddInspection { get; set; } = true; // add an INSP. row after this step
}

/// <summary>Admin-managed cost-center registry — a planner picks a name in the dropdown,
/// but only the number is ever stored/printed on the sheet.</summary>
public class CostCenterEntry
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(20)]
    public string Number { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(80)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>A group of process sheets that move through review together.</summary>
public class Batch
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string Name { get; set; } = string.Empty;      // batch number / label

    [StringLength(100)]
    public string? ProjectName { get; set; }

    // Set on a rejected-and-returned draft: this batch may only be sent to the one
    // reviewer who rejected it — the source must fix and resend, not redirect elsewhere.
    public int? RestrictedRecipientId { get; set; }
    public AppUser? RestrictedRecipient { get; set; }

    // Research's "split & send" staging: while Research is reviewing a batch of
    // freshly-split drawings, it's owned by Research (invisible to the real recipient)
    // and tagged with the session token + who it's really headed to. Confirming the
    // whole session reassigns every batch in it to its real owner and sends it for real.
    public string? ResearchSessionToken { get; set; }
    public int? ResearchIntendedOwnerId { get; set; }
    public AppUser? ResearchIntendedOwner { get; set; }
    public bool ResearchSent { get; set; }

    // Optional parent contract that drives contract no / work order / quantity.
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    // Convert-to-Process needs its own layout marking per batch of incoming drawings —
    // a delivery from Research (or anywhere else external) can use a different drawing
    // layout than the last one, so this is captured once per batch, not system-wide.
    public string? ConvertRectsJson { get; set; }
    public string? ConvertNameField { get; set; }

    // Optional grouping — several batches (e.g. an Angle batch and a Plate batch) that
    // all belong to the same named project, so they can be reviewed together.
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    // Whether this batch's project Part List has been confirmed as sent along with it, as a
    // reference for Quality — set by Research when it attaches one, or by whoever sends the
    // sheets on to Quality. The file itself lives once per project (ProjectPartList).
    public bool PartListShared { get; set; }

    // Batch-level values applied to every sheet in the batch.
    [StringLength(40)]
    public string? ContractNo { get; set; }        // e.g. "18/26"
    [StringLength(40)]
    public string? WorkOrderNo { get; set; }        // required when ContractNo is set, e.g. "413/256/1"
    public int QtyMultiplier { get; set; } = 1;     // Total Qty = sheet Qty * this

    // Batch-level header/footer values, entered once and copied to every sheet.
    [StringLength(30)] public string? AssembledOn { get; set; }
    [StringLength(80)] public string? QualityInsp { get; set; }
    [StringLength(20)] public string? SheetNo { get; set; } = "1/1";
    [StringLength(80)] public string? WrittenBy { get; set; }
    [StringLength(20)] public string? NoOfSheets { get; set; } = "1";
    [StringLength(20)] public string? ProcessSheetIssue { get; set; } = "1";
    [StringLength(20)] public string? DrawingIssue { get; set; } = "1";
    [StringLength(40)] public string? DocRef { get; set; } = "E/206/401/ن";

    // Planning "Checked by" signer (optional).
    public int? CheckedById { get; set; }
    public AppUser? CheckedBy { get; set; }

    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public BatchStatus Status { get; set; } = BatchStatus.Draft;

    // Who currently must act on the batch (null once fully approved).
    public int? CurrentHolderId { get; set; }
    public AppUser? CurrentHolder { get; set; }

    public List<ProcessSheet> Sheets { get; set; } = new();
    public List<BatchTransfer> Transfers { get; set; } = new();
    public List<StampPlacement> Placements { get; set; } = new();
}

/// <summary>A stamp/signature placed at an exact spot on the sheet, applied to every sheet in the batch.</summary>
public class StampPlacement
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    // Position is measured as an exact gap (% of sheet width/height) from a chosen
    // corner — not a center point on the whole page — so the same stamp lands in the
    // same spot relative to that edge even when sheets differ in total length.
    public double NormX { get; set; }         // % gap from the anchored horizontal edge (0..1)
    public double NormY { get; set; }         // % gap from the anchored vertical edge (0..1)
    public bool AnchorRight { get; set; }     // false = measured from the left edge, true = from the right
    public bool AnchorBottom { get; set; }    // false = measured from the top edge, true = from the bottom
    public double WidthPct { get; set; } = 12; // width as % of sheet width

    // Optional: anchor to a specific word/phrase instead of a page corner. When set, this
    // takes priority — the stamp is found next to that exact text on every sheet, which
    // stays correct even when sheets differ in length (the corner fields above become just
    // a fallback position for exports that can't run the browser search).
    [StringLength(120)]
    public string? AnchorText { get; set; }
    public double OffsetXPx { get; set; }     // pixels to the right of the found text (negative = left)
    public double OffsetYPx { get; set; }     // pixels below the found text (negative = above)

    // Normally null, meaning this placement applies to every sheet in the batch. Set
    // this to one specific sheet's Id when just that one sheet needed its stamp
    // "reallocated" — a per-sheet override that replaces the batch-wide placement for
    // that sheet only, leaving every other sheet using the shared one.
    public int? ProcessSheetId { get; set; }

    public byte[]? Image { get; set; }        // snapshot of the placed image
    public string? Mime { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}


/// <summary>One hop in a batch's journey (created / sent / approved / rejected).</summary>
public class BatchTransfer
{
    public int Id { get; set; }

    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public int? FromUserId { get; set; }
    public AppUser? FromUser { get; set; }

    public int? ToUserId { get; set; }
    public AppUser? ToUser { get; set; }

    [StringLength(40)]
    public string Action { get; set; } = "Sent";  // Created / Sent / Approved / Rejected

    [StringLength(500)]
    public string? Note { get; set; }

    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>A reviewer's decision on a single sheet, carrying their stamp snapshot.</summary>
public class SheetReview
{
    public int Id { get; set; }

    public int ProcessSheetId { get; set; }
    public ProcessSheet? ProcessSheet { get; set; }

    public int ReviewerId { get; set; }
    public AppUser? Reviewer { get; set; }

    // Slot the stamp lands in (snapshot of the reviewer's function at the time).
    public SignOffSlot Slot { get; set; }

    public ReviewDecision Decision { get; set; }

    [StringLength(500)]
    public string? Comment { get; set; }   // required when Rejected

    // Snapshot of the stamp + name so history is stable even if the user changes theirs.
    public byte[]? StampImage { get; set; }
    public string? StampMime { get; set; }
    [StringLength(9)]
    public string? DocControlColor { get; set; }   // e.g. "#e11111" — the Quality user's own stamp color

    // Reviewer's mark-up (e.g. a circle on the part no) when rejecting — feedback only,
    // never rendered on the clean/resent sheet.
    public byte[]? AnnotationImage { get; set; }
    public string? AnnotationMime { get; set; }

    [StringLength(120)]
    public string? ReviewerName { get; set; }

    [StringLength(20)]
    public string? ReviewerCode { get; set; }

    // Snapshot for the Document Control box (Quality slot).
    [StringLength(60)]
    public string? DocSector { get; set; }
    [StringLength(20)]
    public string? DocIssue { get; set; }

    public DateTime ReviewedAt { get; set; } = DateTime.Now;
}
