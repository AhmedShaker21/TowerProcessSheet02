using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TowerProcessSheet.Models;

/// <summary>
/// A fabrication operation the user picked for a sheet (e.g. Cutting &amp; Drilling,
/// Cutting, Milling). Inspections, material, galvanizing and final steps are
/// generated automatically and are NOT stored here.
/// </summary>
public class ProcessOperation
{
    public int Id { get; set; }

    [Required]
    [StringLength(40)]
    public string OperationKey { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    // If set, overrides the catalog's default value for this occurrence only
    // (the employee flagged that this particular sheet needs something different).
    [StringLength(20)]
    public string? CustomTimeAllowed { get; set; }
    [StringLength(20)]
    public string? CustomCostCenter { get; set; }
    [StringLength(20)]
    public string? CustomSetting { get; set; }
    [StringLength(400)]
    public string? CustomDescription { get; set; }
    [StringLength(20)]
    public string? CustomInspCostCenter { get; set; }  // cost center for the INSP row right after this operation

    public int ProcessSheetId { get; set; }

    [ForeignKey(nameof(ProcessSheetId))]
    public ProcessSheet? ProcessSheet { get; set; }
}
