using System.ComponentModel.DataAnnotations;

namespace TowerProcessSheet.Models;

/// <summary>Top-level contract that groups batches and drives contract no / work order / quantity.</summary>
public class Contract
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string ContractNo { get; set; } = string.Empty;   // e.g. "18/26"

    [StringLength(40)]
    public string? WorkOrderNo { get; set; }                  // e.g. "413/256/1"

    [StringLength(100)]
    public string? ProjectName { get; set; }

    public int QtyMultiplier { get; set; } = 1;               // Total Qty = sheet Qty * this

    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<Batch> Batches { get; set; } = new();
}
