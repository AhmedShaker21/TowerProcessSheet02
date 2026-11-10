using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;

namespace TowerProcessSheet.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;
    public AdminController(AppDbContext db) => _db = db;

    // ---- The big interactive dashboard ----
    public async Task<IActionResult> Index()
    {
        ViewBag.UserCount = await _db.Users.CountAsync(u => u.IsActive);
        ViewBag.BatchCount = await _db.Batches.CountAsync();
        ViewBag.SheetCount = await _db.ProcessSheets.CountAsync();
        ViewBag.ActiveCount = await _db.Batches.CountAsync(b => b.Status == BatchStatus.InReview);
        ViewBag.ApprovedCount = await _db.Batches.CountAsync(b => b.Status == BatchStatus.Approved);
        ViewBag.RejectedSheets = await _db.SheetReviews
            .Where(r => r.Decision == ReviewDecision.Rejected).Select(r => r.ProcessSheetId).Distinct().CountAsync();

        // Deleting rows (e.g. Delete Project/Delete Batch) never shrinks the actual .db
        // file on disk by itself — SQLite just marks the freed pages as reusable inside
        // the file. Shown here so it's obvious when a VACUUM is actually worth running.
        try
        {
            var dbPath = _db.Database.IsSqlite() ? _db.Database.GetDbConnection().DataSource : null;
            if (!string.IsNullOrWhiteSpace(dbPath) && System.IO.File.Exists(dbPath))
                ViewBag.DbFileSizeMb = Math.Round(new System.IO.FileInfo(dbPath).Length / (1024.0 * 1024.0), 1);
        }
        catch { }

        ViewBag.Users = await _db.Users.OrderBy(u => u.FullName).ToListAsync();

        var batches = await _db.Batches
            .Include(b => b.CurrentHolder).Include(b => b.CreatedBy)
            .OrderByDescending(b => b.CreatedAt).Take(40).ToListAsync();
        var counts = await _db.ProcessSheets.Where(s => s.BatchId != null)
            .GroupBy(s => s.BatchId!.Value)
            .Select(g => new { BatchId = g.Key, Count = g.Count() }).ToListAsync();
        ViewBag.Counts = counts.ToDictionary(x => x.BatchId, x => x.Count);
        return View(batches);
    }

    // ---- Actually shrinks the .db file on disk, reclaiming space left behind by
    // deletions (Delete Project/Delete Batch, or any other row removal) — SQLite never
    // does this on its own. Needs roughly as much free disk space as the database's
    // current size to rebuild it, so if the disk is genuinely at zero, free some space
    // manually first (e.g. delete unrelated files on the same drive) before this can run. ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VacuumDatabase()
    {
        if (!_db.Database.IsSqlite())
        {
            TempData["Msg"] = "Not needed — SQL Server manages its own file space.";
            return RedirectToAction("Index");
        }
        try
        {
            await _db.Database.ExecuteSqlRawAsync("VACUUM;");
            TempData["Msg"] = "Database compacted — the .db file has been shrunk on disk.";
        }
        catch (Exception ex)
        {
            TempData["Err"] = "VACUUM failed: " + ex.Message + " — if the disk has no free space at all, VACUUM needs room to rebuild the file, so free some space manually first.";
        }
        return RedirectToAction("Index");
    }

    // ---- Cost centers (dropdown registry: planner picks the name, only the number is stored) ----
    public async Task<IActionResult> CostCenters() =>
        View(await _db.CostCenters.OrderBy(c => c.Number).ToListAsync());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCostCenter(string number, string name)
    {
        if (!string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(name)
            && !await _db.CostCenters.AnyAsync(c => c.Number == number.Trim()))
        {
            _db.CostCenters.Add(new CostCenterEntry { Number = number.Trim(), Name = name.Trim() });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction("CostCenters");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCostCenter(int id)
    {
        var c = await _db.CostCenters.FindAsync(id);
        if (c != null) { _db.CostCenters.Remove(c); await _db.SaveChangesAsync(); }
        return RedirectToAction("CostCenters");
    }

    // ---- Departments ----
    public async Task<IActionResult> Departments() =>
        View(await _db.Departments.OrderBy(d => d.Name).ToListAsync());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDepartment(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !await _db.Departments.AnyAsync(d => d.Name == name.Trim()))
        {
            _db.Departments.Add(new Department { Name = name.Trim() });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction("Departments");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDepartment(int id)
    {
        var d = await _db.Departments.FindAsync(id);
        if (d != null) { _db.Departments.Remove(d); await _db.SaveChangesAsync(); }
        return RedirectToAction("Departments");
    }

    // ---- Process steps (added to the operation catalog) ----
    public async Task<IActionResult> Steps() =>
        View(await _db.CustomSteps.OrderBy(s => s.Id).ToListAsync());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStep(string name, string? costCenter, string? setting,
        string? timeAllowed, string? description, bool addInspection)
    {
        if (string.IsNullOrWhiteSpace(name)) return RedirectToAction("Steps");
        _db.CustomSteps.Add(new CustomStep
        {
            Name = name.Trim(),
            CostCenter = costCenter?.Trim() ?? "",
            Setting = setting?.Trim() ?? "",
            TimeAllowed = timeAllowed?.Trim() ?? "",
            Description = description?.Trim() ?? "",
            AddInspection = addInspection,
        });
        await _db.SaveChangesAsync();
        DbSeeder.SyncCatalog(_db);
        TempData["Msg"] = "Step added to the catalog.";
        return RedirectToAction("Steps");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteStep(int id)
    {
        var s = await _db.CustomSteps.FindAsync(id);
        if (s != null) { _db.CustomSteps.Remove(s); await _db.SaveChangesAsync(); DbSeeder.SyncCatalog(_db); }
        return RedirectToAction("Steps");
    }
}