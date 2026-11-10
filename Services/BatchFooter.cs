using TowerProcessSheet.Models;

namespace TowerProcessSheet.Services;

/// <summary>Copies batch-level header/footer values onto a sheet.</summary>
public static class BatchFooter
{
    public static void ApplyTo(Batch batch, ProcessSheet s)
    {
        s.ContractNo = batch.ContractNo;
        s.WorkOrderNo = batch.WorkOrderNo;
        // Only fill Total Qty when the planner actually set a multiplier (> 1);
        // otherwise leave it blank, like before.
        s.TotalQty = batch.QtyMultiplier > 1 ? (s.Qty * batch.QtyMultiplier).ToString() : null;
        s.AssembledOn = batch.AssembledOn;
        s.QualityInsp = batch.QualityInsp;
        s.SheetNo = batch.SheetNo ?? "1/1";
        s.WrittenBy = batch.WrittenBy;
        s.NoOfSheets = batch.NoOfSheets ?? "1";
        s.ProcessSheetIssue = batch.ProcessSheetIssue ?? "1";
        s.DrawingIssue = batch.DrawingIssue ?? "1";
        s.DocRef = batch.DocRef ?? "E/206/401/ن";
    }
}
