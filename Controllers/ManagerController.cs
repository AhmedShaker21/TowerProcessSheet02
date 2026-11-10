using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;

namespace TowerProcessSheet.Controllers;

[Authorize(Roles = "SectorManager,Coordinator,Admin")]
public class ManagerController : Controller
{
    private readonly AppDbContext _db;
    public ManagerController(AppDbContext db) => _db = db;

    public class PlannerRow
    {
        public string Name { get; set; } = "";
        public int TotalSheets { get; set; }
        public int NotStarted { get; set; }     // still sitting in a draft batch
        public int SentToQuality { get; set; }  // batch has reached a Quality-slot user at least once
        public int Finished { get; set; }       // batch fully approved
        public int ReceivedFromResearch { get; set; }  // drawings that came in from Research, needing conversion
        public int Converted { get; set; }             // of those, how many are now real process sheets
    }

    public class QualityRow
    {
        public string Name { get; set; } = "";
        public int Received { get; set; }       // distinct sheets ever sent to them
        public int Reviewed { get; set; }       // sheets they've made a decision on
        public int Approved { get; set; }
        public List<string> ReceivedFrom { get; set; } = new();  // which planners' work they've seen
    }

    public async Task<IActionResult> Index()
    {
        if (!User.IsInRole("SectorManager") && !User.IsInRole("Admin"))
        {
            int myUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int uid) ? uid : 0;
            var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == myUserId);
            if (me == null || me.Department == "Research") return Forbid();
        }

        var planners = await _db.Users.Where(u => u.IsPlanner).OrderBy(u => u.FullName).ToListAsync();
        var qualityUsers = await _db.Users.Where(u => u.Slot == SignOffSlot.QualityInspector).OrderBy(u => u.FullName).ToListAsync();

        var plannerRows = new List<PlannerRow>();
        foreach (var p in planners)
        {
            var batchIds = await _db.Batches.Where(b => b.CreatedById == p.Id).Select(b => b.Id).ToListAsync();
            int total = await _db.ProcessSheets.CountAsync(s => s.BatchId != null && batchIds.Contains(s.BatchId.Value));
            int notStarted = await _db.ProcessSheets.CountAsync(s => s.BatchId != null && batchIds.Contains(s.BatchId.Value)
                && _db.Batches.Any(b => b.Id == s.BatchId && b.Status == BatchStatus.Draft));
            int finished = await _db.ProcessSheets.CountAsync(s => s.BatchId != null && batchIds.Contains(s.BatchId.Value)
                && _db.Batches.Any(b => b.Id == s.BatchId && b.Status == BatchStatus.Approved));
            var qualityIds = qualityUsers.Select(q => q.Id).ToHashSet();
            var sentBatchIds = await _db.BatchTransfers
                .Where(t => batchIds.Contains(t.BatchId) && t.ToUserId != null && qualityIds.Contains(t.ToUserId.Value))
                .Select(t => t.BatchId).Distinct().ToListAsync();
            int sentToQuality = await _db.ProcessSheets.CountAsync(s => s.BatchId != null && sentBatchIds.Contains(s.BatchId.Value));

            int receivedFromResearch = await _db.ProcessSheets.CountAsync(s => s.OriginalPdf != null
                && ((s.BatchId != null && batchIds.Contains(s.BatchId.Value)) || s.AssignedToUserId == p.Id));
            int converted = await _db.ProcessSheets.CountAsync(s => s.OriginalPdf != null && s.ConvertedToProcess
                && ((s.BatchId != null && batchIds.Contains(s.BatchId.Value)) || s.AssignedToUserId == p.Id));

            plannerRows.Add(new PlannerRow
            {
                Name = p.FullName, TotalSheets = total, NotStarted = notStarted,
                SentToQuality = sentToQuality, Finished = finished,
                ReceivedFromResearch = receivedFromResearch, Converted = converted,
            });
        }

        var qualityRows = new List<QualityRow>();
        foreach (var q in qualityUsers)
        {
            var receivedBatchIds = await _db.BatchTransfers
                .Where(t => t.ToUserId == q.Id).Select(t => t.BatchId).Distinct().ToListAsync();
            int received = await _db.ProcessSheets.CountAsync(s => s.BatchId != null && receivedBatchIds.Contains(s.BatchId.Value));
            int reviewed = await _db.SheetReviews.CountAsync(r => r.ReviewerId == q.Id && r.Slot == SignOffSlot.QualityInspector);
            int approved = await _db.SheetReviews.CountAsync(r => r.ReviewerId == q.Id && r.Slot == SignOffSlot.QualityInspector && r.Decision == ReviewDecision.Accepted);
            var fromNames = await _db.Batches.Where(b => receivedBatchIds.Contains(b.Id))
                .Select(b => b.CreatedBy!.FullName).Distinct().ToListAsync();

            qualityRows.Add(new QualityRow
            {
                Name = q.FullName, Received = received, Reviewed = reviewed, Approved = approved,
                ReceivedFrom = fromNames!,
            });
        }

        ViewBag.Planners = plannerRows;
        ViewBag.Quality = qualityRows;
        return View();
    }
}
