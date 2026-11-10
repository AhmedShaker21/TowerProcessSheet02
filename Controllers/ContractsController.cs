using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;
using TowerProcessSheet.Services;

namespace TowerProcessSheet.Controllers;

[Authorize(Roles = "Planner,Admin")]
public class ContractsController : Controller
{
    private readonly AppDbContext _db;
    public ContractsController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var contracts = await _db.Contracts
            .Include(c => c.Batches)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        return View(contracts);
    }

    [HttpGet]
    public IActionResult Create() => View(new Contract());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string contractNo, string? workOrderNo, string? projectName, int qtyMultiplier)
    {
        if (string.IsNullOrWhiteSpace(contractNo))
        {
            ModelState.AddModelError("", "Contract No is required.");
            return View(new Contract());
        }
        var contract = new Contract
        {
            ContractNo = contractNo.Trim(),
            WorkOrderNo = workOrderNo?.Trim(),
            ProjectName = projectName?.Trim(),
            QtyMultiplier = qtyMultiplier < 1 ? 1 : qtyMultiplier,
            CreatedById = User.GetUserId(),
            CreatedAt = DateTime.Now,
        };
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync();
        return RedirectToAction("Edit", new { id = contract.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var contract = await _db.Contracts.Include(c => c.Batches).FirstOrDefaultAsync(c => c.Id == id);
        if (contract == null) return NotFound();
        ViewBag.Candidates = await CandidateBatchesAsync(id);
        return View(contract);
    }

    // Batches the planner can attach: their own, or already on this contract (admin sees all).
    private async Task<List<Batch>> CandidateBatchesAsync(int contractId)
    {
        int me = User.GetUserId();
        var q = _db.Batches.Include(b => b.CurrentHolder).AsQueryable();
        if (!User.IsAdmin())
            q = q.Where(b => b.CreatedById == me || b.ContractId == contractId);
        return await q.OrderByDescending(b => b.CreatedAt).ToListAsync();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string contractNo, string? workOrderNo, string? projectName,
        int qtyMultiplier, List<int>? batchIds)
    {
        var contract = await _db.Contracts.FindAsync(id);
        if (contract == null) return NotFound();
        if (string.IsNullOrWhiteSpace(contractNo))
        {
            TempData["Err"] = "Contract No is required.";
            return RedirectToAction("Edit", new { id });
        }

        contract.ContractNo = contractNo.Trim();
        contract.WorkOrderNo = workOrderNo?.Trim();
        contract.ProjectName = projectName?.Trim();
        contract.QtyMultiplier = qtyMultiplier < 1 ? 1 : qtyMultiplier;

        var selected = batchIds ?? new List<int>();
        var candidates = await CandidateBatchesAsync(id);
        foreach (var b in candidates)
        {
            bool wasHere = b.ContractId == id;
            bool nowHere = selected.Contains(b.Id);
            if (nowHere)
            {
                b.ContractId = id;
            }
            else if (wasHere)
            {
                // Removed from contract: clear contract-driven values.
                b.ContractId = null;
                b.ContractNo = null;
                b.WorkOrderNo = null;
                b.QtyMultiplier = 1;
                var sheets = await _db.ProcessSheets.Where(s => s.BatchId == b.Id).ToListAsync();
                foreach (var s in sheets) BatchFooter.ApplyTo(b, s);
            }
        }
        await _db.SaveChangesAsync();

        await PropagateContractAsync(contract);   // push contract values onto all its batches + sheets
        await _db.SaveChangesAsync();

        TempData["Msg"] = "Contract saved and applied to every sheet in its batches.";
        return RedirectToAction("Edit", new { id });
    }

    private async Task PropagateContractAsync(Contract c)
    {
        var batches = await _db.Batches.Where(b => b.ContractId == c.Id).ToListAsync();
        foreach (var b in batches)
        {
            b.ContractNo = c.ContractNo;
            b.WorkOrderNo = c.WorkOrderNo;
            b.QtyMultiplier = c.QtyMultiplier;
            if (!string.IsNullOrWhiteSpace(c.ProjectName)) b.ProjectName = c.ProjectName;

            var sheets = await _db.ProcessSheets.Where(s => s.BatchId == b.Id).ToListAsync();
            foreach (var s in sheets) BatchFooter.ApplyTo(b, s);
        }
    }
}
