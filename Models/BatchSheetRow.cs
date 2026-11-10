namespace TowerProcessSheet.Models;

/// <summary>Lightweight row for listing sheets inside a batch.</summary>
public class BatchSheetRow
{
    public int Id { get; set; }
    public string DrawingNo { get; set; } = "";
    public string PartNo { get; set; } = "";
    public string Section { get; set; } = "";
    public bool HasDrawing { get; set; }
    public bool NeedsConversion { get; set; }
    public bool IsConvertedFromResearch { get; set; }   // came from Research AND has been converted
    public bool IsQualityRejected { get; set; }          // latest Quality review on this sheet was a rejection
    public bool WasResentAfterRejection { get; set; }    // rejected, then fixed and resent - awaiting Quality's re-check
    public int? SiblingConvertedSheetId { get; set; }     // another destination's copy of this same drawing, already converted
}

/// <summary>A rejection to show in the batch's rejected-sheets panel.</summary>
public class RejectionRow
{
    public int SheetId { get; set; }
    public string Drawing { get; set; } = "";
    public string? ReviewerName { get; set; }
    public SignOffSlot Slot { get; set; }
    public string? Comment { get; set; }
    public DateTime At { get; set; }
}

/// <summary>All sheets of a batch, for the combined export/print view.</summary>
public class BatchExportVm
{
    public Batch Batch { get; set; } = null!;
    public List<ProcessSheet> Sheets { get; set; } = new();
}

/// <summary>One reviewer's activity on a batch, for the pipeline history.</summary>
public class PipelineStage
{
    public string? Name { get; set; }
    public SignOffSlot Slot { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public DateTime LastAt { get; set; }
}