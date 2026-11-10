using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;
using TowerProcessSheet.Services;

namespace TowerProcessSheet.Controllers;

[Authorize]
public class BatchesController : Controller
{
    private const int PageSize = 50;
    private readonly AppDbContext _db;
    private readonly Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine _viewEngine;
    private readonly Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider _tempDataProvider;
    public BatchesController(AppDbContext db,
        Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine viewEngine,
        Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider tempDataProvider)
    {
        _db = db; _viewEngine = viewEngine; _tempDataProvider = tempDataProvider;
    }

    // Renders a view to an HTML string instead of as the HTTP response — used to build one
    // Word-compatible file per sheet for a zip download, reusing the exact same route-card
    // markup the normal "View all / PDF" export already uses.
    private async Task<string> RenderViewToStringAsync(string viewName, object model)
    {
        var viewResult = _viewEngine.FindView(ControllerContext, viewName, false);
        if (viewResult.View == null) throw new InvalidOperationException($"View '{viewName}' not found.");

        var viewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), ModelState) { Model = model };
        var tempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(HttpContext, _tempDataProvider);
        using var sw = new StringWriter();
        var viewContext = new ViewContext(ControllerContext, viewResult.View, viewData, tempData, sw, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return sw.ToString();
    }

    private Task<AppUser?> MeAsync() => _db.Users.FirstOrDefaultAsync(u => u.Id == User.GetUserId());

    // ---- Inbox + lists ----
    public async Task<IActionResult> Index()
    {
        int me = User.GetUserId();
        ViewBag.Inbox = await _db.Batches
            .Include(b => b.CreatedBy)
            .Where(b => b.CurrentHolderId == me && b.Status == BatchStatus.InReview)
            // If this person has sheets specifically assigned to them in this batch
            // (the "Assign whole batch to" handoff, not the standard sign-off pipeline),
            // only keep counting it as "waiting" while some of those are still
            // unconverted — once they've finished their own share, it should stop
            // showing up here even though the batch's Status field itself never changes.
            .Where(b => !_db.ProcessSheets.Any(s => s.BatchId == b.Id && s.AssignedToUserId == me)
                || _db.ProcessSheets.Any(s => s.BatchId == b.Id && s.AssignedToUserId == me && !s.ConvertedToProcess))
            .OrderBy(b => b.CreatedAt).ToListAsync();

        ViewBag.Mine = await _db.Batches
            .Include(b => b.CurrentHolder)
            .Where(b => b.CreatedById == me)
            .OrderByDescending(b => b.CreatedAt).ToListAsync();

        // Sheets a coordinator assigned to this person specifically — these live in
        // someone else's batch (e.g. Eman Mahmoud's), so they wouldn't show up above at
        // all otherwise. Nothing needs to be "sent back" — this list is always live.
        ViewBag.AssignedSheets = await _db.ProcessSheets
            .Where(s => s.AssignedToUserId == me)
            .OrderBy(s => s.ConvertedToProcess).ThenByDescending(s => s.Id)
            .Select(s => new AssignedSheetRow
            {
                Id = s.Id,
                DrawingNo = s.DrawingNo,
                PartNo = s.PartNo,
                Section = s.Section,
                ConvertedToProcess = s.ConvertedToProcess,
                BatchId = s.BatchId,
                BatchName = s.Batch != null ? s.Batch.Name : null,
                FromName = s.Batch != null && s.Batch.CreatedBy != null ? s.Batch.CreatedBy.FullName : null,
            })
            .ToListAsync();

        // The piece "Send completed to Quality" actually needed on this side — sheets
        // never move out of the coordinator's batch, they just get marked who they were
        // sent to, so this is the only place a Quality account can actually find them.
        ViewBag.SentForQualityReview = await _db.ProcessSheets
            .Where(s => s.SentToQualityUserId == me)
            .OrderByDescending(s => s.Id)
            .Select(s => new AssignedSheetRow
            {
                Id = s.Id,
                DrawingNo = s.DrawingNo,
                PartNo = s.PartNo,
                Section = s.Section,
                ConvertedToProcess = s.ConvertedToProcess,
                BatchId = s.BatchId,
                BatchName = s.Batch != null ? s.Batch.Name : null,
                FromName = s.Batch != null && s.Batch.CreatedBy != null ? s.Batch.CreatedBy.FullName : null,
            })
            .ToListAsync();

        if (User.IsAdmin())
            ViewBag.All = await _db.Batches
                .Include(b => b.CurrentHolder).Include(b => b.CreatedBy)
                .OrderByDescending(b => b.CreatedAt).ToListAsync();

        ViewBag.ApprovedCount = await _db.Batches.CountAsync(b => b.Status == BatchStatus.Approved
            && (b.CreatedById == me || User.IsAdmin()));
        ViewBag.RejectedWaiting = await _db.SheetReviews
            .Where(r => r.Decision == ReviewDecision.Rejected && r.ProcessSheet!.Batch!.CreatedById == me)
            .Select(r => r.ProcessSheetId).Distinct().CountAsync();

        var myUser = await _db.Users.FindAsync(me);
        if (myUser != null) { myUser.LastSeenInboxAt = DateTime.Now; await _db.SaveChangesAsync(); }

        return View();
    }

    // ---- Batch overview: sheets (paginated) + progress + history ----
    public async Task<IActionResult> Details(int id, int page = 1)
    {
        var batch = await _db.Batches
            .Include(b => b.CreatedBy).Include(b => b.CurrentHolder).Include(b => b.CheckedBy)
            .Include(b => b.RestrictedRecipient)
            .Include(b => b.Transfers).ThenInclude(t => t.FromUser)
            .Include(b => b.Transfers).ThenInclude(t => t.ToUser)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (batch == null) return NotFound();

        int me = User.GetUserId();
        int total = await _db.ProcessSheets.CountAsync(s => s.BatchId == id);
        int convertedCount = await _db.ProcessSheets.CountAsync(s => s.BatchId == id && s.ConvertedToProcess);
        ViewBag.ConvertedCount = convertedCount;
        // The standard "review each sheet, then Send batch" flow below only makes sense
        // for the old sign-off pipeline — a batch with any Research-derived sheets uses
        // the newer Convert/Quality/Programs workflow instead, where the holder is a
        // Planner converting sheets, not a reviewer signing off on them. ReviewedByMe can
        // never move off zero for those batches (Quality's own reviews are filed under
        // Quality's own id, not the holder's), which would otherwise block Send forever
        // for no real reason.
        bool hasResearchSheets = await _db.ProcessSheets.AnyAsync(s => s.BatchId == id && s.OriginalPdf != null && s.OriginalPdf.Length > 0);
        ViewBag.HasResearchSheets = hasResearchSheets;
        int reviewedByMe = await _db.SheetReviews
            .Where(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Slot != SignOffSlot.CompiledBy && r.Slot != SignOffSlot.CheckedBy)
            .Select(r => r.ProcessSheetId).Distinct().CountAsync();

        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pages);

        var sheets = await _db.ProcessSheets
            .Where(s => s.BatchId == id)
            .OrderBy(s => s.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(s => new BatchSheetRow
            {
                Id = s.Id,
                DrawingNo = s.DrawingNo,
                PartNo = s.PartNo,
                Section = s.Section,
                HasDrawing = (s.FullDrawingImage != null && s.FullDrawingImage.Length > 0)
                    || (s.DiagramImage != null && s.DiagramImage.Length > 0),
                NeedsConversion = s.OriginalPdf != null && s.OriginalPdf.Length > 0 && !s.ConvertedToProcess,
                IsConvertedFromResearch = s.OriginalPdf != null && s.OriginalPdf.Length > 0 && s.ConvertedToProcess,
                IsQualityRejected = s.Reviews.Where(r => r.Slot == SignOffSlot.QualityInspector)
                    .OrderByDescending(r => r.ReviewedAt).Select(r => r.Decision).FirstOrDefault() == ReviewDecision.Rejected,
                WasResentAfterRejection = s.Reviews.Any(r => r.Slot == SignOffSlot.QualityInspector && r.Decision == ReviewDecision.Rejected)
                    && s.ResentForReviewAt.HasValue
                    && s.ResentForReviewAt > s.Reviews.Where(r => r.Slot == SignOffSlot.QualityInspector)
                        .OrderByDescending(r => r.ReviewedAt).Select(r => r.ReviewedAt).FirstOrDefault(),
                SiblingConvertedSheetId = s.ResearchGroupKey != null
                    ? _db.ProcessSheets.Where(o => o.ResearchGroupKey == s.ResearchGroupKey && o.Id != s.Id && o.ConvertedToProcess)
                        .Select(o => (int?)o.Id).FirstOrDefault()
                    : null,
            })
            .ToListAsync();

        var myReviews = await _db.SheetReviews
            .Where(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Slot != SignOffSlot.CompiledBy && r.Slot != SignOffSlot.CheckedBy)
            .OrderByDescending(r => r.ReviewedAt)
            .Select(r => new { r.ProcessSheetId, r.Decision })
            .ToListAsync();

        var myReviewsDict = new Dictionary<int, ReviewDecision>();
        foreach (var x in myReviews)
            if (!myReviewsDict.ContainsKey(x.ProcessSheetId)) myReviewsDict[x.ProcessSheetId] = x.Decision;

        ViewBag.Batch = batch;
        ViewBag.Total = total;
        ViewBag.ReviewedByMe = reviewedByMe;
        ViewBag.Page = page;
        ViewBag.Pages = pages;
        ViewBag.Sheets = sheets;
        ViewBag.MyReviews = myReviewsDict;
        ViewBag.IsHolder = batch.CurrentHolderId == me;
        ViewBag.IsCreator = batch.CreatedById == me || User.IsAdmin();
        var meUser = await MeAsync();
        ViewBag.IsCoordinator = (meUser != null && meUser.IsCoordinator && batch.CreatedById == me) || User.IsAdmin();
        ViewBag.IsQualityInspector = meUser != null && meUser.Slot == SignOffSlot.QualityInspector;
        ViewBag.IsPlanningDept = meUser != null && meUser.Department == "Planning";
        ViewBag.IsProgramsOps = meUser != null && meUser.Department == "Programs & Operation";
        // Converted but not yet sent to anyone for review — what the coordinator's
        // "Send completed to Quality" button offers.
        ViewBag.CompletedCount = await _db.ProcessSheets.CountAsync(s => s.BatchId == id && s.ConvertedToProcess && s.SentToQualityUserId == null);
        // What THIS Quality account specifically was sent here, and how much of it
        // they've actually ACCEPTED so far (a rejected-and-not-yet-fixed sheet doesn't
        // count — "all done" means every sheet actually passed, not just reviewed).
        int sentToMe = meUser == null ? 0 : await _db.ProcessSheets.CountAsync(s => s.BatchId == id && s.SentToQualityUserId == meUser.Id);
        int acceptedOfMine = meUser == null ? 0 : await _db.ProcessSheets
            .Where(s => s.BatchId == id && s.SentToQualityUserId == meUser.Id)
            .CountAsync(s => s.Reviews.Where(r => r.ReviewerId == meUser.Id && r.Slot == SignOffSlot.QualityInspector)
                .OrderByDescending(r => r.ReviewedAt).Select(r => r.Decision).FirstOrDefault() == ReviewDecision.Accepted);
        ViewBag.SentToMeCount = sentToMe;
        ViewBag.AcceptedOfMine = acceptedOfMine;
        ViewBag.AllConverted = sentToMe > 0 && acceptedOfMine >= sentToMe;

        // Same idea, but for the batch as a whole regardless of who's viewing — this is
        // what lets Eman Mahmoud (the coordinator) send to Programs & Operation herself,
        // rather than only Quality being able to trigger that handoff.
        int sentToQualityTotal = await _db.ProcessSheets.CountAsync(s => s.BatchId == id && s.SentToQualityUserId != null);
        int acceptedTotal = await _db.ProcessSheets
            .Where(s => s.BatchId == id && s.SentToQualityUserId != null)
            .CountAsync(s => s.Reviews.Where(r => r.Slot == SignOffSlot.QualityInspector)
                .OrderByDescending(r => r.ReviewedAt).Select(r => r.Decision).FirstOrDefault() == ReviewDecision.Accepted);
        ViewBag.SentToQualityTotal = sentToQualityTotal;
        ViewBag.AcceptedTotal = acceptedTotal;
        ViewBag.ReadyForPrograms = sentToQualityTotal > 0 && acceptedTotal >= sentToQualityTotal;
        if (ViewBag.IsCoordinator)
        {
            ViewBag.MyProjects = await _db.Projects.Where(p => p.CreatedById == me || User.IsAdmin())
                .OrderByDescending(p => p.CreatedAt).ToListAsync();
        }
        ViewBag.Recipients = await RecipientsAsync(me);
        ViewBag.Planners = await PlannersAsync();
        ViewBag.PlacementCount = await _db.StampPlacements.CountAsync(p => p.BatchId == id);

        ViewBag.Rejected = await _db.SheetReviews
            .Where(r => r.ProcessSheet!.BatchId == id && r.Decision == ReviewDecision.Rejected)
            .OrderByDescending(r => r.ReviewedAt)
            .Select(r => new RejectionRow
            {
                SheetId = r.ProcessSheetId,
                Drawing = r.ProcessSheet!.DrawingNo,
                ReviewerName = r.ReviewerName,
                Slot = r.Slot,
                Comment = r.Comment,
                At = r.ReviewedAt,
            })
            .ToListAsync();

        // Pipeline: each reviewer's accepted/rejected tally on this batch.
        var revs = await _db.SheetReviews
            .Where(r => r.ProcessSheet!.BatchId == id)
            .Select(r => new { r.ReviewerId, r.ReviewerName, r.Slot, r.Decision, r.ReviewedAt })
            .ToListAsync();
        ViewBag.Pipeline = revs
            .GroupBy(r => new { r.ReviewerId, r.ReviewerName, r.Slot })
            .Select(g => new PipelineStage
            {
                Name = g.Key.ReviewerName,
                Slot = g.Key.Slot,
                Accepted = g.Count(x => x.Decision == ReviewDecision.Accepted),
                Rejected = g.Count(x => x.Decision == ReviewDecision.Rejected),
                LastAt = g.Max(x => x.ReviewedAt),
            })
            .OrderBy(p => p.LastAt)
            .ToList();
        return View(batch);
    }

    private async Task<List<SelectListItem>> RecipientsAsync(int me) =>
        await _db.Users.Where(u => u.IsActive && u.Id != me)
            .OrderBy(u => u.FullName)
            .Select(u => new SelectListItem($"{u.FullName} — {u.Department} ({u.Slot})", u.Id.ToString()))
            .ToListAsync();

    // ---- View a rejected sheet with the reviewer's drawing overlaid (feedback only) ----
    public async Task<IActionResult> Rejection(int sheetId)
    {
        var sheet = await _db.ProcessSheets
            .Include(s => s.Operations).Include(s => s.Reviews).Include(s => s.Batch).ThenInclude(bt => bt!.Placements)
            .FirstOrDefaultAsync(s => s.Id == sheetId);
        if (sheet == null) return NotFound();

        var rev = sheet.Reviews
            .Where(r => r.Decision == ReviewDecision.Rejected)
            .OrderByDescending(r => r.ReviewedAt)
            .FirstOrDefault();
        ViewBag.Rejection = rev;
        return View(sheet);
    }

    // ---- Place stamp/signature at exact spots on a sample sheet, applied to all ----
    [HttpGet]
    public async Task<IActionResult> PlaceStamps(int id)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var me = await MeAsync();
        if (me == null) return Forbid();

        var sample = await _db.ProcessSheets
            .Where(s => s.BatchId == id).OrderBy(s => s.Id)
            .Include(s => s.Operations).Include(s => s.Reviews)
            .Include(s => s.Batch).ThenInclude(bt => bt!.Placements)
            .FirstOrDefaultAsync();
        if (sample == null) { TempData["Err"] = "This batch has no sheets."; return RedirectToAction("Details", new { id }); }

        ViewBag.Batch = batch;
        ViewBag.MyStamp = (me.StampImage != null && me.StampImage.Length > 0)
            ? $"data:{me.StampMime ?? "image/png"};base64,{Convert.ToBase64String(me.StampImage)}" : null;
        var sigList = await _db.UserSignatures.Where(s => s.UserId == me.Id).OrderBy(s => s.Id).ToListAsync();
        ViewBag.MySignatures = sigList.Select(s => new SignaturePickerItem
        {
            Id = s.Id,
            Label = string.IsNullOrWhiteSpace(s.Label) ? $"Signature {s.Id}" : s.Label,
            DataUrl = $"data:{s.Mime ?? "image/png"};base64,{Convert.ToBase64String(s.Image)}",
        }).ToList();
        ViewBag.StampFields = me.StampFieldsJson;
        ViewBag.Sector = me.Sector ?? "";
        ViewBag.Code = me.StampCode ?? "";
        ViewBag.Mine = await _db.StampPlacements.Where(p => p.BatchId == id && p.UserId == me.Id).ToListAsync();
        return View(sample);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlaceStamps(int id, string placements)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        var me = await MeAsync();
        if (me == null) return Forbid();

        List<PlacementDto> items;
        try { items = System.Text.Json.JsonSerializer.Deserialize<List<PlacementDto>>(placements ?? "[]") ?? new(); }
        catch { items = new(); }

        // Replace this user's placements for the batch.
        var old = await _db.StampPlacements.Where(p => p.BatchId == id && p.UserId == me.Id).ToListAsync();
        _db.StampPlacements.RemoveRange(old);

        foreach (var it in items)
        {
            byte[]? img = me.StampImage;
            string? mime = me.StampMime;
            // Each item may carry its own image (uploaded stamp, or a generated Document-Control stamp).
            if (!string.IsNullOrEmpty(it.img) && it.img.StartsWith("data:"))
            {
                try
                {
                    int semi = it.img.IndexOf(';');
                    int comma = it.img.IndexOf(',');
                    mime = semi > 5 ? it.img.Substring(5, semi - 5) : "image/png";
                    img = Convert.FromBase64String(it.img[(comma + 1)..]);
                }
                catch { /* keep fallback */ }
            }
            _db.StampPlacements.Add(new StampPlacement
            {
                BatchId = id,
                UserId = me.Id,
                NormX = Math.Clamp(it.x, 0, 1),
                NormY = Math.Clamp(it.y, 0, 1),
                AnchorRight = it.ax == "right",
                AnchorBottom = it.ay == "bottom",
                WidthPct = Math.Clamp(it.w, 2, 60),
                AnchorText = string.IsNullOrWhiteSpace(it.text) ? null : it.text.Trim(),
                OffsetXPx = it.dx,
                OffsetYPx = it.dy,
                Image = img,
                Mime = mime,
                CreatedAt = DateTime.Now,
            });
        }
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not save the stamp placement (it may be too large or malformed): " + ex.Message;
            return RedirectToAction("PlaceStamps", new { id });
        }
        TempData["Msg"] = $"Placed {items.Count} stamp(s) on every sheet in the batch.";
        return RedirectToAction("Details", new { id });
    }

    public class PlacementDto
    {
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public string? img { get; set; }
        public string? ax { get; set; }   // "left" or "right" — which edge x is measured from
        public string? ay { get; set; }   // "top" or "bottom" — which edge y is measured from
        public string? text { get; set; }  // optional: anchor to this word/phrase instead of a corner
        public double dx { get; set; }     // px offset from the found text
        public double dy { get; set; }
    }

    public class SignaturePickerItem
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public string DataUrl { get; set; } = "";
    }

    // ---- Review one sheet (position within the batch) ----
    public async Task<IActionResult> Review(int id, int pos = 0)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var ids = await _db.ProcessSheets.Where(s => s.BatchId == id)
            .OrderBy(s => s.Id).Select(s => s.Id).ToListAsync();
        if (ids.Count == 0) { TempData["Msg"] = "This batch has no sheets."; return RedirectToAction("Details", new { id }); }

        pos = Math.Clamp(pos, 0, ids.Count - 1);
        int sheetId = ids[pos];

        var sheet = await _db.ProcessSheets
            .Include(s => s.Operations).Include(s => s.Reviews).Include(s => s.Batch)
            .FirstOrDefaultAsync(s => s.Id == sheetId);
        if (sheet == null) return NotFound();

        int me = User.GetUserId();
        var mine = sheet.Reviews.FirstOrDefault(r => r.ReviewerId == me);

        ViewBag.Batch = batch;
        ViewBag.Pos = pos;
        ViewBag.Count = ids.Count;
        ViewBag.MyReview = mine;
        ViewBag.IsHolder = batch.CurrentHolderId == me;
        ViewBag.ReviewedByMe = await _db.SheetReviews
            .Where(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Slot != SignOffSlot.CompiledBy && r.Slot != SignOffSlot.CheckedBy)
            .Select(r => r.ProcessSheetId).Distinct().CountAsync();
        return View(sheet);
    }

    // ---- Record a decision, then jump to the next unreviewed sheet ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, int sheetId, int pos,
        ReviewDecision decision, string? comment, string? annotation)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        if (batch.CurrentHolderId != User.GetUserId())
        {
            TempData["Msg"] = "This batch is not currently in your inbox.";
            return RedirectToAction("Details", new { id });
        }

        if (decision == ReviewDecision.Rejected && string.IsNullOrWhiteSpace(comment))
        {
            TempData["Err"] = "A reason is required to reject a sheet.";
            return RedirectToAction("Review", new { id, pos });
        }

        var me = await MeAsync();
        if (me == null) return Forbid();

        var review = await _db.SheetReviews
            .FirstOrDefaultAsync(r => r.ProcessSheetId == sheetId && r.ReviewerId == me.Id && r.Slot == me.Slot);
        if (review == null)
        {
            review = new SheetReview { ProcessSheetId = sheetId, ReviewerId = me.Id };
            _db.SheetReviews.Add(review);
        }
        review.Slot = me.Slot;
        review.Decision = decision;
        review.Comment = decision == ReviewDecision.Rejected ? comment?.Trim() : null;
        review.ReviewerName = me.FullName;
        review.ReviewerCode = me.StampCode;
        review.DocSector = me.Sector;
        review.DocIssue = me.DocIssue;
        review.DocControlColor = me.DocControlColor;
        review.ReviewedAt = DateTime.Now;
        review.StampImage = decision == ReviewDecision.Accepted ? me.StampImage : null;
        review.StampMime = decision == ReviewDecision.Accepted ? me.StampMime : null;

        // Reviewer's drawing (rejections only).
        if (decision == ReviewDecision.Rejected && !string.IsNullOrEmpty(annotation) && annotation.StartsWith("data:"))
        {
            try
            {
                int semi = annotation.IndexOf(';');
                int comma = annotation.IndexOf(',');
                string meta = semi > 5 ? annotation.Substring(5, semi - 5) : "image/png";
                review.AnnotationImage = Convert.FromBase64String(annotation[(comma + 1)..]);
                review.AnnotationMime = string.IsNullOrWhiteSpace(meta) ? "image/png" : meta;
            }
            catch { review.AnnotationImage = null; review.AnnotationMime = null; }
        }
        else
        {
            review.AnnotationImage = null;
            review.AnnotationMime = null;
        }
        await _db.SaveChangesAsync();

        // Find the next sheet I haven't reviewed yet.
        var ids = await _db.ProcessSheets.Where(s => s.BatchId == id)
            .OrderBy(s => s.Id).Select(s => s.Id).ToListAsync();
        var reviewed = (await _db.SheetReviews
            .Where(r => r.ReviewerId == me.Id && r.ProcessSheet!.BatchId == id
                        && r.Slot != SignOffSlot.CompiledBy && r.Slot != SignOffSlot.CheckedBy)
            .Select(r => r.ProcessSheetId).ToListAsync()).ToHashSet();

        for (int i = pos + 1; i < ids.Count; i++)
            if (!reviewed.Contains(ids[i])) return RedirectToAction("Review", new { id, pos = i });
        for (int i = 0; i < ids.Count; i++)
            if (!reviewed.Contains(ids[i])) return RedirectToAction("Review", new { id, pos = i });

        TempData["Msg"] = "All sheets reviewed. You can send the batch now.";
        return RedirectToAction("Details", new { id });
    }

    // ---- Send the whole batch to the next person ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int id, int toUserId, string? note)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        int me = User.GetUserId();
        if (batch.CurrentHolderId != me) { TempData["Err"] = "Not your batch to send."; return RedirectToAction("Details", new { id }); }
        if (batch.RestrictedRecipientId.HasValue && toUserId != batch.RestrictedRecipientId.Value)
        {
            TempData["Err"] = "This batch came back from a rejection — it can only be sent back to the reviewer who rejected it.";
            return RedirectToAction("Details", new { id });
        }

        int total = await _db.ProcessSheets.CountAsync(s => s.BatchId == id);
        int reviewed = await _db.SheetReviews.Where(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Slot != SignOffSlot.CompiledBy && r.Slot != SignOffSlot.CheckedBy).Select(r => r.ProcessSheetId).Distinct().CountAsync();
        if (reviewed < total)
        {
            TempData["Err"] = $"Review every sheet first ({reviewed}/{total} done).";
            return RedirectToAction("Details", new { id });
        }

        bool anyRejected = await _db.SheetReviews
            .AnyAsync(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Decision == ReviewDecision.Rejected);

        batch.CurrentHolderId = toUserId;
        batch.Status = BatchStatus.InReview;
        batch.Transfers.Add(new BatchTransfer
        {
            FromUserId = me,
            ToUserId = toUserId,
            Action = anyRejected ? "Sent (with rejections)" : "Approved & sent",
            Note = note?.Trim(),
            At = DateTime.Now,
        });
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Batch sent.";
        return RedirectToAction("Index");
    }

    // ---- Edit batch-level values (contract / work order / multiplier / checker) ----
    [HttpGet]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> EditDetails(int id)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        ViewBag.Planners = await PlannersAsync();
        return View(batch);
    }

    // Lets a coordinator (e.g. the person who splits incoming drawings across the team)
    // hand a draft batch to a different planner — the batch then behaves exactly as if
    // that planner had created it themselves.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reassign(int id, int toUserId)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin))
        {
            TempData["Err"] = "Only a coordinator can reassign a batch.";
            return RedirectToAction("Details", new { id });
        }
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        if (batch.CreatedById != me.Id && !User.IsAdmin())
        {
            TempData["Err"] = "Only the batch's own coordinator can reassign it.";
            return RedirectToAction("Details", new { id });
        }
        var target = await _db.Users.FindAsync(toUserId);
        if (target == null || !target.IsPlanner)
        {
            TempData["Err"] = "Choose a valid planner to assign to.";
            return RedirectToAction("Details", new { id });
        }
        // CreatedById never changes — that's what keeps this batch showing up under
        // "Batches I created" forever, so Eman Mahmoud can always come back in and check
        // status. Only who currently holds it (their own "Waiting for you" inbox) moves.
        batch.CurrentHolderId = toUserId;
        batch.Status = BatchStatus.InReview;
        // A batch with no project would otherwise just float with nothing to find it
        // by once it's someone else's — every reassigned batch gets a project (named
        // after it, or joining one already using that name) so it's always reachable
        // from the recipient's own Projects tab, not just their plain batch list.
        await EnsureProjectForBatchAsync(batch, me);
        batch.Transfers.Add(new BatchTransfer { FromUserId = me.Id, ToUserId = toUserId, Action = "Assigned", At = DateTime.Now });
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Batch assigned to {target.FullName}.";
        return RedirectToAction("Details", new { id });
    }

    // ---- Reassign every one of my own batches within a project to a different planner
    // in one go, instead of opening each batch and reassigning it individually. ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReassignProject(int projectId, int toUserId)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin))
        {
            TempData["Err"] = "Only a coordinator can reassign a project.";
            return RedirectToAction("Projects");
        }
        var project = await _db.Projects.FindAsync(projectId);
        if (project == null) return NotFound();
        var target = await _db.Users.FindAsync(toUserId);
        if (target == null || !target.IsPlanner)
        {
            TempData["Err"] = "Choose a valid planner to assign to.";
            return RedirectToAction("Projects");
        }
        // Only batches I created within this project — a project can also hold the
        // Angle/Plate/Quality copies sent to entirely different people, which aren't
        // mine to reassign.
        var myBatches = User.IsAdmin()
            ? await _db.Batches.Where(b => b.ProjectId == projectId).ToListAsync()
            : await _db.Batches.Where(b => b.ProjectId == projectId && b.CreatedById == me.Id).ToListAsync();
        foreach (var batch in myBatches)
        {
            batch.CurrentHolderId = toUserId;
            batch.Status = BatchStatus.InReview;
            batch.Transfers.Add(new BatchTransfer { FromUserId = me.Id, ToUserId = toUserId, Action = "Assigned", At = DateTime.Now, Note = $"Whole project \"{project.Name}\" reassigned" });
        }
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Project \"{project.Name}\" ({myBatches.Count} batch(es)) assigned to {target.FullName}.";
        return RedirectToAction("Projects");
    }

    // ---- Split an EXISTING batch's own sheets across several planners by count — e.g.
    // this batch has 100 drawings, give 50 to Azza and 50 to Hala. No re-upload: the
    // sheets themselves move into a new batch per planner. ----
    // ---- Group several batches under one named project folder (e.g. all the Angle +
    // Plate deliveries that belong to "S2") — purely organizational. ----
    public async Task<IActionResult> Projects()
    {
        var me = await MeAsync();
        if (me == null) { TempData["Err"] = "Not allowed."; return RedirectToAction("Index"); }

        // A batch is "mine" if I created it, currently hold it, or SENT it to someone. The last
        // one matters for Research: a delivery they send is created in the recipient's name
        // (Angle workshop, Quality, Planning, ...), so without it everything Research sent would
        // vanish from their own Projects page the moment it was sent.
        int myId = me.Id;
        System.Linq.Expressions.Expression<Func<Batch, bool>> isMine = b =>
            b.CreatedById == myId || b.CurrentHolderId == myId || b.Transfers.Any(t => t.FromUserId == myId);

        // A project shows up here if I have at least one batch inside it, or I created the
        // project record itself (Research does, the moment they name it while sending).
        var myProjectIds = await _db.Batches.Where(isMine).Where(b => b.ProjectId != null)
            .Select(b => b.ProjectId!.Value).Distinct().ToListAsync();
        var projects = me.IsAdmin
            ? await _db.Projects.OrderByDescending(p => p.CreatedAt).ToListAsync()
            : await _db.Projects.Where(p => myProjectIds.Contains(p.Id) || p.CreatedById == myId).OrderByDescending(p => p.CreatedAt).ToListAsync();
        var projectIds = projects.Select(p => p.Id).ToList();
        // My own batches within those projects — a project can also hold copies that went to
        // entirely different people and were never mine.
        var batchesByProject = await (me.IsAdmin
                ? _db.Batches.Where(b => b.ProjectId != null && projectIds.Contains(b.ProjectId.Value))
                : _db.Batches.Where(b => b.ProjectId != null && projectIds.Contains(b.ProjectId.Value)).Where(isMine))
            .Include(b => b.CurrentHolder).Include(b => b.CreatedBy).Include(b => b.Transfers)
            .OrderByDescending(b => b.CreatedAt).ToListAsync();

        var projectsWithPartList = await _db.ProjectPartLists.Where(p => projectIds.Contains(p.ProjectId))
            .Select(p => p.ProjectId).ToListAsync();
        ViewBag.PartListExistsProjectIds = projectsWithPartList;
        ViewBag.PartListProjectIds = batchesByProject
            .Where(b => b.PartListShared && projectsWithPartList.Contains(b.ProjectId!.Value))
            .Select(b => b.ProjectId!.Value).Distinct().ToList();

        ViewBag.Projects = projects;
        ViewBag.BatchesByProject = batchesByProject.GroupBy(b => b.ProjectId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        ViewBag.Unassigned = await _db.Batches.Where(isMine).Where(b => b.ProjectId == null)
            .Include(b => b.Transfers)
            .OrderByDescending(b => b.CreatedAt).ToListAsync();
        // Only shown to the batches' own coordinator, and only for projects where I
        // actually created at least one batch — matches who's allowed to reassign a
        // single batch, just extended to the whole project at once.
        ViewBag.IsCoordinator = me.IsCoordinator || me.IsAdmin;
        ViewBag.MyCreatedProjectIds = me.IsAdmin ? projectIds
            : await _db.Batches.Where(b => b.CreatedById == me.Id && b.ProjectId != null)
                .Select(b => b.ProjectId!.Value).Distinct().ToListAsync();
        ViewBag.Planners = await PlannersAsync();

        // Planning now lands here straight after login instead of Index, so the "new in
        // your inbox" notification has to be cleared from here too — otherwise it would
        // never go away for anyone who no longer visits Index at all.
        var myUserRow = await _db.Users.FindAsync(me.Id);
        if (myUserRow != null) { myUserRow.LastSeenInboxAt = DateTime.Now; await _db.SaveChangesAsync(); }
        return View();
    }

    // ---- Part List (reference PDF of every part and its data) ----
    private const long MaxPartListBytes = 30L * 1024 * 1024;

    // Same redirect Confirm & Send is normally reached with — the drawing counts are
    // recomputed from what's on disk for this upload, so the page doesn't come back all zeros.
    private IActionResult RedirectBackToConfirm(string token, double? normX, double? normY,
        bool anchorRight, bool anchorBottom, double? widthPct, double? fontScale)
    {
        var dir = CalDir(token);
        int Count(string sub) => Directory.Exists(Path.Combine(dir, sub))
            ? Directory.GetFiles(Path.Combine(dir, sub), "*.pdf").Length : 0;
        return RedirectToAction("ResearchConfirm", new
        {
            token,
            angleCount = Count("angle"),
            plateCount = Count("plate"),
            otherCount = Count("other"),
            imageCount = Count("images"),
            normX,
            normY,
            anchorRight,
            anchorBottom,
            widthPct,
            fontScale,
        });
    }

    // Reads and sanity-checks an uploaded Part List: must be a PDF and not absurdly large.
    private static async Task<(byte[]? Bytes, string? Error)> ReadPartListAsync(IFormFile file)
    {
        if (file.Length > MaxPartListBytes) return (null, "The Part List file is too large (30 MB max).");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        // A real PDF starts with %PDF (some tools put a little junk before it, so allow a short lead-in).
        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        if (!head.Contains("%PDF")) return (null, "The Part List must be a PDF file.");
        return (bytes, null);
    }

    // The Part List must follow the standard template (Mark No. | Profie | Weight(kg/m) | Qty/LEG |
    // Length(m) | Description). Returns a message telling exactly what is wrong, or null when it fits.
    private string? PartListTemplateError(byte[] pdf)
    {
        var t = PartListReader.ReadTemplate(pdf);
        if (t.Ok) return null;
        string cols = string.Join(" | ", PartListReader.TemplateColumns);
        if (!t.HeaderFound)
            return t.MissingColumns.Count > 0
                ? string.Format(Tr.S(HttpContext, "pl_tpl_missing"), string.Join(", ", t.MissingColumns), cols)
                : string.Format(Tr.S(HttpContext, "pl_tpl_order"), cols);
        if (t.RowCount == 0)
            return string.Format(Tr.S(HttpContext, "pl_tpl_norows"), cols);
        return string.Format(Tr.S(HttpContext, "pl_tpl_badrows"), t.FirstBadLine ?? "", cols);
    }

    // The template as a spreadsheet: fill it in, print or save it as PDF, then upload that PDF.
    [HttpGet]
    public IActionResult PartListTemplate()
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Part List");
        var widths = new[] { 16.0, 20.0, 16.0, 12.0, 12.0, 40.0 };
        for (int c = 0; c < PartListReader.TemplateColumns.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = PartListReader.TemplateColumns[c];
            cell.Style.Font.Bold = true;
            cell.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            ws.Column(c + 1).Width = widths[c];
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "PartList-Template.xlsx");
    }

    // One Part List per project — a new upload replaces whatever was there before.
    private async Task StorePartListAsync(int projectId, string fileName, byte[] pdf, AppUser me)
    {
        if (fileName.Length > 200) fileName = fileName[..200];
        var row = await _db.ProjectPartLists.FirstOrDefaultAsync(p => p.ProjectId == projectId);
        if (row == null)
        {
            _db.ProjectPartLists.Add(new ProjectPartList
            {
                ProjectId = projectId,
                FileName = fileName,
                Pdf = pdf,
                UploadedByName = me.FullName,
                UploadedAt = DateTime.Now,
            });
        }
        else
        {
            row.FileName = fileName; row.Pdf = pdf; row.UploadedByName = me.FullName; row.UploadedAt = DateTime.Now;
        }
        await _db.SaveChangesAsync();
    }

    // Everything about a project's Part List except the PDF bytes themselves.
    private Task<ProjectPartList?> PartListInfoAsync(int? projectId) =>
        projectId == null ? Task.FromResult<ProjectPartList?>(null)
            : _db.ProjectPartLists.Where(p => p.ProjectId == projectId)
                .Select(p => new ProjectPartList { Id = p.Id, ProjectId = p.ProjectId, FileName = p.FileName, UploadedByName = p.UploadedByName, UploadedAt = p.UploadedAt })
                .FirstOrDefaultAsync();

    // A batch with no project has nowhere to hang a project-level file or be found from
    // someone's Projects tab — gives it one (named after it, or joining one with that name).
    private async Task<int> EnsureProjectForBatchAsync(Batch batch, AppUser me)
    {
        if (batch.ProjectId.HasValue) return batch.ProjectId.Value;
        var projectName = (string.IsNullOrWhiteSpace(batch.ProjectName) ? batch.Name : batch.ProjectName).Trim();
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Name == projectName);
        if (project == null)
        {
            project = new TowerProcessSheet.Models.Project { Name = projectName, CreatedById = me.Id };
            _db.Projects.Add(project);
            await _db.SaveChangesAsync();
        }
        batch.ProjectId = project.Id;
        batch.ProjectName = projectName;
        return project.Id;
    }

    // Opens the project's Part List PDF in the browser (inline) so it can be read next to a
    // drawing or Process Sheet that isn't clear.
    [HttpGet]
    public async Task<IActionResult> PartList(int projectId)
    {
        var row = await _db.ProjectPartLists.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId);
        if (row == null) return NotFound();
        Response.Headers["Content-Disposition"] = "inline; filename*=UTF-8''" + Uri.EscapeDataString(row.FileName);
        return File(row.Pdf, "application/pdf");
    }

    // ---- Part List check: every row of the project's Part List, marked automatically by what
    // exists for it — converted to a Process Sheet, drawing received but not converted yet, or
    // nothing at all. Worked out live from the project's sheets each time, so it can never go
    // stale when a sheet is converted, edited or deleted. ----
    public class PartListCheckRow
    {
        public PartListRow Part { get; set; } = new();
        public int State { get; set; }               // 0 = no sheet yet, 1 = drawing received (not converted), 2 = converted to Process
        public int? SheetId { get; set; }
        public string? SheetPartNo { get; set; }     // the matched sheet's own Part No, so a wrong match is easy to spot
        public bool Manual { get; set; }             // ticked by hand
        public string? MarkedBy { get; set; }
        public bool Marked => State == 2 || Manual;  // counts as done: converted automatically, or ticked by hand
    }

    public class PartListUnmatchedSheet
    {
        public int Id { get; set; }
        public string PartNo { get; set; } = "";
        public bool Converted { get; set; }
    }

    public class PartListCheck
    {
        public int ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string FileName { get; set; } = "";
        public List<PartListCheckRow> Rows { get; set; } = new();
        public List<PartListUnmatchedSheet> UnmatchedSheets { get; set; } = new();
    }

    private async Task<PartListCheck?> BuildPartListCheckAsync(int projectId)
    {
        var file = await _db.ProjectPartLists.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId);
        if (file == null) return null;
        var projectName = await _db.Projects.Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync() ?? "";
        var parts = PartListReader.ReadCached(projectId, file.UploadedAt, file.Pdf);
        var partKeys = new HashSet<string>(parts.SelectMany(x => x.Keys));

        var sheets = await _db.ProcessSheets
            .Where(s => s.Batch != null && s.Batch.ProjectId == projectId && s.PartNo != "")
            .Select(s => new { s.Id, s.PartNo, s.ConvertedToProcess })
            .ToListAsync();

        // The same drawing can exist in several batches of the project (one copy per destination),
        // so a converted copy wins over an unconverted one when they point at the same part.
        var found = new Dictionary<string, (int SheetId, string PartNo, bool Converted)>();
        var unmatched = new Dictionary<string, PartListUnmatchedSheet>();
        foreach (var sh in sheets)
        {
            var key = PartListReader.Candidates(sh.PartNo).FirstOrDefault(k => partKeys.Contains(k));
            if (key == null)
            {
                var uk = PartListReader.Normalize(sh.PartNo);
                if (!unmatched.TryGetValue(uk, out var u) || (!u.Converted && sh.ConvertedToProcess))
                    unmatched[uk] = new PartListUnmatchedSheet { Id = sh.Id, PartNo = sh.PartNo, Converted = sh.ConvertedToProcess };
                continue;
            }
            if (!found.TryGetValue(key, out var cur) || (!cur.Converted && sh.ConvertedToProcess))
                found[key] = (sh.Id, sh.PartNo, sh.ConvertedToProcess);
        }

        var marks = await _db.PartListManualMarks.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .ToDictionaryAsync(m => m.RowKey, m => m.MarkedByName);

        var check = new PartListCheck { ProjectId = projectId, ProjectName = projectName, FileName = file.FileName };
        foreach (var part in parts)
        {
            var row = new PartListCheckRow { Part = part };
            // A row can answer to several keys (a plain-text row lists every word on it); the best sheet wins.
            (int SheetId, string PartNo, bool Converted)? best = null;
            foreach (var k in part.Keys)
                if (found.TryGetValue(k, out var f) && (best == null || (!best.Value.Converted && f.Converted)))
                    best = f;
            if (best != null)
            {
                row.State = best.Value.Converted ? 2 : 1;
                row.SheetId = best.Value.SheetId;
                row.SheetPartNo = best.Value.PartNo;
            }
            if (marks.TryGetValue(part.RowKey, out var markedBy)) { row.Manual = true; row.MarkedBy = markedBy; }
            check.Rows.Add(row);
        }
        check.UnmatchedSheets = unmatched.Values.OrderBy(x => x.PartNo).ToList();
        return check;
    }

    // q (optional) pre-fills the search box, so opening the list from a sheet lands on that
    // sheet's own row, already highlighted.
    [HttpGet]
    public async Task<IActionResult> PartListStatus(int projectId, string? q = null)
    {
        var check = await BuildPartListCheckAsync(projectId);
        if (check == null) return NotFound();
        ViewBag.Query = q ?? "";
        var meNow = await MeAsync();
        ViewBag.CanMark = meNow != null && !meNow.IsDisplay;
        return View(check);
    }

    // Tick / untick a Part List row by hand — for a row the automatic check missed.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePartListMark(int projectId, string rowKey, bool marked)
    {
        var me = await MeAsync();
        if (me == null || me.IsDisplay) return Forbid();
        if (string.IsNullOrWhiteSpace(rowKey) || rowKey.Length > 120) return BadRequest();
        if (!await _db.ProjectPartLists.AnyAsync(p => p.ProjectId == projectId)) return NotFound();

        var mark = await _db.PartListManualMarks.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.RowKey == rowKey);
        if (marked && mark == null)
            _db.PartListManualMarks.Add(new PartListManualMark { ProjectId = projectId, RowKey = rowKey, MarkedByName = me.FullName, MarkedAt = DateTime.Now });
        else if (!marked && mark != null)
            _db.PartListManualMarks.Remove(mark);
        await _db.SaveChangesAsync();
        return Json(new { ok = true, marked, by = me.FullName });
    }

    // The same check as a spreadsheet — rows already coloured by status, so what's still
    // missing can be handed around or filtered in Excel.
    [HttpGet]
    public async Task<IActionResult> PartListStatusExcel(int projectId)
    {
        var check = await BuildPartListCheckAsync(projectId);
        if (check == null) return NotFound();

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Part List");
        // A list that follows the standard template keeps its own column names; an older Tekla-style list keeps the old ones.
        bool tpl = check.Rows.Any(x => x.Part.Template);
        string[] heads = tpl
            ? new[] { "Status", "Mark No.", "Profie", "Weight(kg/m)", "Qty/LEG", "Length(m)", "Description", "Matched sheet" }
            : new[] { "Status", "POS", "QTY", "SECTION", "WIDTH", "LENGTH", "WEIGHT", "ST. GR.", "Matched sheet", "Line (as read)" };
        for (int c = 0; c < heads.Length; c++)
        {
            ws.Cell(1, c + 1).Value = heads[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }
        int r = 2;
        foreach (var row in check.Rows)
        {
            var p = row.Part;
            var statusText = row.State == 2 ? "Process Sheet Done" : row.Manual ? "Marked by hand" : row.State == 1 ? "Recognized from Drawing (not converted)" : "No sheet";
            var values = tpl
                ? new[] { statusText, p.Pos, p.Section, p.Weight, p.Qty, p.Length, p.Remark, row.SheetPartNo ?? "" }
                : new[] { statusText, p.Pos, p.Qty, p.Section, p.Width, p.Length, p.Weight, p.Grade, row.SheetPartNo ?? "", p.Loose ? p.Raw : "" };
            for (int c = 0; c < values.Length; c++) ws.Cell(r, c + 1).Value = values[c];
            if (row.State == 2) ws.Range(r, 1, r, values.Length).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGreen;
            else if (row.Manual) ws.Range(r, 1, r, values.Length).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightSkyBlue;
            else if (row.State == 1) ws.Range(r, 1, r, values.Length).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightYellow;
            r++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"PartList-{SanitizeFileName(check.ProjectName)}.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProject(string name)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin)) { TempData["Err"] = "Not allowed."; return RedirectToAction("Index"); }
        if (string.IsNullOrWhiteSpace(name)) { TempData["Err"] = "Give the project a name."; return RedirectToAction("Projects"); }

        _db.Projects.Add(new TowerProcessSheet.Models.Project { Name = name.Trim(), CreatedById = me.Id });
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Project \"{name.Trim()}\" created.";
        return RedirectToAction("Projects");
    }

    // ---- Admin cleanup: permanently delete a whole project, every batch inside it, and
    // every sheet in those batches. A batch's own link to its sheets doesn't cascade on
    // its own (it just clears the link), so the sheets are removed explicitly first. ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteProject(int id)
    {
        if (!User.IsAdmin()) { TempData["Err"] = "Only an admin can delete a project."; return RedirectToAction("Projects"); }
        var project = await _db.Projects.FindAsync(id);
        if (project == null) return NotFound();

        var batchIds = await _db.Batches.Where(b => b.ProjectId == id).Select(b => b.Id).ToListAsync();
        var sheets = await _db.ProcessSheets.Where(s => s.BatchId != null && batchIds.Contains(s.BatchId.Value)).ToListAsync();
        _db.ProcessSheets.RemoveRange(sheets);
        await _db.SaveChangesAsync();
        var batches = await _db.Batches.Where(b => batchIds.Contains(b.Id)).ToListAsync();
        _db.Batches.RemoveRange(batches);
        _db.Projects.Remove(project);
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Project \"{project.Name}\" deleted — {batches.Count} batch(es) and {sheets.Count} sheet(s) with it.";
        return RedirectToAction("Projects");
    }

    // ---- Admin cleanup: permanently delete one batch and every sheet in it, without
    // touching the rest of its project. ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBatch(int id)
    {
        if (!User.IsAdmin()) { TempData["Err"] = "Only an admin can delete a batch."; return RedirectToAction("Projects"); }
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var sheets = await _db.ProcessSheets.Where(s => s.BatchId == id).ToListAsync();
        _db.ProcessSheets.RemoveRange(sheets);
        await _db.SaveChangesAsync();
        var batchName = batch.Name;
        _db.Batches.Remove(batch);
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Batch \"{batchName}\" deleted — {sheets.Count} sheet(s) with it.";
        return RedirectToAction("Projects");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignToProject(int batchId, int? projectId)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        batch.ProjectId = projectId == 0 ? null : projectId;
        await _db.SaveChangesAsync();
        TempData["Msg"] = projectId is null or 0 ? "Removed from its project." : "Added to project.";
        return RedirectToAction("Details", new { id = batchId });
    }

    [HttpGet]
    public async Task<IActionResult> SplitAssign(int id)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin))
        {
            TempData["Err"] = "Only a coordinator can split a batch this way.";
            return RedirectToAction("Details", new { id });
        }
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        int total = await _db.ProcessSheets.CountAsync(s => s.BatchId == id);
        int unassigned = await _db.ProcessSheets.CountAsync(s => s.BatchId == id && s.AssignedToUserId == null);
        ViewBag.Batch = batch;
        ViewBag.Total = total;
        ViewBag.Unassigned = unassigned;
        ViewBag.Planners = await PlannersAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SplitAssign(int id, List<int> plannerIds, List<int> counts)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin))
        {
            TempData["Err"] = "Only a coordinator can split a batch this way.";
            return RedirectToAction("Details", new { id });
        }
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        if (batch.CreatedById != me.Id && !User.IsAdmin())
        {
            TempData["Err"] = "Only the batch's own coordinator can split it this way.";
            return RedirectToAction("Details", new { id });
        }

        var shares = new List<(int PlannerId, int Count)>();
        for (int i = 0; i < Math.Min(plannerIds?.Count ?? 0, counts?.Count ?? 0); i++)
            if (plannerIds![i] > 0 && counts![i] > 0) shares.Add((plannerIds[i], counts[i]));

        if (shares.Count == 0) { TempData["Err"] = "Pick at least one planner and a count."; return RedirectToAction("SplitAssign", new { id }); }

        // Only sheets nobody's responsible for yet — re-running this later (to hand out
        // more of the same batch) never touches assignments already made.
        var available = await _db.ProcessSheets.Where(s => s.BatchId == id && s.AssignedToUserId == null).OrderBy(s => s.Id).ToListAsync();
        int totalRequested = shares.Sum(s => s.Count);
        if (totalRequested > available.Count)
        {
            TempData["Err"] = $"You asked to split {totalRequested} drawings but only {available.Count} in this batch aren't already assigned to someone.";
            return RedirectToAction("SplitAssign", new { id });
        }

        // Sheets stay exactly where they are — this only marks who's responsible for
        // each one. Nothing moves out of this batch, so it stays visible here forever,
        // and status updates (converted, reviewed) show up immediately without anyone
        // needing to "send it back".
        var summary = new List<string>();
        int cursor = 0;
        foreach (var (plannerId, count) in shares)
        {
            var target = await _db.Users.FindAsync(plannerId);
            if (target == null || !target.IsPlanner) continue;
            var slice = available.Skip(cursor).Take(count).ToList();
            cursor += count;
            if (slice.Count == 0) continue;

            foreach (var sheet in slice) sheet.AssignedToUserId = target.Id;
            await _db.SaveChangesAsync();
            summary.Add($"{target.FullName}: {slice.Count}");
        }

        TempData["Msg"] = summary.Count > 0 ? "Assigned — " + string.Join(" · ", summary) : "Nothing was assigned.";
        return RedirectToAction("Details", new { id });
    }

    // ---- Coordinator: upload a pile of drawings (one multi-page PDF, or many separate
    // files) and split it across several planners in one go, e.g. 1000 drawings -> 500 to
    // Hala, 500 to Azza. Each planner gets their own new batch, already filled in using the
    // saved calibration layout — same as a normal import, just done for several people at once.
    [HttpGet]
    public async Task<IActionResult> DistributeDrawings()
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin))
        {
            TempData["Err"] = "Only a coordinator can distribute drawings.";
            return RedirectToAction("Index");
        }
        ViewBag.Planners = await PlannersAsync();
        ViewBag.Saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Default");
        return View();
    }

    public class DistributeShareDto
    {
        public int ToUserId { get; set; }
        public int Count { get; set; }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(512L * 1024 * 1024)]
    public async Task<IActionResult> DistributeDrawings(string mode, List<IFormFile> pdfs, string sharesJson, string? projectName)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin))
        {
            TempData["Err"] = "Only a coordinator can distribute drawings.";
            return RedirectToAction("Index");
        }

        List<DistributeShareDto> shares;
        try { shares = System.Text.Json.JsonSerializer.Deserialize<List<DistributeShareDto>>(sharesJson ?? "[]") ?? new(); }
        catch { TempData["Err"] = "Could not read the split you entered."; return RedirectToAction("DistributeDrawings"); }
        shares = shares.Where(s => s.Count > 0).ToList();
        if (shares.Count == 0) { TempData["Err"] = "Enter how many drawings go to each planner."; return RedirectToAction("DistributeDrawings"); }

        var files = (pdfs ?? new()).Where(f => f.Length > 0 && f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { TempData["Err"] = "Upload at least one PDF."; return RedirectToAction("DistributeDrawings"); }

        var saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Default");
        if (saved == null)
        {
            TempData["Err"] = "No saved layout yet — use Import drawings once first to mark where each field sits.";
            return RedirectToAction("DistributeDrawings");
        }
        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(saved.RectsJson) ?? new();
            rects = raw.ToDictionary(kv => kv.Key,
                kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "The saved layout is unreadable — please recalibrate."; return RedirectToAction("DistributeDrawings"); }

        // Build the flat, ordered pile of single-page PDFs to divide up.
        string token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(CalDir(token));
        var pagePaths = new List<string>();
        int fileIdx = 0;
        if (mode == "onepdf")
        {
            using var ms = new MemoryStream();
            await files[0].CopyToAsync(ms);
            List<byte[]> pages;
            try { pages = Services.PdfSplitter.SplitToBytes(ms.ToArray()); }
            catch (Exception ex) { TempData["Err"] = "Could not read that PDF: " + ex.Message; return RedirectToAction("DistributeDrawings"); }
            foreach (var page in pages)
            {
                string p = Path.Combine(CalDir(token), $"{fileIdx:D4}.pdf");
                await System.IO.File.WriteAllBytesAsync(p, page);
                pagePaths.Add(p); fileIdx++;
            }
        }
        else
        {
            foreach (var f in files)
            {
                string p = Path.Combine(CalDir(token), $"{fileIdx:D4}_{Path.GetFileName(f.FileName)}");
                await using (var fs = System.IO.File.Create(p)) await f.CopyToAsync(fs);
                pagePaths.Add(p); fileIdx++;
            }
        }

        int totalRequested = shares.Sum(s => s.Count);
        if (totalRequested > pagePaths.Count)
        {
            TempData["Err"] = $"You asked to split {totalRequested} drawings but only {pagePaths.Count} were uploaded.";
            try { Directory.Delete(CalDir(token), true); } catch { }
            return RedirectToAction("DistributeDrawings");
        }

        // Slice the pile in order and create one new batch per planner.
        var summary = new List<string>();
        int cursor = 0;
        foreach (var share in shares)
        {
            var target = await _db.Users.FindAsync(share.ToUserId);
            if (target == null || !target.IsPlanner) continue;
            var slice = pagePaths.Skip(cursor).Take(share.Count).ToList();
            cursor += share.Count;
            if (slice.Count == 0) continue;

            var batch = new Batch
            {
                Name = $"BATCH-{DateTime.Now:yyyyMMdd-HHmm}-{target.Username}",
                ProjectName = projectName?.Trim(),
                CreatedById = target.Id,
                CreatedAt = DateTime.Now,
                Status = BatchStatus.Draft,
            };
            _db.Batches.Add(batch);
            await _db.SaveChangesAsync();
            batch.Transfers.Add(new BatchTransfer { FromUserId = me.Id, ToUserId = target.Id, Action = "Assigned", At = DateTime.Now });

            var (created, failed) = await ProcessFiles(slice, token, batch, rects, saved.NameField);
            await _db.SaveChangesAsync();
            summary.Add($"{target.FullName}: {created} sheet(s){(failed.Count > 0 ? $", {failed.Count} could not be read" : "")}");
        }

        try { Directory.Delete(CalDir(token), true); } catch { /* best effort cleanup */ }

        TempData["Msg"] = "Distributed — " + string.Join(" · ", summary);
        return RedirectToAction("Index");
    }

    // ---- Research: upload a pile of drawings, auto-split by Section's first letter
    // (L = Angle, P = Plate), then send each group to whoever it should go to — a
    // workshop, Quality, or a specific Planning engineer — stamped with Document
    // Control showing that destination's own code. ----
    [HttpGet]
    public async Task<IActionResult> ResearchSplit()
    {
        var me = await MeAsync();
        if (me == null || (!me.IsAdmin && (!me.IsCoordinator || me.Department != "Research")))
        {
            TempData["Err"] = "Only Research can use this.";
            return RedirectToAction("Index");
        }
        ViewBag.Saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Research");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(512L * 1024 * 1024)]
    public async Task<IActionResult> ResearchSplitUpload(string mode, List<IFormFile> pdfs, string? projectName, bool recalibrate = false)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin)) { TempData["Err"] = "Not allowed."; return RedirectToAction("Index"); }

        var files = (pdfs ?? new()).Where(f => f.Length > 0 && f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { TempData["Err"] = "Upload at least one PDF."; return RedirectToAction("ResearchSplit"); }

        // Save everything to a working folder first — regardless of whether a layout is
        // already saved. If not, the very next step marks it using this upload's own first
        // file, then comes straight back and finishes with all of it. No detour, no batch.
        string token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(CalDir(token), "angle"));
        Directory.CreateDirectory(Path.Combine(CalDir(token), "plate"));
        Directory.CreateDirectory(Path.Combine(CalDir(token), "other"));
        if (!string.IsNullOrWhiteSpace(projectName))
            await System.IO.File.WriteAllTextAsync(Path.Combine(CalDir(token), "_project.txt"), projectName.Trim());

        if (mode == "onepdf")
        {
            using var ms = new MemoryStream();
            await files[0].CopyToAsync(ms);
            List<byte[]> pages;
            try { pages = Services.PdfSplitter.SplitToBytes(ms.ToArray()); }
            catch (Exception ex) { TempData["Err"] = "Could not read that PDF: " + ex.Message; return RedirectToAction("ResearchSplit"); }
            int i = 0;
            foreach (var page in pages)
            {
                await System.IO.File.WriteAllBytesAsync(Path.Combine(CalDir(token), $"raw_{i:D4}.pdf"), page);
                i++;
            }
        }
        else
        {
            int i = 0;
            foreach (var f in files)
            {
                var p = Path.Combine(CalDir(token), $"raw_{i:D4}_{Path.GetFileName(f.FileName)}");
                await using (var fs = System.IO.File.Create(p)) await f.CopyToAsync(fs);
                i++;
            }
        }

        var saved = recalibrate ? null : await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Research");
        if (saved == null)
            return RedirectToAction("ResearchCalibrate", new { token });

        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(saved.RectsJson) ?? new();
            rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "The saved layout is unreadable — please recalibrate."; return RedirectToAction("ResearchCalibrate", new { token }); }
        if (!rects.ContainsKey("section"))
            return RedirectToAction("ResearchCalibrate", new { token });

        var (angleCount, plateCount, otherCount, imageCount, samples) = ClassifyResearchFiles(token, rects);
        if (angleCount == 0 && plateCount == 0 && otherCount > 0)
        {
            // The saved Section position didn't find a recognizable L/P value on a
            // single drawing here — almost certainly because these drawings use a
            // different layout than whatever the saved calibration was marked on.
            // Rather than block here (which would be a dead end — this same account
            // could hit this on every upload), let it through as "all Other" and rely
            // on "Reassign Others" on the next screen to actually fix it.
            TempData["Msg"] = "Couldn't find a valid Section on any of these drawings using the saved positions — " +
                "everything's been put in Other for now. Use \"Reassign Others\" on the next screen to mark " +
                "the right spot for this batch specifically. What was actually read from that spot: " + string.Join(" | ", samples) + " — " + DrawingOcr.Status;
        }
        return RedirectToAction("ResearchPlaceStamp", new { token, angleCount, plateCount, otherCount });
    }

    // Splits every raw PDF page in the token folder into angle/plate/other by Section's first letter.
    private (int angleCount, int plateCount, int otherCount, int imageCount, List<string> samples) ClassifyResearchFiles(
        string token, Dictionary<string, DrawingRegionExtractor.NormRect> rects, bool otherOnly = false)
    {
        // Reassigning only searches inside the "other" subfolder — files already sorted
        // correctly into angle/plate stay untouched, so a second, different-looking
        // batch of drawings within the same upload can get its own field positions
        // without disturbing what already classified correctly the first time.
        var searchDir = otherOnly ? Path.Combine(CalDir(token), "other") : CalDir(token);
        var pagePaths = Directory.Exists(searchDir)
            ? Directory.GetFiles(searchDir, "raw_*.pdf", SearchOption.AllDirectories).ToList()
            : new List<string>();
        var sectionRect = new Dictionary<string, DrawingRegionExtractor.NormRect> { ["section"] = rects["section"] };
        int angleCount = 0, plateCount = 0, otherCount = 0, imageCount = 0;
        var samples = new List<string>();
        foreach (var path in pagePaths)
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                // A scanned/photographed page has no text layer at all — no box position
                // could ever find a Section value on it, so it goes straight into its own
                // "images" bucket rather than being tried and inevitably landing in "other".
                // With OCR installed the Section is first tried from the page image; only a page where even
                // that reads nothing goes to the images bucket.
                Dictionary<string, string>? vals = null;
                if (!DrawingRegionExtractor.HasAnyText(bytes))
                {
                    if (DrawingOcr.IsAvailable) vals = DrawingRegionExtractor.ExtractFields(bytes, sectionRect);
                    if (vals == null || string.IsNullOrWhiteSpace(vals.GetValueOrDefault("section", "")))
                    {
                        if (samples.Count < 5) samples.Add("(image — no text layer at all)");
                        var imgDest = Path.Combine(CalDir(token), "images", Path.GetFileName(path));
                        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(imgDest), StringComparison.OrdinalIgnoreCase))
                            System.IO.File.Move(path, imgDest);
                        imageCount++;
                        continue;
                    }
                }
                vals ??= DrawingRegionExtractor.ExtractFields(bytes, sectionRect);
                var section = vals.GetValueOrDefault("section", "").Trim();
                if (samples.Count < 5) samples.Add(string.IsNullOrEmpty(section) ? "(nothing — empty)" : section);
                string folder = section.Length > 0 && char.ToUpperInvariant(section[0]) == 'L' ? "angle"
                    : section.Length > 0 && char.ToUpperInvariant(section[0]) == 'P' ? "plate" : "other";
                var dest = Path.Combine(CalDir(token), folder, Path.GetFileName(path));
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Move(path, dest);
                if (folder == "angle") angleCount++; else if (folder == "plate") plateCount++; else otherCount++;
            }
            catch (Exception ex) { if (samples.Count < 5) samples.Add("(error reading page: " + ex.Message + ")"); otherCount++; try { System.IO.File.Move(path, Path.Combine(CalDir(token), "other", Path.GetFileName(path))); } catch { } }
        }
        return (angleCount, plateCount, otherCount, imageCount, samples);
    }

    // ---- First-time-only: mark the fields using this upload's own first drawing, right
    // here in Research's own flow — no batch, no detour. ----
    [HttpGet]
    // ---- If a big upload turns out to contain drawings from more than one template
    // (some end up correctly classified, a chunk ends up as "Other" because the
    // Section field sits somewhere different on those specific ones — e.g. one odd
    // sample drawing got shown during the first pass, so the box was marked against
    // the wrong layout for most of the batch), this lets just that "Other" group get
    // marked with its own field positions — without touching the ones that already
    // classified correctly. Can be run again and again if a third layout shows up too. ----
    [HttpGet]
    public IActionResult ResearchReassignOthers(string token)
    {
        var otherDir = Path.Combine(CalDir(token), "other");
        var skipped = GetSkippedSamples(token);
        string? first = null;
        if (Directory.Exists(otherDir))
        {
            foreach (var candidate in Directory.GetFiles(otherDir, "*.pdf").OrderBy(p => p))
            {
                if (skipped.Contains(Path.GetFileName(candidate))) continue;
                try { if (DrawingRegionExtractor.HasAnyText(System.IO.File.ReadAllBytes(candidate))) { first = candidate; break; } }
                catch { }
            }
        }
        if (first == null) { TempData["Err"] = "There's nothing left in Other to reassign."; return RedirectToAction("ResearchSplit"); }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(System.IO.File.ReadAllBytes(first));
            ViewBag.PreviewImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render a sample from Other: " + ex.Message;
            return RedirectToAction("ResearchSplit");
        }
        ViewBag.SampleFileName = Path.GetFileName(first);

        ViewBag.Token = token;
        ViewBag.BatchId = 0;
        ViewBag.IsResearch = true;
        ViewBag.IsReassignOthers = true;
        ViewBag.FileCount = Directory.GetFiles(otherDir, "*.pdf").Length;
        // Only these two matter here — classifying Angle vs Plate needs just the
        // Section value (Part No is optional, used only to name the output files).
        ViewBag.Fields = new (string Key, string Label)[]
        {
            ("partNo", "Part No / Item"), ("section", "Section (e.g. L45*5)"),
        };
        return View("Calibrate");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResearchApplyReassign(string token, string rectsJson)
    {
        if (string.IsNullOrWhiteSpace(rectsJson) || rectsJson == "{}")
        {
            TempData["Err"] = "Mark at least the Section box before continuing.";
            return RedirectToAction("ResearchReassignOthers", new { token });
        }

        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(rectsJson) ?? new();
            rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "Could not read what you marked — please try again."; return RedirectToAction("ResearchReassignOthers", new { token }); }

        if (!rects.ContainsKey("section"))
        {
            TempData["Err"] = "Please also mark the Section box, so Angle/Plate can be detected.";
            return RedirectToAction("ResearchReassignOthers", new { token });
        }

        // Only re-classifies whatever is currently sitting in "other" — files already
        // sorted into angle/plate from an earlier pass are left completely alone.
        var (angleCount, plateCount, otherCount, imageCount, samples) = ClassifyResearchFiles(token, rects, otherOnly: true);
        if (angleCount == 0 && plateCount == 0 && otherCount > 0)
        {
            TempData["Err"] = "Still couldn't find a valid Section using the box you just marked — " +
                "please check it's placed directly over the Section value and try again. " +
                "What was actually read from that spot: " + string.Join(" | ", samples) + " — " + DrawingOcr.Status;
            return RedirectToAction("ResearchReassignOthers", new { token });
        }

        TempData["Msg"] = $"Reassigned: {angleCount} became Angle, {plateCount} became Plate" +
            (otherCount > 0 ? $", {otherCount} still couldn't be classified — reassign again if they use yet another layout." : ".");
        return RedirectToAction("ResearchPlaceStamp", new { token, angleCount = 0, plateCount = 0, otherCount = 0 });
    }

    public IActionResult ResearchCalibrate(string token)
    {
        var dir = CalDir(token);
        // Files may already have been classified into angle/plate/other subfolders by
        // the time this is reached (e.g. redirected here after classification found
        // nothing usable) — search everywhere under the token folder, not just its root.
        // Skips any page with no text layer at all when picking which one to show for
        // marking — a scanned/photographed drawing has nothing there to find regardless
        // of where a box is drawn, so it wouldn't make a usable sample. Also skips any
        // file the user has manually marked "can't identify fields here" via the button
        // on the calibration page — that file has already been moved into Other.
        var skipped = GetSkippedSamples(token);
        string? first = null;
        if (Directory.Exists(dir))
        {
            foreach (var candidate in Directory.GetFiles(dir, "raw_*.pdf", SearchOption.AllDirectories).OrderBy(p => p))
            {
                if (skipped.Contains(Path.GetFileName(candidate))) continue;
                try { if (DrawingRegionExtractor.HasAnyText(System.IO.File.ReadAllBytes(candidate))) { first = candidate; break; } }
                catch { }
            }
        }
        if (first == null) { TempData["Err"] = "Upload session expired, or every drawing here is a scanned image with no text layer — please upload the drawings again."; return RedirectToAction("ResearchSplit"); }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(System.IO.File.ReadAllBytes(first));
            ViewBag.PreviewImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render the first drawing: " + ex.Message;
            return RedirectToAction("ResearchSplit");
        }
        ViewBag.SampleFileName = Path.GetFileName(first);

        ViewBag.Token = token;
        ViewBag.BatchId = 0;
        ViewBag.IsResearch = true;
        ViewBag.FileCount = Directory.GetFiles(dir, "raw_*.pdf", SearchOption.AllDirectories).Length;
        ViewBag.Fields = new (string Key, string Label)[]
        {
            ("partNo", "Part No / Item"), ("section", "Section (e.g. L45*5)"),
        };
        return View("Calibrate");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResearchApplyCalibration(string token, string rectsJson, string? nameField)
    {
        if (string.IsNullOrWhiteSpace(rectsJson) || rectsJson == "{}")
        {
            TempData["Err"] = "Mark at least the Section box before continuing.";
            return RedirectToAction("ResearchCalibrate", new { token });
        }
        if (string.IsNullOrWhiteSpace(token) || !Directory.Exists(CalDir(token)))
        {
            TempData["Err"] = "Your upload session expired — please upload the drawings again.";
            return RedirectToAction("ResearchSplit");
        }

        try
        {
            var saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Research");
            if (saved == null) { saved = new SavedCalibration { Name = "Research" }; _db.SavedCalibrations.Add(saved); }
            saved.RectsJson = rectsJson;
            saved.NameField = nameField;
            saved.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
            CalibrationBackup.Save(saved);

            Dictionary<string, DrawingRegionExtractor.NormRect> rects;
            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(rectsJson) ?? new();
                rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
            }
            catch { TempData["Err"] = "Could not read what you marked — please try again."; return RedirectToAction("ResearchCalibrate", new { token }); }

            if (!rects.ContainsKey("section"))
            {
                TempData["Err"] = "Please also mark the Section box, so Angle/Plate can be detected.";
                return RedirectToAction("ResearchCalibrate", new { token });
            }

            var (angleCount, plateCount, otherCount, imageCount, samples) = ClassifyResearchFiles(token, rects);
            if (angleCount == 0 && plateCount == 0 && otherCount > 0)
            {
                // Just marked, and still found nothing readable as a Section on any
                // drawing. With a large, mixed upload this can genuinely happen even
                // with a correctly-placed box (different page sizes/layouts shift where
                // a percentage-based box lands) — so this proceeds as "all Other" rather
                // than blocking, and "Reassign Others" on the next screen is the way to
                // actually fix it, rather than looping back here with no way forward.
                TempData["Msg"] = "Couldn't find a valid Section using the box you just marked — everything's been put in " +
                    "Other for now. If the box really was over the Section value, these drawings may use a different page " +
                    "size — use \"Reassign Others\" on the next screen. What was actually read: " + string.Join(" | ", samples) + " — " + DrawingOcr.Status;
            }
            return RedirectToAction("ResearchPlaceStamp", new { token, angleCount, plateCount, otherCount });
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Something went wrong while applying the layout: " + ex.Message;
            return RedirectToAction("ResearchCalibrate", new { token });
        }
    }

    // ---- Place the stamp once, on this upload's own first drawing, before it's applied
    // to every drawing that gets sent out. ----
    // Re-renders just the stamp preview at a given font size, so the "Update preview" button
    // can show the real effect without a full page reload.
    [HttpGet]
    public async Task<IActionResult> ResearchPreviewStamp(double fontScale)
    {
        var me = await MeAsync();
        var png = DocControlStampRenderer.Render(me?.Sector ?? "R & D Sector", DateTime.Now.ToString("d/ M / yyyy"), me?.DocIssue ?? "1", "14AA", fontScale);
        return File(png, "image/png");
    }

    [HttpGet]
    public async Task<IActionResult> ResearchPlaceStamp(string token, int angleCount, int plateCount, int otherCount)
    {
        var me = await MeAsync();
        if (me == null) return RedirectToAction("Index");

        // Any one drawing from this upload works as the sample — pick whichever group has files.
        var dir = CalDir(token);
        string? samplePath = null;
        foreach (var folder in new[] { "angle", "plate", "other", "images" })
        {
            var candidate = Directory.Exists(Path.Combine(dir, folder))
                ? Directory.GetFiles(Path.Combine(dir, folder), "*.pdf").OrderBy(p => p).FirstOrDefault() : null;
            if (candidate != null) { samplePath = candidate; break; }
        }
        if (samplePath == null) { TempData["Err"] = "Upload session expired — please upload the drawings again."; return RedirectToAction("ResearchSplit"); }

        // Always the real, current count of files actually sitting in each folder —
        // more reliable than trusting counts passed through a chain of redirects,
        // especially once "Reassign others" can move files between folders on its own.
        angleCount = Directory.Exists(Path.Combine(dir, "angle")) ? Directory.GetFiles(Path.Combine(dir, "angle"), "*.pdf").Length : 0;
        plateCount = Directory.Exists(Path.Combine(dir, "plate")) ? Directory.GetFiles(Path.Combine(dir, "plate"), "*.pdf").Length : 0;
        otherCount = Directory.Exists(Path.Combine(dir, "other")) ? Directory.GetFiles(Path.Combine(dir, "other"), "*.pdf").Length : 0;
        int imageCount = Directory.Exists(Path.Combine(dir, "images")) ? Directory.GetFiles(Path.Combine(dir, "images"), "*.pdf").Length : 0;

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(await System.IO.File.ReadAllBytesAsync(samplePath));
            ViewBag.DrawingSrc = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render a sample drawing: " + ex.Message;
            return RedirectToAction("ResearchSplit");
        }

        ViewBag.Token = token;
        ViewBag.AngleCount = angleCount; ViewBag.PlateCount = plateCount; ViewBag.OtherCount = otherCount; ViewBag.ImageCount = imageCount;
        var png = DocControlStampRenderer.Render(me.Sector ?? "R & D Sector", DateTime.Now.ToString("d/ M / yyyy"), me.DocIssue ?? "1", "14AA", me.AutoStampFontScale);
        ViewBag.PreviewSrc = "data:image/png;base64," + Convert.ToBase64String(png);
        ViewBag.NormX = me.AutoStampNormX; ViewBag.NormY = me.AutoStampNormY;
        ViewBag.AnchorRight = me.AutoStampAnchorRight; ViewBag.AnchorBottom = me.AutoStampAnchorBottom;
        ViewBag.WidthPct = me.AutoStampWidthPct; ViewBag.FontScale = me.AutoStampFontScale;
        return View();
    }

    [HttpGet]
    // ---- Preview every drawing in a group with the current stamp settings, before sending ----
    [HttpGet]
    public IActionResult ResearchPreviewGroup(string token, string group,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale)
    {
        var dir = Path.Combine(CalDir(token), group);
        if (!Directory.Exists(dir)) { TempData["Err"] = "Nothing to preview in this group."; return RedirectToAction("ResearchSplit"); }
        var files = Directory.GetFiles(dir, "*.pdf").OrderBy(p => p).Select(Path.GetFileName).ToList();
        var overridden = files.Where(f => FileOverrideExists(token, group, f!)).ToHashSet();

        ViewBag.Token = token; ViewBag.Group = group; ViewBag.Files = files; ViewBag.Overridden = overridden;
        ViewBag.NormX = normX; ViewBag.NormY = normY; ViewBag.AnchorRight = anchorRight; ViewBag.AnchorBottom = anchorBottom;
        ViewBag.WidthPct = widthPct; ViewBag.FontScale = fontScale;
        return View();
    }

    private class StampOverride
    {
        public double NormX { get; set; }
        public double NormY { get; set; }
        public bool AnchorRight { get; set; }
        public bool AnchorBottom { get; set; }
        public double WidthPct { get; set; }
        public double FontScale { get; set; }
    }

    private static string OverridePath(string token, string group, string file) =>
        Path.Combine(CalDir(token), group, file + ".stamp.json");

    private static bool FileOverrideExists(string token, string group, string file) =>
        System.IO.File.Exists(OverridePath(token, group, file));

    private static StampOverride? ReadFileOverride(string token, string group, string file)
    {
        var path = OverridePath(token, group, file);
        if (!System.IO.File.Exists(path)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<StampOverride>(System.IO.File.ReadAllText(path)); }
        catch { return null; }
    }

    // ---- Tracks which files the user has manually marked "can't identify fields on
    // this one" from the calibration page — those are excluded from being offered as a
    // sample again, without disturbing the search-everywhere logic other flows rely on. ----
    private static string SkippedSamplesPath(string token) => Path.Combine(CalDir(token), "_skipped_samples.txt");

    private static HashSet<string> GetSkippedSamples(string token)
    {
        var path = SkippedSamplesPath(token);
        if (!System.IO.File.Exists(path)) return new HashSet<string>();
        try { return new HashSet<string>(System.IO.File.ReadAllLines(path), StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(); }
    }

    private static void AddSkippedSample(string token, string fileName)
    {
        try { System.IO.File.AppendAllLines(SkippedSamplesPath(token), new[] { fileName }); } catch { }
    }

    // ---- "Can't identify fields on this page" button on the calibration screen — moves
    // just this one file into Other (it's clearly not representative of the rest of the
    // upload) and shows the next candidate instead, rather than forcing the user to guess
    // at boxes on a page they can't actually read. ----
    [HttpGet]
    public IActionResult ResearchSkipSample(string token, string fileName, bool isReassign = false)
    {
        // Wherever this file currently sits (root, or already inside another bucket from
        // a prior pass), move it into "other" — this is exactly what "can't identify
        // fields here" means for this file.
        var match = Directory.Exists(CalDir(token))
            ? Directory.GetFiles(CalDir(token), fileName, SearchOption.AllDirectories).FirstOrDefault() : null;
        if (match != null)
        {
            var otherDir = Path.Combine(CalDir(token), "other");
            Directory.CreateDirectory(otherDir);
            var dest = Path.Combine(otherDir, fileName);
            if (!string.Equals(Path.GetFullPath(match), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                try { System.IO.File.Move(match, dest, overwrite: true); } catch { }
            }
        }
        AddSkippedSample(token, fileName);
        TempData["Msg"] = $"\"{fileName}\" moved to Other — showing the next drawing to mark instead.";
        return RedirectToAction(isReassign ? "ResearchReassignOthers" : "ResearchCalibrate", new { token });
    }

    // ---- Reposition the stamp on one specific drawing, before sending — doesn't touch any other file ----
    [HttpGet]
    public async Task<IActionResult> ResearchAdjustFile(string token, string group, string file,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale)
    {
        var path = Path.Combine(CalDir(token), group, file);
        if (!System.IO.File.Exists(path)) { TempData["Err"] = "That drawing isn't available anymore — the upload session may have expired."; return RedirectToAction("ResearchSplit"); }

        // What's shown/edited here starts from the override if one exists, else the shared
        // (fallback) settings passed in — but the fallback itself is always remembered
        // separately, so "back to preview" and "revert" still know the shared position.
        double useNormX = normX, useNormY = normY, useWidthPct = widthPct, useFontScale = fontScale;
        bool useAnchorRight = anchorRight, useAnchorBottom = anchorBottom;
        var existing = ReadFileOverride(token, group, file);
        if (existing != null)
        {
            useNormX = existing.NormX; useNormY = existing.NormY;
            useAnchorRight = existing.AnchorRight; useAnchorBottom = existing.AnchorBottom;
            useWidthPct = existing.WidthPct; useFontScale = existing.FontScale;
        }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(await System.IO.File.ReadAllBytesAsync(path));
            ViewBag.DrawingSrc = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex) { TempData["Err"] = "Could not render this drawing: " + ex.Message; return RedirectToAction("ResearchSplit"); }

        var me = await MeAsync();
        var stampPng = DocControlStampRenderer.Render(me?.Sector ?? "R & D Sector", DateTime.Now.ToString("d/ M / yyyy"), me?.DocIssue ?? "1", "—", useFontScale);
        ViewBag.PreviewSrc = "data:image/png;base64," + Convert.ToBase64String(stampPng);
        ViewBag.Token = token; ViewBag.Group = group; ViewBag.File = file;
        ViewBag.NormX = useNormX; ViewBag.NormY = useNormY; ViewBag.AnchorRight = useAnchorRight; ViewBag.AnchorBottom = useAnchorBottom;
        ViewBag.WidthPct = useWidthPct; ViewBag.FontScale = useFontScale;
        ViewBag.FallbackNormX = normX; ViewBag.FallbackNormY = normY; ViewBag.FallbackAnchorRight = anchorRight; ViewBag.FallbackAnchorBottom = anchorBottom;
        ViewBag.FallbackWidthPct = widthPct; ViewBag.FallbackFontScale = fontScale;
        ViewBag.HasOverride = existing != null;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResearchAdjustFile(string token, string group, string file,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale,
        double fallbackNormX, double fallbackNormY, bool fallbackAnchorRight, bool fallbackAnchorBottom, double fallbackWidthPct, double fallbackFontScale)
    {
        var path = Path.Combine(CalDir(token), group, file);
        if (!System.IO.File.Exists(path)) { TempData["Err"] = "That drawing isn't available anymore."; return RedirectToAction("ResearchSplit"); }
        var over = new StampOverride
        {
            NormX = Math.Clamp(normX, 0, 1),
            NormY = Math.Clamp(normY, 0, 1),
            AnchorRight = anchorRight,
            AnchorBottom = anchorBottom,
            WidthPct = Math.Clamp(widthPct, 5, 60),
            FontScale = Math.Clamp(fontScale, 0.4, 2.5),
        };
        System.IO.File.WriteAllText(OverridePath(token, group, file), System.Text.Json.JsonSerializer.Serialize(over));
        TempData["Msg"] = "This drawing will use its own stamp position.";
        return RedirectToAction("ResearchPreviewGroup", new
        {
            token,
            group,
            normX = fallbackNormX,
            normY = fallbackNormY,
            anchorRight = fallbackAnchorRight,
            anchorBottom = fallbackAnchorBottom,
            widthPct = fallbackWidthPct,
            fontScale = fallbackFontScale,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResearchClearFileOverride(string token, string group, string file,
        double fallbackNormX, double fallbackNormY, bool fallbackAnchorRight, bool fallbackAnchorBottom, double fallbackWidthPct, double fallbackFontScale)
    {
        var path = OverridePath(token, group, file);
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
        TempData["Msg"] = "Reverted to the shared stamp position for this drawing.";
        return RedirectToAction("ResearchAdjustFile", new
        {
            token,
            group,
            file,
            normX = fallbackNormX,
            normY = fallbackNormY,
            anchorRight = fallbackAnchorRight,
            anchorBottom = fallbackAnchorBottom,
            widthPct = fallbackWidthPct,
            fontScale = fallbackFontScale,
        });
    }

    // Renders one file, stamped with the given settings, for the preview grid — nothing is saved here.
    [HttpGet]
    public async Task<IActionResult> ResearchPreviewFile(string token, string group, string file,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale)
    {
        var me = await MeAsync();
        var path = Path.Combine(CalDir(token), group, file);
        if (!System.IO.File.Exists(path)) return NotFound();

        var over = ReadFileOverride(token, group, file);
        if (over != null) { normX = over.NormX; normY = over.NormY; anchorRight = over.AnchorRight; anchorBottom = over.AnchorBottom; widthPct = over.WidthPct; fontScale = over.FontScale; }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(await System.IO.File.ReadAllBytesAsync(path));
            var stampPng = DocControlStampRenderer.Render(me?.Sector ?? "R & D Sector", DateTime.Now.ToString("d/ M / yyyy"), me?.DocIssue ?? "1", "—", fontScale);
            var composited = DocControlStampRenderer.Composite(preview.Png, stampPng, normX, normY, anchorRight, anchorBottom, widthPct);
            return File(composited, "image/png");
        }
        catch { return NotFound(); }
    }

    public async Task<IActionResult> ResearchConfirm(string token, int angleCount, int plateCount, int otherCount, int imageCount,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale = 1.0)
    {
        ViewBag.Token = token;
        ViewBag.AngleCount = angleCount;
        ViewBag.PlateCount = plateCount;
        ViewBag.OtherCount = otherCount;
        ViewBag.ImageCount = imageCount;
        ViewBag.NormX = normX; ViewBag.NormY = normY;
        ViewBag.AnchorRight = anchorRight; ViewBag.AnchorBottom = anchorBottom;
        ViewBag.WidthPct = widthPct; ViewBag.FontScale = fontScale;
        ViewBag.Destinations = await DestinationsAsync();

        var angleWorkshop = await _db.Users.FirstOrDefaultAsync(u => u.IsAngleWorkshop);
        var plateWorkshop = await _db.Users.FirstOrDefaultAsync(u => u.IsPlateWorkshop);
        var quality = await _db.Users.FirstOrDefaultAsync(u => u.Username == "quality");
        var emanMahmoud = await _db.Users.FirstOrDefaultAsync(u => u.Username == "eman.mahmoud");
        ViewBag.CoreDestinations = new[] { angleWorkshop, plateWorkshop, quality, emanMahmoud }
            .Where(u => u != null).Select(u => u!).DistinctBy(u => u.Id).ToList();
        ViewBag.AngleWorkshopId = angleWorkshop?.Id;
        ViewBag.PlateWorkshopId = plateWorkshop?.Id;
        ViewBag.QualityId = quality?.Id;
        ViewBag.EmanMahmoudId = emanMahmoud?.Id;

        var projectFile = Path.Combine(CalDir(token), "_project.txt");
        ViewBag.ProjectName = System.IO.File.Exists(projectFile) ? await System.IO.File.ReadAllTextAsync(projectFile) : "";
        var batchFile = Path.Combine(CalDir(token), "_batch.txt");
        ViewBag.BatchName = System.IO.File.Exists(batchFile) ? await System.IO.File.ReadAllTextAsync(batchFile) : "";
        // Projects already in the system, for the picker — newest activity first.
        ViewBag.ExistingProjects = await _db.Projects.AsNoTracking().OrderBy(p => p.Name).Select(p => p.Name).ToListAsync();
        return View("ResearchConfirm");
    }

    private async Task<List<SelectListItem>> DestinationsAsync()
    {
        // The Other group (unrecognized Section) can go to any workshop/Quality/Planning
        // account — Research isn't limited to the fixed Angle/Plate routing here, since
        // there's no automatic rule to fall back on for these.
        return await _db.Users.Where(u => u.IsAngleWorkshop || u.IsPlateWorkshop || u.Username == "quality" || u.IsPlanner)
            .OrderBy(u => u.Department).ThenBy(u => u.FullName)
            .Select(u => new SelectListItem($"{u.FullName} — {u.Department}" + (u.StampCode != null ? $" ({u.StampCode})" : ""), u.Id.ToString()))
            .ToListAsync();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResearchSplitSend(string token, string? projectName, List<int>? otherToUserIds, int? imageToUserId, string? stampDate,
        double? normX, double? normY, bool anchorRight = true, bool anchorBottom = true, double? widthPct = null,
        double? fontScale = null, List<int>? angleToUserIds = null, List<int>? plateToUserIds = null,
        IFormFile? partListFile = null, string? batchName = null)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin)) { TempData["Err"] = "Not allowed."; return RedirectToAction("Index"); }

        // Remember what was typed, so a bounce back to the confirm page doesn't make Research retype it.
        try
        {
            if (!string.IsNullOrWhiteSpace(projectName))
                await System.IO.File.WriteAllTextAsync(Path.Combine(CalDir(token), "_project.txt"), projectName.Trim());
            if (!string.IsNullOrWhiteSpace(batchName))
                await System.IO.File.WriteAllTextAsync(Path.Combine(CalDir(token), "_batch.txt"), batchName.Trim());
        }
        catch { }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            TempData["Err"] = "Give this delivery a project name before sending.";
            return RedirectBackToConfirm(token, normX, normY, anchorRight, anchorBottom, widthPct, fontScale);
        }
        if (string.IsNullOrWhiteSpace(batchName))
        {
            TempData["Err"] = "Give this delivery a batch name before sending.";
            return RedirectBackToConfirm(token, normX, normY, anchorRight, anchorBottom, widthPct, fontScale);
        }
        if (projectName.Trim().Length > 100 || batchName.Trim().Length > 100)
        {
            TempData["Err"] = "The project and batch names must be 100 characters or fewer.";
            return RedirectBackToConfirm(token, normX, normY, anchorRight, anchorBottom, widthPct, fontScale);
        }

        // Optional Part List PDF — checked up front, before anything is created, so a bad
        // file stops the send cleanly instead of leaving a half-sent delivery behind.
        byte[]? partListBytes = null;
        string partListName = "";
        if (partListFile != null && partListFile.Length > 0)
        {
            var (bytes, partListError) = await ReadPartListAsync(partListFile);
            if (partListError != null)
            {
                TempData["Err"] = partListError;
                return RedirectBackToConfirm(token, normX, normY, anchorRight, anchorBottom, widthPct, fontScale);
            }
            var templateError = PartListTemplateError(bytes!);
            if (templateError != null)
            {
                TempData["Err"] = templateError;
                return RedirectBackToConfirm(token, normX, normY, anchorRight, anchorBottom, widthPct, fontScale);
            }
            partListBytes = bytes;
            partListName = Path.GetFileName(partListFile.FileName);
        }

        var saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Research");
        Dictionary<string, DrawingRegionExtractor.NormRect> rects = new();
        if (saved != null)
        {
            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(saved.RectsJson) ?? new();
                rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
            }
            catch { }
        }

        // Angle and Plate each go to whoever was checked for that group — no fixed
        // assumption about which workshop goes with which shape. "Other" (unrecognized
        // section) is still picked from a single dropdown.
        var angleTargets = angleToUserIds != null && angleToUserIds.Count > 0
            ? await _db.Users.Where(u => angleToUserIds.Contains(u.Id)).ToListAsync() : new List<AppUser>();
        var plateTargets = plateToUserIds != null && plateToUserIds.Count > 0
            ? await _db.Users.Where(u => plateToUserIds.Contains(u.Id)).ToListAsync() : new List<AppUser>();

        // Research names the project when sending — if one with this exact name already
        // exists, every new batch joins it instead of creating a duplicate, so the same
        // project keeps accumulating batches across separate uploads over time.
        int? projectId = null;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var trimmedName = projectName.Trim();
            var existingProject = await _db.Projects.FirstOrDefaultAsync(p => p.Name == trimmedName);
            if (existingProject == null)
            {
                existingProject = new TowerProcessSheet.Models.Project { Name = trimmedName, CreatedById = me.Id };
                _db.Projects.Add(existingProject);
                await _db.SaveChangesAsync();
            }
            projectId = existingProject.Id;
            if (partListBytes != null) await StorePartListAsync(existingProject.Id, partListName, partListBytes, me);
        }

        var otherTargets = otherToUserIds != null && otherToUserIds.Count > 0
            ? await _db.Users.Where(u => otherToUserIds.Contains(u.Id)).ToListAsync() : new List<AppUser>();
        var imageTargets = imageToUserId.HasValue
            ? await _db.Users.Where(u => u.Id == imageToUserId.Value).ToListAsync() : new List<AppUser>();

        var groups = new (string Folder, string Label, List<AppUser> Targets)[]
        {
            ("angle", "Angle", angleTargets), ("plate", "Plate", plateTargets),
            ("other", "Other", otherTargets), ("images", "Scanned image", imageTargets),
        };

        var summary = new List<string>();
        bool hadSkippedGroup = false;
        var sentBatchIds = new List<int>();
        double useNormX = normX ?? me.AutoStampNormX, useNormY = normY ?? me.AutoStampNormY;
        double useWidthPct = widthPct ?? me.AutoStampWidthPct;
        double useFontScale = fontScale ?? me.AutoStampFontScale;
        string dateStr = DateTime.TryParse(stampDate, out var dParsed) ? dParsed.ToString("d/ M / yyyy") : DateTime.Now.ToString("d/ M / yyyy");
        foreach (var g in groups)
        {
            var dir = Path.Combine(CalDir(token), g.Folder);
            // Each source file remembers which folder it actually came from — needed so
            // it gets flagged as unclassified (Other/Images) or not (genuine Angle/Plate).
            var sourcePaths = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.pdf").Select(p => (Path: p, OriginFolder: g.Folder)).ToList()
                : new List<(string Path, string OriginFolder)>();
            if (sourcePaths.Count == 0) continue;
            if (g.Targets.Count == 0)
            {
                // Files exist for this group but nothing was picked to receive them —
                // left in place (nothing lost), but silently doing nothing here reads
                // as a bug later, so it's called out loudly in the summary instead.
                summary.Add($"⚠ {sourcePaths.Count} {g.Label} drawing(s) were NOT sent — no destination was picked for this group.");
                hadSkippedGroup = true;
                continue;
            }

            // Each destination reads the same source files directly — no redundant
            // per-target copy is made anymore (removed: was doubling disk usage for
            // every extra destination, which is what caused "not enough disk space" on
            // a large upload). The source file is only deleted once every destination
            // has had its turn (isLastTarget below).
            for (int targetIdx = 0; targetIdx < g.Targets.Count; targetIdx++)
            {
                var target = g.Targets[targetIdx];
                bool isLastTarget = targetIdx == g.Targets.Count - 1;

                var batch = new Batch
                {
                    Name = $"{batchName.Trim()} — {g.Label} — {target.FullName}",
                    ProjectName = projectName?.Trim(),
                    ProjectId = projectId,
                    CreatedById = target.Id,
                    CreatedAt = DateTime.Now,
                    Status = BatchStatus.Draft,
                    PartListShared = partListBytes != null,
                };
                _db.Batches.Add(batch);
                await _db.SaveChangesAsync();
                batch.Transfers.Add(new BatchTransfer { FromUserId = me.Id, ToUserId = target.Id, Action = "Assigned", At = DateTime.Now });

                string sector = me.Sector ?? "R & D Sector", issue = me.DocIssue ?? "1", copyNo = target.StampCode ?? "—";
                int createdTotal = 0;
                var failedTotal = new List<string>();

                // Phase 1: the expensive part (reading each PDF, rendering it to an image,
                // compositing the stamp) runs across all available CPU cores at once instead
                // of one file at a time — this is what used to make a large send take hours.
                // Each task only touches its own file and returns a plain result; nothing
                // here touches the database, so there's no concurrency risk in doing this
                // in parallel.
                var textRects = rects.Where(r => r.Key != "diagram").ToDictionary(r => r.Key, r => r.Value);
                DrawingImageExtractor.NormRect? diagramRect = rects.TryGetValue("diagram", out var dr)
                    ? new DrawingImageExtractor.NormRect(dr.X, dr.Y, dr.W, dr.H) : null;
                var buildResults = new BuiltSheetResult[sourcePaths.Count];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, sourcePaths.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
                    async (idx, ct) =>
                    {
                        var (src, originFolder) = sourcePaths[idx];
                        buildResults[idx] = await BuildSheetAsync(src, originFolder, textRects, diagramRect, saved?.NameField, token,
                            sector, dateStr, issue, copyNo, useNormX, useNormY, anchorRight, anchorBottom, useWidthPct, useFontScale);
                    });

                // Phase 2: fast, sequential database writes only — every field is already
                // computed, so this is just object creation and one save at the end, not the
                // per-file save-then-look-up-then-update round trip this used to need.
                for (int idx = 0; idx < sourcePaths.Count; idx++)
                {
                    var (src, originFolder) = sourcePaths[idx];
                    var r = buildResults[idx];
                    if (!r.Success) { failedTotal.Add(r.FailReason ?? r.SrcFileName); continue; }

                    var sheet = new ProcessSheet
                    {
                        BatchId = batch.Id,
                        DrawingNo = string.IsNullOrWhiteSpace(r.PartNo) ? r.DrawingNo : r.PartNo.Trim() + "_dr",
                        PartNo = r.PartNo,
                        Section = r.Section,
                        Material = r.Material,
                        CutLength = r.CutLength,
                        ProjectName = string.IsNullOrWhiteSpace(r.ProjectName) ? (batch.ProjectName ?? "") : r.ProjectName,
                        IncludeGalvanizing = false,
                        SheetNo = "1/1",
                        OriginalPdf = r.OriginalPdf,
                        FullDrawingImage = r.FullDrawingImage,
                        FullDrawingMime = r.FullDrawingImage != null ? "image/png" : null,
                        DiagramImage = r.DiagramImage,
                        DiagramMime = r.DiagramImage != null ? "image/png" : null,
                        ResearchGroupKey = token + "_" + r.SrcFileName,
                        IsUnclassified = originFolder == "other" || originFolder == "images",
                        IsScannedImage = originFolder == "images",
                        StampSector = sector,
                        StampDate = dateStr,
                        StampIssue = issue,
                        StampCopyNo = copyNo,
                        StampNormX = r.NormX,
                        StampNormY = r.NormY,
                        StampAnchorRight = r.AnchorRight,
                        StampAnchorBottom = r.AnchorBottom,
                        StampWidthPct = r.WidthPct,
                        StampFontScale = r.FontScale,
                        StampedDrawingImage = r.StampedDrawingImage,
                    };
                    if (r.Qty.HasValue) sheet.Qty = r.Qty.Value;
                    sheet.Operations.Add(new ProcessOperation { OperationKey = "CUT_DRILL" });
                    BatchFooter.ApplyTo(batch, sheet);
                    _db.ProcessSheets.Add(sheet);
                    createdTotal++;

                    // Rename a copy of the original PDF to "{value}_pr.pdf" for download —
                    // done here, sequentially, since generating a unique name checks disk
                    // state and isn't safe to do from multiple parallel tasks at once.
                    if (r.NamingValue != "")
                    {
                        try { System.IO.File.Copy(src, UniqueRenamedPath(token, r.NamingValue), overwrite: false); } catch { }
                    }
                }
                // Only deletes once every destination for this group has had its turn to
                // read it — deleting after the first destination would break the next one
                // still waiting to process the same shared file.
                if (isLastTarget)
                {
                    foreach (var (src2, _) in sourcePaths) { try { System.IO.File.Delete(src2); } catch { } }
                }

                await _db.SaveChangesAsync();
                summary.Add($"{g.Label} → {target.FullName} ({target.StampCode}): {createdTotal} sheet(s)" +
                    (failedTotal.Count > 0 ? $", {failedTotal.Count} unread — e.g. {string.Join(" | ", failedTotal.Take(3))}" : ""));
                sentBatchIds.Add(batch.Id);
            }
        }

        // Only cleans up when nothing was skipped — a skipped group's files stay on disk
        // under this token so they aren't lost; the message below points back to
        // Confirm & Send to pick a destination for what's left.
        if (!hadSkippedGroup) { try { Directory.Delete(CalDir(token), true); } catch { /* best effort cleanup */ } }

        if (sentBatchIds.Count == 0 && !hadSkippedGroup) { TempData["Err"] = "Nothing to send."; return RedirectToAction("ResearchSplit"); }
        TempData["Msg"] = string.Join(" · ", summary) +
            (hadSkippedGroup ? $" Go back to Confirm & Send with this token to pick a destination for what's left: {token}" : "");
        return RedirectToAction("ResearchSent", new { ids = string.Join(",", sentBatchIds) });
    }

    // ---- What was just sent, to whom, with a link to review/fix each batch's stamp ----
    [HttpGet]
    public async Task<IActionResult> ResearchSent(string ids)
    {
        var idList = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        var batches = await _db.Batches.Where(b => idList.Contains(b.Id))
            .Include(b => b.CreatedBy)
            .ToListAsync();
        var reachedAt = await _db.BatchTransfers.Where(t => idList.Contains(t.BatchId) && t.Action == "Assigned")
            .GroupBy(t => t.BatchId).Select(g => new { BatchId = g.Key, At = g.Max(t => t.At) })
            .ToDictionaryAsync(x => x.BatchId, x => x.At);
        var sent = new List<SentBatchRow>();
        foreach (var b in batches)
        {
            var sheets = await _db.ProcessSheets.Where(s => s.BatchId == b.Id)
                .Select(s => new SentSheetRow { Id = s.Id, DrawingNo = s.DrawingNo, PartNo = s.PartNo, Section = s.Section })
                .ToListAsync();
            sent.Add(new SentBatchRow
            {
                Id = b.Id,
                Name = b.Name,
                ProjectName = b.ProjectName,
                ToName = b.CreatedBy?.FullName ?? "—",
                ToCode = b.CreatedBy?.StampCode ?? "—",
                SheetCount = sheets.Count,
                Sheets = sheets,
                CreatedAt = b.CreatedAt,
                ReachedAt = reachedAt.TryGetValue(b.Id, out var at) ? at : null,
            });
        }
        ViewBag.Sent = sent;
        return View();
    }

    public class SentSheetRow
    {
        public int Id { get; set; }
        public string DrawingNo { get; set; } = "";
        public string PartNo { get; set; } = "";
        public string Section { get; set; } = "";
    }

    public class AssignedSheetRow
    {
        public int Id { get; set; }
        public string DrawingNo { get; set; } = "";
        public string PartNo { get; set; } = "";
        public string Section { get; set; } = "";
        public bool ConvertedToProcess { get; set; }
        public bool IsReviewedByMe { get; set; }
        public bool IsRejectedByMe { get; set; }
        public bool WasResent { get; set; }
        public int? BatchId { get; set; }
        public string? BatchName { get; set; }
        public string? FromName { get; set; }
    }

    public class SentBatchRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? ProjectName { get; set; }
        public string ToName { get; set; } = "";
        public string ToCode { get; set; } = "";
        public int SheetCount { get; set; }
        public List<SentSheetRow> Sheets { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime? ReachedAt { get; set; }
    }

    // ---- Fix one sheet's stamp without touching any other sheet or batch ----
    // Where Save / Cancel return to: the page the researcher came from (the page with all the
    // drawings, for review) — `ret` carries it; without it, that sheet's own batch on the Sent page.
    private string ResearchFixReturnUrl(string? ret, ProcessSheet sheet)
    {
        if (!string.IsNullOrEmpty(ret) && Url.IsLocalUrl(ret)) return ret;
        return sheet.BatchId.HasValue
            ? Url.Action("ResearchSent", "Batches", new { ids = sheet.BatchId.Value.ToString() })!
            : Url.Action("ResearchHistory", "Batches")!;
    }

    [HttpGet]
    public async Task<IActionResult> ResearchFixStamp(int id, string? ret = null)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        var basisImage = (sheet.FullDrawingImage != null && sheet.FullDrawingImage.Length > 0) ? sheet.FullDrawingImage
            : (sheet.DiagramImage != null && sheet.DiagramImage.Length > 0) ? sheet.DiagramImage : null;
        if (basisImage == null) { TempData["Err"] = "No drawing image is stored for this sheet."; return Redirect(ResearchFixReturnUrl(ret, sheet)); }

        ViewBag.ReturnUrl = ResearchFixReturnUrl(ret, sheet);
        ViewBag.SheetId = id;
        ViewBag.DrawingSrc = "data:image/png;base64," + Convert.ToBase64String(basisImage);
        var png = DocControlStampRenderer.Render(sheet.StampSector ?? "R & D Sector", sheet.StampDate ?? DateTime.Now.ToString("d/ M / yyyy"),
            sheet.StampIssue ?? "1", sheet.StampCopyNo ?? "—", sheet.StampFontScale);
        ViewBag.PreviewSrc = "data:image/png;base64," + Convert.ToBase64String(png);
        ViewBag.NormX = sheet.StampNormX; ViewBag.NormY = sheet.StampNormY;
        ViewBag.AnchorRight = sheet.StampAnchorRight; ViewBag.AnchorBottom = sheet.StampAnchorBottom;
        ViewBag.WidthPct = sheet.StampWidthPct; ViewBag.FontScale = sheet.StampFontScale;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResearchFixStamp(int id, double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale, string? ret = null)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        var basisImage = (sheet.FullDrawingImage != null && sheet.FullDrawingImage.Length > 0) ? sheet.FullDrawingImage
            : (sheet.DiagramImage != null && sheet.DiagramImage.Length > 0) ? sheet.DiagramImage : null;
        if (basisImage == null) { TempData["Err"] = "No drawing image is stored for this sheet."; return Redirect(ResearchFixReturnUrl(ret, sheet)); }

        sheet.StampNormX = Math.Clamp(normX, 0, 1); sheet.StampNormY = Math.Clamp(normY, 0, 1);
        sheet.StampAnchorRight = anchorRight; sheet.StampAnchorBottom = anchorBottom;
        sheet.StampWidthPct = Math.Clamp(widthPct, 5, 60);
        sheet.StampFontScale = Math.Clamp(fontScale, 0.4, 2.5);
        try
        {
            var png = DocControlStampRenderer.Render(sheet.StampSector ?? "R & D Sector", sheet.StampDate ?? DateTime.Now.ToString("d/ M / yyyy"),
                sheet.StampIssue ?? "1", sheet.StampCopyNo ?? "—", sheet.StampFontScale);
            sheet.StampedDrawingImage = DocControlStampRenderer.Composite(basisImage, png, sheet.StampNormX, sheet.StampNormY, sheet.StampAnchorRight, sheet.StampAnchorBottom, sheet.StampWidthPct);
        }
        catch (Exception ex) { TempData["Err"] = "Could not re-stamp this drawing: " + ex.Message; return RedirectToAction("ResearchFixStamp", new { id, ret }); }

        await _db.SaveChangesAsync();
        TempData["Msg"] = "Stamp updated for this drawing.";
        return Redirect(ResearchFixReturnUrl(ret, sheet));
    }

    // ---- Everything Research has ever sent, for lookup later ----
    [HttpGet]
    public async Task<IActionResult> ResearchHistory()
    {
        var me = await MeAsync();
        if (me == null) return RedirectToAction("Index");
        var transfers = await _db.BatchTransfers.Where(t => t.FromUserId == me.Id && t.Action == "Assigned")
            .Include(t => t.ToUser).Include(t => t.Batch)
            .OrderByDescending(t => t.At)
            .ToListAsync();
        var history = new List<SentBatchRow>();
        foreach (var t in transfers.Where(t => t.Batch != null))
        {
            var sheets = await _db.ProcessSheets.Where(s => s.BatchId == t.Batch!.Id)
                .Select(s => new SentSheetRow { Id = s.Id, DrawingNo = s.DrawingNo, PartNo = s.PartNo, Section = s.Section })
                .ToListAsync();
            history.Add(new SentBatchRow
            {
                Id = t.Batch!.Id,
                Name = t.Batch.Name,
                ProjectName = t.Batch.ProjectName,
                ToName = t.ToUser?.FullName ?? "—",
                ToCode = t.ToUser?.StampCode ?? "—",
                SheetCount = sheets.Count,
                Sheets = sheets,
                CreatedAt = t.Batch.CreatedAt,
                ReachedAt = t.At,
            });
        }
        ViewBag.History = history;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> EditDetails(int id, string? contractNo, string? workOrderNo,
        int qtyMultiplier, int? checkedById, string? assembledOn, string? qualityInsp, string? sheetNo,
        string? writtenBy, string? noOfSheets, string? processSheetIssue, string? drawingIssue, string? docRef)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(contractNo) && string.IsNullOrWhiteSpace(workOrderNo))
        {
            TempData["Err"] = "A Work Order is required when a Contract No is set.";
            return RedirectToAction("EditDetails", new { id });
        }

        batch.ContractNo = contractNo?.Trim();
        batch.WorkOrderNo = workOrderNo?.Trim();
        batch.QtyMultiplier = qtyMultiplier < 1 ? 1 : qtyMultiplier;
        batch.CheckedById = checkedById;
        batch.AssembledOn = assembledOn?.Trim();
        batch.QualityInsp = qualityInsp?.Trim();
        batch.SheetNo = string.IsNullOrWhiteSpace(sheetNo) ? "1/1" : sheetNo.Trim();
        batch.WrittenBy = writtenBy?.Trim();
        batch.NoOfSheets = string.IsNullOrWhiteSpace(noOfSheets) ? "1" : noOfSheets.Trim();
        batch.ProcessSheetIssue = string.IsNullOrWhiteSpace(processSheetIssue) ? "1" : processSheetIssue.Trim();
        batch.DrawingIssue = string.IsNullOrWhiteSpace(drawingIssue) ? "1" : drawingIssue.Trim();
        batch.DocRef = string.IsNullOrWhiteSpace(docRef) ? "E/206/401/ن" : docRef.Trim();

        await PropagateAsync(batch);            // push everything onto every sheet
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Batch details saved and applied to all sheets.";
        return RedirectToAction("Details", new { id });
    }

    // ---- Eman Mahmoud sends only the sheets that have actually been converted to
    // Process (by whoever on her team did it) — the rest stay right where they are,
    // still being worked on. Moves just that subset into its own batch, handed to
    // whichever Quality account she picks. ----
    [HttpGet]
    public async Task<IActionResult> SendCompletedToQuality(int batchId)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin)) { TempData["Err"] = "Not allowed."; return RedirectToAction("Index"); }
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();

        // Only sheets not already sent to someone for review — sending again later (as
        // more get converted) never re-sends ones already out with Quality.
        int completed = await _db.ProcessSheets.CountAsync(s => s.BatchId == batchId && s.ConvertedToProcess && s.SentToQualityUserId == null);
        int total = await _db.ProcessSheets.CountAsync(s => s.BatchId == batchId);
        ViewBag.Batch = batch;
        ViewBag.Completed = completed;
        ViewBag.Total = total;
        ViewBag.QualityUsers = await QualityUsersAsync();
        ViewBag.PartList = await PartListInfoAsync(batch.ProjectId);
        ViewBag.PartListCheck = batch.ProjectId.HasValue ? await BuildPartListCheckAsync(batch.ProjectId.Value) : null;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendCompletedToQuality(int batchId, int qualityUserId, IFormFile? partListFile, bool sendPartList = false)
    {
        var me = await MeAsync();
        if (me == null || (!me.IsCoordinator && !me.IsAdmin)) { TempData["Err"] = "Not allowed."; return RedirectToAction("Index"); }
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        var qualityUser = await _db.Users.FindAsync(qualityUserId);
        if (qualityUser == null || qualityUser.Slot != SignOffSlot.QualityInspector)
        {
            TempData["Err"] = "Pick a Quality account.";
            return RedirectToAction("SendCompletedToQuality", new { batchId });
        }

        // Sheets stay exactly where they are, in this same batch — this only marks who
        // they were sent to for review. Quality gets to it via "Sheets sent to me for
        // review" in their own Inbox, and Eman keeps seeing every sheet here the whole
        // time, with its real status, instead of it disappearing from her view.
        var completedSheets = await _db.ProcessSheets
            .Where(s => s.BatchId == batchId && s.ConvertedToProcess && s.SentToQualityUserId == null)
            .ToListAsync();
        if (completedSheets.Count == 0)
        {
            TempData["Err"] = "Nothing new to send — everything converted so far has already gone to Quality.";
            return RedirectToAction("Details", new { id = batchId });
        }

        // Optional Part List as a reference for Quality: a file uploaded here is saved to the
        // project (replacing any earlier one); with no file, the tick box confirms (or
        // withdraws) one that's already attached — e.g. by Research when it sent the drawings.
        // Checked before any sheet is marked as sent, so a bad file stops everything cleanly.
        string partListNote = "";
        if (partListFile != null && partListFile.Length > 0)
        {
            var (plBytes, plError) = await ReadPartListAsync(partListFile);
            if (plError != null)
            {
                TempData["Err"] = plError;
                return RedirectToAction("SendCompletedToQuality", new { batchId });
            }
            var plTemplateError = PartListTemplateError(plBytes!);
            if (plTemplateError != null)
            {
                TempData["Err"] = plTemplateError;
                return RedirectToAction("SendCompletedToQuality", new { batchId });
            }
            var projectIdForList = await EnsureProjectForBatchAsync(batch, me);
            await StorePartListAsync(projectIdForList, Path.GetFileName(partListFile.FileName), plBytes!, me);
            batch.PartListShared = true;
            partListNote = " Part List attached as a reference.";
        }
        else if (batch.ProjectId.HasValue && await _db.ProjectPartLists.AnyAsync(p => p.ProjectId == batch.ProjectId))
        {
            batch.PartListShared = sendPartList;
            partListNote = sendPartList ? " Part List included as a reference." : "";
        }

        foreach (var sheet in completedSheets) { sheet.SentToQualityUserId = qualityUser.Id; sheet.SentToQualityAt = DateTime.Now; }
        await _db.SaveChangesAsync();

        TempData["Msg"] = $"Sent {completedSheets.Count} completed sheet(s) to {qualityUser.FullName}.{partListNote}";
        return RedirectToAction("Details", new { id = batchId });
    }

    // ---- A dedicated tab for Quality — every delivery they've received, grouped by
    // which batch (and project, if the coordinator put it in one) it came from, since
    // more than one batch can land with the same Quality account at once. ----
    public async Task<IActionResult> QualityInbox()
    {
        int me = User.GetUserId();
        var sheets = await _db.ProcessSheets.Where(s => s.SentToQualityUserId == me)
            .Include(s => s.Batch).ThenInclude(b => b!.Project)
            .Include(s => s.Reviews)
            .OrderByDescending(s => s.Id)
            .ToListAsync();

        var groupProjectIds = sheets.Where(s => s.Batch?.ProjectId != null).Select(s => s.Batch!.ProjectId!.Value).Distinct().ToList();
        var projectsWithPartList = await _db.ProjectPartLists.Where(p => groupProjectIds.Contains(p.ProjectId))
            .Select(p => p.ProjectId).ToListAsync();
        var groups = sheets.GroupBy(s => s.BatchId)
            .Select(g => new QualityDeliveryGroup
            {
                BatchId = g.Key,
                BatchName = g.First().Batch?.Name ?? "—",
                ProjectName = g.First().Batch?.Project?.Name,
                PartListProjectId = g.First().Batch is { PartListShared: true, ProjectId: not null } pb
                    && projectsWithPartList.Contains(pb.ProjectId.Value) ? pb.ProjectId : null,
                Sheets = g.Select(s =>
                {
                    var myReview = s.Reviews.Where(r => r.ReviewerId == me && r.Slot == SignOffSlot.QualityInspector)
                        .OrderByDescending(r => r.ReviewedAt).FirstOrDefault();
                    return new AssignedSheetRow
                    {
                        Id = s.Id,
                        DrawingNo = s.DrawingNo,
                        PartNo = s.PartNo,
                        Section = s.Section,
                        ConvertedToProcess = s.ConvertedToProcess,
                        IsReviewedByMe = myReview != null,
                        IsRejectedByMe = myReview?.Decision == ReviewDecision.Rejected,
                        WasResent = myReview?.Decision == ReviewDecision.Rejected
                            && s.ResentForReviewAt.HasValue && s.ResentForReviewAt > myReview.ReviewedAt,
                    };
                }).ToList(),
            })
            .OrderByDescending(g => g.Sheets.Max(s => s.Id))
            .ToList();

        ViewBag.Groups = groups;
        return View();
    }

    public class QualityDeliveryGroup
    {
        public int? BatchId { get; set; }
        public string BatchName { get; set; } = "";
        public string? ProjectName { get; set; }
        public int? PartListProjectId { get; set; }   // set when a Part List was sent along as a reference
        public List<AssignedSheetRow> Sheets { get; set; } = new();
    }

    // ---- Programs & Operation's own tab — every batch handed to them, with the two
    // steps that apply once per batch (Finalize on the first sheet, then Place stamp)
    // shown as a two-step progress bar, and every sheet shown as a card. ----
    // ---- Search across every batch by Contract Notification No. or Work Order No. —
    // for Programs & Operation to find "which batches used this number", including ones
    // where the number has since been changed (checks the history archive too). ----
    // ---- Every batch that's ever had its Contract Notification No. or Work Order No.
    // changed after being finalized — showing exactly what it changed from and to. ----
    public async Task<IActionResult> FinalizeChanges()
    {
        var allHistory = await _db.ProgramsFinalizeHistories
            .OrderBy(h => h.BatchId).ThenBy(h => h.ActiveFrom)
            .ToListAsync();

        var rows = new List<FinalizeChangeRow>();
        foreach (var group in allHistory.GroupBy(h => h.BatchId))
        {
            var entries = group.OrderBy(h => h.ActiveFrom).ToList();
            var sample = await _db.ProcessSheets.Where(s => s.BatchId == group.Key).FirstOrDefaultAsync();
            for (int i = 0; i < entries.Count; i++)
            {
                var oldEntry = entries[i];
                // What it changed TO is either the next archived entry, or — if this was
                // the most recent change — whatever the batch's numbers currently are.
                string? newContract = i + 1 < entries.Count ? entries[i + 1].ContractNotificationNo : sample?.ContractNotificationNo;
                string? newWorkOrder = i + 1 < entries.Count ? entries[i + 1].WorkOrderNo : sample?.WorkOrderNo;
                rows.Add(new FinalizeChangeRow
                {
                    BatchId = group.Key,
                    BatchName = oldEntry.BatchName,
                    OldContractNotificationNo = oldEntry.ContractNotificationNo,
                    NewContractNotificationNo = newContract,
                    OldWorkOrderNo = oldEntry.WorkOrderNo,
                    NewWorkOrderNo = newWorkOrder,
                    ChangedAt = oldEntry.ActiveTo ?? oldEntry.ActiveFrom,
                });
            }
        }

        ViewBag.Rows = rows.OrderByDescending(r => r.ChangedAt).ToList();
        return View();
    }

    public class FinalizeChangeRow
    {
        public int BatchId { get; set; }
        public string BatchName { get; set; } = "";
        public string? OldContractNotificationNo { get; set; }
        public string? NewContractNotificationNo { get; set; }
        public string? OldWorkOrderNo { get; set; }
        public string? NewWorkOrderNo { get; set; }
        public DateTime? ChangedAt { get; set; }
    }

    public async Task<IActionResult> SearchByContractOrWorkOrder(string? q)
    {
        ViewBag.Query = q;
        if (string.IsNullOrWhiteSpace(q))
        {
            ViewBag.Results = new List<ContractSearchResult>();
            return View();
        }
        string term = q.Trim();

        var current = await _db.ProcessSheets
            .Where(s => s.BatchId != null && (s.ContractNotificationNo == term || s.WorkOrderNo == term))
            .Select(s => new { s.BatchId, BatchName = s.Batch!.Name, s.ContractNotificationNo, s.WorkOrderNo })
            .Distinct()
            .ToListAsync();

        var historical = await _db.ProgramsFinalizeHistories
            .Where(h => h.ContractNotificationNo == term || h.WorkOrderNo == term)
            .ToListAsync();

        var results = new List<ContractSearchResult>();
        results.AddRange(current.Select(c => new ContractSearchResult
        {
            BatchId = c.BatchId,
            BatchName = c.BatchName ?? "—",
            ContractNotificationNo = c.ContractNotificationNo,
            WorkOrderNo = c.WorkOrderNo,
            IsCurrent = true,
        }));
        results.AddRange(historical.Select(h => new ContractSearchResult
        {
            BatchId = h.BatchId,
            BatchName = h.BatchName,
            ContractNotificationNo = h.ContractNotificationNo,
            WorkOrderNo = h.WorkOrderNo,
            IsCurrent = false,
            ActiveFrom = h.ActiveFrom,
            ActiveTo = h.ActiveTo,
        }));

        ViewBag.Results = results.OrderByDescending(r => r.IsCurrent).ThenBy(r => r.BatchName).ToList();
        return View();
    }

    public class ContractSearchResult
    {
        public int? BatchId { get; set; }
        public string BatchName { get; set; } = "";
        public string? ContractNotificationNo { get; set; }
        public string? WorkOrderNo { get; set; }
        public bool IsCurrent { get; set; }
        public DateTime? ActiveFrom { get; set; }
        public DateTime? ActiveTo { get; set; }
    }

    // ---- Full history of Contract/Work Order changes for one batch, plus its current
    // numbers — "nothing gets erased" view. ----
    public async Task<IActionResult> ProgramsFinalizeHistoryFor(int batchId)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        var sample = await _db.ProcessSheets.Where(s => s.BatchId == batchId).FirstOrDefaultAsync();
        var history = await _db.ProgramsFinalizeHistories.Where(h => h.BatchId == batchId)
            .OrderByDescending(h => h.ActiveFrom).ToListAsync();
        ViewBag.Batch = batch;
        ViewBag.Sample = sample;
        ViewBag.History = history;
        return View();
    }

    public async Task<IActionResult> ProgramsInbox()
    {
        int me = User.GetUserId();
        var batches = await _db.Batches.Where(b => b.CurrentHolderId == me)
            .Include(b => b.Project).Include(b => b.Placements)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        var groups = new List<ProgramsDeliveryGroup>();
        foreach (var batch in batches)
        {
            var sheets = await _db.ProcessSheets.Where(s => s.BatchId == batch.Id)
                .OrderBy(s => s.Id).ToListAsync();
            if (sheets.Count == 0) continue;

            bool finalized = sheets[0].ContractNotificationNo != null || sheets[0].WorkOrderNo != null;
            bool stamped = batch.Placements.Any(p => p.UserId == me);

            groups.Add(new ProgramsDeliveryGroup
            {
                BatchId = batch.Id,
                BatchName = batch.Name,
                ProjectName = batch.Project?.Name,
                Finalized = finalized,
                Stamped = stamped,
                FirstSheetId = sheets[0].Id,
                Sheets = sheets.Select(s => new AssignedSheetRow
                {
                    Id = s.Id,
                    DrawingNo = s.DrawingNo,
                    PartNo = s.PartNo,
                    Section = s.Section,
                    ConvertedToProcess = s.ConvertedToProcess,
                }).ToList(),
            });
        }

        ViewBag.Groups = groups;
        return View();
    }

    public class ProgramsDeliveryGroup
    {
        public int BatchId { get; set; }
        public string BatchName { get; set; } = "";
        public string? ProjectName { get; set; }
        public bool Finalized { get; set; }
        public bool Stamped { get; set; }
        public int FirstSheetId { get; set; }
        public List<AssignedSheetRow> Sheets { get; set; } = new();
    }

    // ---- After stamping a whole delivery, individual sheets sometimes need their
    // stamp nudged on their own (a longer part name pushed things around, etc.) — same
    // idea as Research's "Fix stamp", but for the overlay-image stamps Quality and
    // Programs & Operation use (StampPlacement), not Research's baked-in drawing stamp. ----
    [HttpGet]
    public async Task<IActionResult> PerSheetFixStamp(int sheetId)
    {
        var sheet = await _db.ProcessSheets.Include(s => s.Batch).ThenInclude(b => b!.Placements)
            .Include(s => s.Reviews).Include(s => s.Operations)
            .FirstOrDefaultAsync(s => s.Id == sheetId);
        if (sheet == null) return NotFound();
        var me = await MeAsync();
        if (me == null) return Forbid();

        // This sheet's own override if it has one, else fall back to showing where the
        // batch-wide placement currently sits (as the starting point to nudge from).
        var mySheetOnly = sheet.Batch?.Placements.Where(p => p.UserId == me.Id && p.ProcessSheetId == sheetId).ToList() ?? new();
        ViewBag.Mine = mySheetOnly.Count > 0 ? mySheetOnly
            : sheet.Batch?.Placements.Where(p => p.UserId == me.Id && p.ProcessSheetId == null).ToList() ?? new();
        ViewBag.IsOverride = mySheetOnly.Count > 0;
        ViewBag.MyStamp = (me.StampImage != null && me.StampImage.Length > 0)
            ? "data:" + (me.StampMime ?? "image/png") + ";base64," + Convert.ToBase64String(me.StampImage) : null;

        // The route card renders this same stamp on its own — hiding it here so the
        // draggable overlay above isn't shown twice, once "baked in" and once floating.
        ViewBag.ExcludeUserId = me.Id;

        // Reference Part List for this sheet's project, if one was sent along with it.
        if (sheet.Batch is { PartListShared: true, ProjectId: not null } plBatch
            && await _db.ProjectPartLists.AnyAsync(p => p.ProjectId == plBatch.ProjectId))
            ViewBag.PartListProjectId = plBatch.ProjectId;

        bool isQuality = me.Slot == SignOffSlot.QualityInspector;
        ViewBag.IsQualityReview = isQuality;
        if (isQuality)
        {
            var myLatestReview = sheet.Reviews.Where(r => r.ReviewerId == me.Id && r.Slot == SignOffSlot.QualityInspector)
                .OrderByDescending(r => r.ReviewedAt).FirstOrDefault();
            ViewBag.MyLatestReview = myLatestReview;
            ViewBag.WasResent = myLatestReview?.Decision == ReviewDecision.Rejected
                && sheet.ResentForReviewAt.HasValue && sheet.ResentForReviewAt > myLatestReview.ReviewedAt;
            // How many sheets from this same delivery are still waiting, for "Next" to skip to.
            ViewBag.NextUnreviewedSheetId = await _db.ProcessSheets
                .Where(s => s.SentToQualityUserId == me.Id && s.BatchId == sheet.BatchId && s.Id != sheetId
                    && !s.Reviews.Any(r => r.ReviewerId == me.Id && r.Slot == SignOffSlot.QualityInspector))
                .OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync();
        }
        return View(sheet);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QualityDecide(int sheetId, string decision, string? comment, string? annotation, string? placements)
    {
        var sheet = await _db.ProcessSheets.FindAsync(sheetId);
        if (sheet == null) return NotFound();
        var me = await MeAsync();
        if (me == null) return Forbid();

        bool accepted = decision == "Accept";

        // The stamp's own position only gets saved (and only applied to the sheet) when
        // actually accepting — rejecting means "don't stamp this," even if it was nudged
        // into place first.
        if (accepted && !string.IsNullOrWhiteSpace(placements))
        {
            List<PlacementDto> items;
            try { items = System.Text.Json.JsonSerializer.Deserialize<List<PlacementDto>>(placements) ?? new(); }
            catch { items = new(); }

            var old = await _db.StampPlacements.Where(p => p.ProcessSheetId == sheetId && p.UserId == me.Id).ToListAsync();
            _db.StampPlacements.RemoveRange(old);
            foreach (var it in items)
            {
                byte[]? img = me.StampImage;
                string? mime = me.StampMime;
                if (!string.IsNullOrEmpty(it.img) && it.img.StartsWith("data:"))
                {
                    try
                    {
                        int semi = it.img.IndexOf(';');
                        int comma = it.img.IndexOf(',');
                        mime = semi > 5 ? it.img.Substring(5, semi - 5) : "image/png";
                        img = Convert.FromBase64String(it.img[(comma + 1)..]);
                    }
                    catch { }
                }
                _db.StampPlacements.Add(new StampPlacement
                {
                    BatchId = sheet.BatchId ?? 0,
                    UserId = me.Id,
                    ProcessSheetId = sheetId,
                    NormX = Math.Clamp(it.x, 0, 1),
                    NormY = Math.Clamp(it.y, 0, 1),
                    AnchorRight = it.ax == "right",
                    AnchorBottom = it.ay == "bottom",
                    WidthPct = Math.Clamp(it.w, 2, 60),
                    AnchorText = string.IsNullOrWhiteSpace(it.text) ? null : it.text.Trim(),
                    OffsetXPx = it.dx,
                    OffsetYPx = it.dy,
                    Image = img,
                    Mime = mime,
                    CreatedAt = DateTime.Now,
                });
            }
        }
        else if (!accepted)
        {
            // Rejecting a sheet that was previously Accepted (and so already stamped)
            // must actually remove that stamp — otherwise the sheet keeps showing as
            // stamped even though it's now marked Rejected, which is exactly backwards.
            // A per-sheet override with no image suppresses just this one sheet's stamp
            // without touching the shared batch-wide placement other accepted sheets
            // still rely on (_RouteCard only renders a placement when it has an image).
            var old = await _db.StampPlacements.Where(p => p.ProcessSheetId == sheetId && p.UserId == me.Id).ToListAsync();
            _db.StampPlacements.RemoveRange(old);
            _db.StampPlacements.Add(new StampPlacement
            {
                BatchId = sheet.BatchId ?? 0,
                UserId = me.Id,
                ProcessSheetId = sheetId,
                NormX = 0,
                NormY = 0,
                AnchorRight = false,
                AnchorBottom = false,
                WidthPct = 10,
                Image = null,
                Mime = null,
                CreatedAt = DateTime.Now,
            });
        }

        byte[]? annotationImg = null;
        if (!string.IsNullOrEmpty(annotation) && annotation.StartsWith("data:"))
        {
            try { annotationImg = Convert.FromBase64String(annotation[(annotation.IndexOf(',') + 1)..]); }
            catch { }
        }

        _db.SheetReviews.Add(new SheetReview
        {
            ProcessSheetId = sheetId,
            ReviewerId = me.Id,
            ReviewerName = me.FullName,
            Slot = SignOffSlot.QualityInspector,
            Decision = accepted ? ReviewDecision.Accepted : ReviewDecision.Rejected,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            StampImage = me.StampImage,
            StampMime = me.StampMime,
            AnnotationImage = annotationImg,
            AnnotationMime = annotationImg != null ? "image/png" : null,
            ReviewedAt = DateTime.Now,
        });
        await _db.SaveChangesAsync();

        var nextId = await _db.ProcessSheets
            .Where(s => s.SentToQualityUserId == me.Id && s.BatchId == sheet.BatchId && s.Id != sheetId
                && !s.Reviews.Any(r => r.ReviewerId == me.Id && r.Slot == SignOffSlot.QualityInspector))
            .OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync();

        TempData["Msg"] = accepted ? "Accepted and stamped." : "Marked as Rejected — Planning will see this and can fix it.";
        return nextId.HasValue
            ? RedirectToAction("PerSheetFixStamp", new { sheetId = nextId.Value })
            : RedirectToAction("QualityInbox");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerSheetFixStamp(int sheetId, string placements)
    {
        var sheet = await _db.ProcessSheets.FindAsync(sheetId);
        if (sheet == null) return NotFound();
        var me = await MeAsync();
        if (me == null) return Forbid();

        List<PlacementDto> items;
        try { items = System.Text.Json.JsonSerializer.Deserialize<List<PlacementDto>>(placements ?? "[]") ?? new(); }
        catch { items = new(); }

        // Only this sheet's own override is touched — every other sheet keeps using the
        // shared batch-wide placement untouched.
        var old = await _db.StampPlacements.Where(p => p.ProcessSheetId == sheetId && p.UserId == me.Id).ToListAsync();
        _db.StampPlacements.RemoveRange(old);

        foreach (var it in items)
        {
            byte[]? img = me.StampImage;
            string? mime = me.StampMime;
            if (!string.IsNullOrEmpty(it.img) && it.img.StartsWith("data:"))
            {
                try
                {
                    int semi = it.img.IndexOf(';');
                    int comma = it.img.IndexOf(',');
                    mime = semi > 5 ? it.img.Substring(5, semi - 5) : "image/png";
                    img = Convert.FromBase64String(it.img[(comma + 1)..]);
                }
                catch { /* keep fallback */ }
            }
            _db.StampPlacements.Add(new StampPlacement
            {
                BatchId = sheet.BatchId ?? 0,
                UserId = me.Id,
                ProcessSheetId = sheetId,
                NormX = Math.Clamp(it.x, 0, 1),
                NormY = Math.Clamp(it.y, 0, 1),
                AnchorRight = it.ax == "right",
                AnchorBottom = it.ay == "bottom",
                WidthPct = Math.Clamp(it.w, 2, 60),
                AnchorText = string.IsNullOrWhiteSpace(it.text) ? null : it.text.Trim(),
                OffsetXPx = it.dx,
                OffsetYPx = it.dy,
                Image = img,
                Mime = mime,
                CreatedAt = DateTime.Now,
            });
        }
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Stamp position updated for this sheet.";
        return me.Slot == SignOffSlot.QualityInspector
            ? RedirectToAction("QualityInbox")
            : RedirectToAction("Details", new { id = sheet.BatchId });
    }

    private Task<List<SelectListItem>> QualityUsersAsync() =>
        _db.Users.Where(u => u.IsActive && u.Slot == SignOffSlot.QualityInspector)
            .OrderBy(u => u.FullName)
            .Select(u => new SelectListItem(u.FullName, u.Id.ToString()))
            .ToListAsync();

    // ---- Once Quality has reviewed and stamped every sheet, send the whole batch on
    // to Programs & Operation. ----
    [HttpGet]
    public async Task<IActionResult> SendToProgramsOps(int batchId)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        ViewBag.Batch = batch;
        ViewBag.ProgramsUsers = await _db.Users.Where(u => u.IsActive && u.Department == "Programs & Operation")
            .OrderBy(u => u.FullName)
            .Select(u => new SelectListItem(u.FullName, u.Id.ToString()))
            .ToListAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendToProgramsOps(int batchId, int programsUserId)
    {
        var me = await MeAsync();
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        var target = await _db.Users.FindAsync(programsUserId);
        if (target == null || target.Department != "Programs & Operation")
        {
            TempData["Err"] = "Pick a Programs & Operation account.";
            return RedirectToAction("SendToProgramsOps", new { batchId });
        }

        batch.CurrentHolderId = target.Id;
        batch.Transfers.Add(new BatchTransfer { FromUserId = me?.Id, ToUserId = target.Id, Action = "Assigned", At = DateTime.Now });
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Sent to {target.FullName}.";
        return RedirectToAction("Details", new { id = batchId });
    }

    // ---- Programs & Operation's four things: Contract Notification No, Work Order No
    // (rendered as a stamp on the sheet), Qty x Contract multiplier -> Total Qty, and
    // their own stamp (placed via the existing "Place stamp" tool, same as anyone else). ----
    [HttpGet]
    public async Task<IActionResult> ProgramsFinalize(int batchId)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        var sample = await _db.ProcessSheets.Where(s => s.BatchId == batchId).OrderBy(s => s.Id).FirstOrDefaultAsync();
        ViewBag.Batch = batch;
        ViewBag.SampleContractNotificationNo = sample?.ContractNotificationNo;
        ViewBag.SampleWorkOrderNo = sample?.WorkOrderNo;
        ViewBag.SampleIssue = sample?.ProgramsIssue;
        // Defaults to today, but stays editable — once already set on a sheet, keep
        // showing whatever was actually saved rather than resetting back to today.
        ViewBag.SampleDate = !string.IsNullOrWhiteSpace(sample?.ProgramsDate) ? sample!.ProgramsDate : DateTime.Now.ToString("yyyy-MM-dd");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProgramsFinalize(int batchId, string? contractNotificationNo, string? workOrderNo, int? qtyMultiplier,
        string? programsIssue, string? programsDate)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();

        var sheets = await _db.ProcessSheets.Where(s => s.BatchId == batchId).ToListAsync();
        int mult = qtyMultiplier.HasValue && qtyMultiplier.Value > 0 ? qtyMultiplier.Value : 1;
        batch.QtyMultiplier = mult;
        string dateToUse = string.IsNullOrWhiteSpace(programsDate) ? DateTime.Now.ToString("d/ M / yyyy")
            : (DateTime.TryParse(programsDate, out var d) ? d.ToString("d/ M / yyyy") : programsDate);

        // All sheets in a batch always carry the same Contract/Work Order numbers, so
        // one of them tells us whether this call is actually changing anything. A batch
        // finalized before this history feature existed won't have ProgramsFinalizedAt
        // set yet — that still counts as "had prior values" if it already has a
        // Contract/WO number, just with the batch's creation date as a fallback start.
        var sample = sheets.FirstOrDefault();
        bool hadPriorValues = sample != null && (sample.ContractNotificationNo != null || sample.WorkOrderNo != null);
        bool isChange = hadPriorValues && (sample!.ContractNotificationNo != contractNotificationNo || sample.WorkOrderNo != workOrderNo);
        DateTime now = DateTime.Now;

        if (isChange)
        {
            // The old numbers don't get overwritten and lost — they're archived here
            // with exactly how long they were active, before the new ones take over.
            _db.ProgramsFinalizeHistories.Add(new ProgramsFinalizeHistory
            {
                BatchId = batchId,
                BatchName = batch.Name,
                ContractNotificationNo = sample!.ContractNotificationNo,
                WorkOrderNo = sample.WorkOrderNo,
                ActiveFrom = sample.ProgramsFinalizedAt ?? batch.CreatedAt,
                ActiveTo = now,
            });
        }

        foreach (var sheet in sheets)
        {
            sheet.ContractNotificationNo = contractNotificationNo;
            sheet.WorkOrderNo = workOrderNo;
            sheet.TotalQty = (sheet.Qty * mult).ToString();
            sheet.ProgramsIssue = programsIssue;
            sheet.ProgramsDate = dateToUse;
            // Only reset the "since when" stamp on an actual first-time set or a real
            // change — resubmitting the same values again shouldn't reset the clock.
            if (isChange || !sheet.ProgramsFinalizedAt.HasValue) sheet.ProgramsFinalizedAt = now;
        }
        await _db.SaveChangesAsync();
        TempData["Msg"] = $"Applied to all {sheets.Count} sheet(s). Now place your stamp if you haven't already.";
        return RedirectToAction("PlaceStamps", new { id = batchId });
    }

    private Task<List<SelectListItem>> PlannersAsync() =>
        _db.Users.Where(u => u.IsActive && u.IsPlanner && u.Id != User.GetUserId())
            .OrderBy(u => u.FullName)
            .Select(u => new SelectListItem($"{u.FullName} ({u.Department})", u.Id.ToString()))
            .ToListAsync();

    // Push batch-level values onto each sheet; Total Qty = Qty * multiplier.
    private async Task PropagateAsync(Batch batch)
    {
        var sheets = await _db.ProcessSheets.Where(s => s.BatchId == batch.Id).ToListAsync();
        foreach (var s in sheets) BatchFooter.ApplyTo(batch, s);
    }

    // Stamp the planner's signature (Complied by) and optional checker (Checked by) on every sheet.
    private async Task ApplyPlanningSignaturesAsync(int batchId, AppUser planner, AppUser? checker)
    {
        var sheetIds = await _db.ProcessSheets.Where(s => s.BatchId == batchId).Select(s => s.Id).ToListAsync();
        var existing = await _db.SheetReviews
            .Where(r => sheetIds.Contains(r.ProcessSheetId)
                        && (r.Slot == SignOffSlot.CompiledBy || r.Slot == SignOffSlot.CheckedBy))
            .ToListAsync();

        foreach (var sid in sheetIds)
        {
            Upsert(existing, sid, SignOffSlot.CompiledBy, planner);
            if (checker != null) Upsert(existing, sid, SignOffSlot.CheckedBy, checker);
        }
    }

    private void Upsert(List<SheetReview> existing, int sheetId, SignOffSlot slot, AppUser user)
    {
        var rev = existing.FirstOrDefault(r => r.ProcessSheetId == sheetId && r.Slot == slot);
        if (rev == null)
        {
            rev = new SheetReview { ProcessSheetId = sheetId, Slot = slot };
            _db.SheetReviews.Add(rev);
            existing.Add(rev);
        }
        rev.ReviewerId = user.Id;
        rev.Decision = ReviewDecision.Accepted;
        // Complied by / Checked by are signature sign-offs (Planning); every other slot
        // is a stamp-holder role — snapshot whichever image actually applies to this slot.
        bool signatureSlot = slot == SignOffSlot.CompiledBy || slot == SignOffSlot.CheckedBy;
        if (signatureSlot && user.HasSignature)
        {
            rev.StampImage = user.SignatureImage;
            rev.StampMime = user.SignatureMime;
        }
        else
        {
            rev.StampImage = user.StampImage;
            rev.StampMime = user.StampMime;
        }
        rev.ReviewerName = slot switch
        {
            SignOffSlot.CompiledBy => string.IsNullOrWhiteSpace(user.CompliedByName) ? user.FullName : user.CompliedByName,
            SignOffSlot.CheckedBy => string.IsNullOrWhiteSpace(user.CheckedByName) ? user.FullName : user.CheckedByName,
            _ => user.FullName,
        };
        rev.ReviewedAt = DateTime.Now;
    }

    // ---- Creator sends a (manually built) draft batch to the first reviewer ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> Dispatch(int id, int toUserId, int? checkedById, string? note)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        int me = User.GetUserId();
        if (batch.CreatedById != me && !User.IsAdmin())
        { TempData["Err"] = "Only the creator can send this batch."; return RedirectToAction("Details", new { id }); }
        if (batch.RestrictedRecipientId.HasValue && toUserId != batch.RestrictedRecipientId.Value)
        {
            TempData["Err"] = "This batch came back from a rejection — it can only be sent back to the reviewer who rejected it.";
            return RedirectToAction("Details", new { id });
        }

        var sheets = await _db.ProcessSheets.Where(s => s.BatchId == id).ToListAsync();
        if (sheets.Count == 0) { TempData["Err"] = "Add at least one sheet before sending."; return RedirectToAction("Details", new { id }); }

        var planner = await MeAsync();
        if (planner == null) return Forbid();

        var sheetIds = sheets.Select(s => s.Id).ToList();
        bool alreadySigned = await _db.SheetReviews
            .AnyAsync(r => sheetIds.Contains(r.ProcessSheetId) && r.Slot == SignOffSlot.CompiledBy);

        if (!alreadySigned)
        {
            // First send: the planner signs (mandatory), checker optional.
            bool hasAnySignature = planner.HasSignature
                || await _db.UserSignatures.AnyAsync(s => s.UserId == planner.Id);
            if (!hasAnySignature)
            {
                TempData["Err"] = "Upload your signature in Profile first — it is required beside 'Complied by' before sending.";
                return RedirectToAction("Details", new { id });
            }
            if (checkedById.HasValue) batch.CheckedById = checkedById;
            var checker = batch.CheckedById.HasValue ? await _db.Users.FindAsync(batch.CheckedById.Value) : null;

            foreach (var s in sheets)
            {
                BatchFooter.ApplyTo(batch, s);
                s.CompiledBy = planner.FullName;
                if (checker != null) s.CheckedBy = checker.FullName;
            }
            await ApplyPlanningSignaturesAsync(id, planner, checker);
        }
        else
        {
            // Resend after a return: keep the original signature (no re-sign);
            // refresh batch values and let the destination review the fixed sheets again.
            foreach (var s in sheets) BatchFooter.ApplyTo(batch, s);
            var oldDest = await _db.SheetReviews
                .Where(r => r.ReviewerId == toUserId && sheetIds.Contains(r.ProcessSheetId))
                .ToListAsync();
            _db.SheetReviews.RemoveRange(oldDest);
        }

        batch.CurrentHolderId = toUserId;
        batch.Status = BatchStatus.InReview;
        batch.Transfers.Add(new BatchTransfer
        {
            FromUserId = me,
            ToUserId = toUserId,
            Action = alreadySigned ? "Resent for review" : "Sent",
            Note = note?.Trim(),
            At = DateTime.Now,
        });
        await _db.SaveChangesAsync();
        TempData["Msg"] = alreadySigned
            ? "Batch resent for review (kept the original signature)."
            : "Batch signed and sent to the first reviewer.";
        return RedirectToAction("Index");
    }

    // ---- Finish a batch (terminal, fully approved) ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();
        int me = User.GetUserId();
        if (batch.CurrentHolderId != me) { TempData["Err"] = "Not your batch."; return RedirectToAction("Details", new { id }); }

        int total = await _db.ProcessSheets.CountAsync(s => s.BatchId == id);
        int reviewed = await _db.SheetReviews.Where(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Slot != SignOffSlot.CompiledBy && r.Slot != SignOffSlot.CheckedBy).Select(r => r.ProcessSheetId).Distinct().CountAsync();
        if (reviewed < total) { TempData["Err"] = $"Review every sheet first ({reviewed}/{total})."; return RedirectToAction("Details", new { id }); }

        batch.Status = BatchStatus.Approved;
        batch.CurrentHolderId = null;
        batch.Transfers.Add(new BatchTransfer { FromUserId = me, Action = "Completed", At = DateTime.Now });
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Batch marked complete.";
        return RedirectToAction("Index");
    }

    // ---- Split the sheets I rejected into a new batch routed back to the creator ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnRejects(int id, string? note)
    {
        var batch = await _db.Batches.Include(b => b.Transfers).FirstOrDefaultAsync(b => b.Id == id);
        if (batch == null) return NotFound();
        int me = User.GetUserId();
        if (batch.CurrentHolderId != me) { TempData["Err"] = "Not your batch."; return RedirectToAction("Details", new { id }); }

        var rejectedIds = await _db.SheetReviews
            .Where(r => r.ReviewerId == me && r.ProcessSheet!.BatchId == id && r.Decision == ReviewDecision.Rejected)
            .Select(r => r.ProcessSheetId).Distinct().ToListAsync();
        if (rejectedIds.Count == 0) { TempData["Msg"] = "No rejected sheets to return."; return RedirectToAction("Details", new { id }); }

        // Previous stage = whoever last sent this batch to me (fallback: the creator).
        int returnTo = (batch.Transfers
            .Where(t => t.ToUserId == me && t.FromUserId != null)
            .OrderByDescending(t => t.At)
            .Select(t => t.FromUserId)
            .FirstOrDefault()) ?? batch.CreatedById;

        var ret = new Batch
        {
            Name = batch.Name + "-RETURN",
            ProjectName = batch.ProjectName,
            ContractId = batch.ContractId,
            ContractNo = batch.ContractNo,
            WorkOrderNo = batch.WorkOrderNo,
            QtyMultiplier = batch.QtyMultiplier,
            CreatedById = returnTo,                 // the previous stage owns it as a draft
            CreatedAt = DateTime.Now,
            Status = BatchStatus.Draft,
            CurrentHolderId = null,
            RestrictedRecipientId = me,              // may only be sent back to whoever rejected it
        };
        _db.Batches.Add(ret);
        await _db.SaveChangesAsync();

        var sheets = await _db.ProcessSheets.Where(s => rejectedIds.Contains(s.Id)).ToListAsync();
        foreach (var s in sheets) s.BatchId = ret.Id;
        ret.Transfers.Add(new BatchTransfer
        {
            FromUserId = me,
            ToUserId = returnTo,
            Action = "Returned (rejected sheets)",
            Note = note?.Trim(),
            At = DateTime.Now,
        });
        await _db.SaveChangesAsync();

        TempData["Msg"] = $"Returned {sheets.Count} rejected sheet(s) to the previous stage as {ret.Name}.";
        return RedirectToAction("Details", new { id = ret.Id });
    }

    // ---- Live wall screen for the workshop: all active batches + latest moves, auto-refresh ----
    public async Task<IActionResult> Wall()
    {
        var active = await _db.Batches
            .Include(b => b.CurrentHolder).Include(b => b.CreatedBy)
            .Where(b => b.Status == BatchStatus.InReview || b.Status == BatchStatus.Draft)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        var counts = await _db.ProcessSheets.Where(s => s.BatchId != null)
            .GroupBy(s => s.BatchId!.Value)
            .Select(g => new { BatchId = g.Key, Count = g.Count() }).ToListAsync();
        ViewBag.Counts = counts.ToDictionary(x => x.BatchId, x => x.Count);

        var ids = active.Select(b => b.Id).ToList();
        var lastMoves = await _db.BatchTransfers
            .Where(t => ids.Contains(t.BatchId))
            .Include(t => t.FromUser).Include(t => t.ToUser)
            .OrderByDescending(t => t.At).Take(60).ToListAsync();
        ViewBag.LastMoves = lastMoves.GroupBy(t => t.BatchId).ToDictionary(g => g.Key, g => g.First());
        ViewBag.Ticker = lastMoves.Take(12).ToList();

        // Done today (small footer strip)
        ViewBag.DoneToday = await _db.Batches.CountAsync(b => b.Status == BatchStatus.Approved);
        return View(active);
    }

    // ---- History: every batch, for Planning/Admin only ----
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> History(string? q)
    {
        var query = _db.Batches.Include(b => b.CreatedBy).Include(b => b.CurrentHolder).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            q = q.Trim();
            query = query.Where(b => b.Name.Contains(q)
                || (b.ProjectName != null && b.ProjectName.Contains(q))
                || (b.ContractNo != null && b.ContractNo.Contains(q)));
        }
        var batches = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();

        var counts = await _db.ProcessSheets.Where(s => s.BatchId != null)
            .GroupBy(s => s.BatchId!.Value)
            .Select(g => new { BatchId = g.Key, Count = g.Count() }).ToListAsync();
        ViewBag.Counts = counts.ToDictionary(x => x.BatchId, x => x.Count);
        ViewBag.Query = q;
        return View(batches);
    }

    // ---- Download every batch (with dates + sheet counts) as one spreadsheet ----
    public async Task<IActionResult> ExportHistory()
    {
        var batches = await _db.Batches
            .Include(b => b.CreatedBy).Include(b => b.CurrentHolder)
            .OrderByDescending(b => b.CreatedAt).ToListAsync();

        var counts = await _db.ProcessSheets.Where(s => s.BatchId != null)
            .GroupBy(s => s.BatchId!.Value)
            .Select(g => new { BatchId = g.Key, Count = g.Count() }).ToListAsync();
        var countMap = counts.ToDictionary(x => x.BatchId, x => x.Count);

        var headers = new List<string> { "Batch", "Project", "Contract No", "Sheets", "Status", "Now With", "Created By", "Created" };
        var rows = batches.Select(b => (IList<string>)new List<string>
        {
            b.Name, b.ProjectName ?? "", b.ContractNo ?? "",
            (countMap.TryGetValue(b.Id, out var c) ? c : 0).ToString(),
            b.Status.ToString(), b.CurrentHolder?.FullName ?? "", b.CreatedBy?.FullName ?? "",
            b.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();

        var bytes = XlsxWriter.Write("Batch History", headers, rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"batch-history-{DateTime.Now:yyyy-MM-dd}.xlsx");
    }

    // ---- Pipeline overview, for Planning/Admin only ----
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> Dashboard()
    {
        var batches = await _db.Batches
            .Include(b => b.CreatedBy).Include(b => b.CurrentHolder)
            .OrderByDescending(b => b.CreatedAt).ToListAsync();
        if (!User.IsAdmin())
        {
            int me = User.GetUserId();
            batches = batches.Where(b => b.CreatedById == me || b.CurrentHolderId == me).ToList();
        }
        var counts = await _db.ProcessSheets.Where(s => s.BatchId != null)
            .GroupBy(s => s.BatchId!.Value)
            .Select(g => new { BatchId = g.Key, Count = g.Count() }).ToListAsync();
        ViewBag.Counts = counts.ToDictionary(x => x.BatchId, x => x.Count);
        return View(batches);
    }

    // ================= BULK IMPORT (planners only) =================

    private static readonly Dictionary<string, string> OpAliases = BuildOpAliases();
    private static Dictionary<string, string> BuildOpAliases()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in OperationCatalog.Operations) { d[o.Key] = o.Key; d[o.Name] = o.Key; }
        d["cut&drill"] = "CUT_DRILL"; d["cut drill"] = "CUT_DRILL"; d["cutdrill"] = "CUT_DRILL"; d["cnc"] = "CUT_DRILL";
        d["cut"] = "CUTTING"; d["mill"] = "MILLING";
        d["galv"] = "GALVANIZE"; d["galvanize"] = "GALVANIZE"; d["galvanized"] = "GALVANIZE";
        return d;
    }

    private static List<ProcessOperation> BuildOps(string? spec)
    {
        var ops = new List<ProcessOperation>();
        int i = 0;
        foreach (var raw in (spec ?? "").Split(new[] { ';', '|', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (OpAliases.TryGetValue(raw, out var key) && key != "GALVANIZE")
                ops.Add(new ProcessOperation { OperationKey = key, SortOrder = i++ });
        }
        if (ops.Count == 0) ops.Add(new ProcessOperation { OperationKey = "CUT_DRILL", SortOrder = 0 });
        return ops;
    }

    // =============== Calibrate once on the first drawing, apply to the whole batch ===============
    private static string CalRoot => Path.Combine(AppContext.BaseDirectory, "import_calibration");
    private static string CalDir(string token) => Path.Combine(CalRoot, token);
    private static string ManifestPath(string token) => Path.Combine(CalDir(token), "manifest.json");
    private static string RenamedDir(string token) => Path.Combine(CalDir(token), "renamed");

    public class CalManifest
    {
        public int BatchId { get; set; }
        public string? NameField { get; set; }
        public Dictionary<string, double[]> Rects { get; set; } = new();
        public List<string> Failed { get; set; } = new();     // filenames still waiting on a manual box
        public int Created { get; set; }
    }

    private static async Task<CalManifest?> LoadManifest(string token)
    {
        var p = ManifestPath(token);
        if (!System.IO.File.Exists(p)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<CalManifest>(await System.IO.File.ReadAllTextAsync(p)); }
        catch { return null; }
    }

    private static async Task SaveManifest(string token, CalManifest m) =>
        await System.IO.File.WriteAllTextAsync(ManifestPath(token), System.Text.Json.JsonSerializer.Serialize(m));

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "" : name;
    }

    // Picks "{name}_pr.pdf", or "{name}_pr (2).pdf" etc. if that name is already used in this batch.
    private static string UniqueRenamedPath(string token, string baseName)
    {
        Directory.CreateDirectory(RenamedDir(token));
        string candidate = $"{baseName}_pr.pdf";
        int n = 2;
        while (System.IO.File.Exists(Path.Combine(RenamedDir(token), candidate)))
        {
            candidate = $"{baseName}_pr ({n}).pdf";
            n++;
        }
        return Path.Combine(RenamedDir(token), candidate);
    }

    public static readonly (string Key, string Label)[] CalibrationFields =
    {
        ("drawingNo", "Drawing No"), ("partNo", "Part No / Item"), ("section", "Section (e.g. L45*5)"),
        ("qty", "Qty"), ("material", "Material"), ("cutLength", "Length"), ("projectName", "Project Name"),
        ("diagram", "Diagram (drawing image)"),
    };

    // ---- Quick add: one drawing, filled from the saved calibration, straight to steps ----
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> QuickAddSheet(int batchId, IFormFile? file)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        if (file == null || file.Length == 0) { TempData["Err"] = "Choose a drawing PDF."; return RedirectToAction("Details", new { id = batchId }); }

        var saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Default");
        if (saved == null)
        {
            TempData["Err"] = "No saved layout yet — use Import drawings (auto-detect) first.";
            return RedirectToAction("Details", new { id = batchId });
        }

        string token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(CalDir(token));
        string dest = Path.Combine(CalDir(token), Path.GetFileName(file.FileName));
        await using (var fs = System.IO.File.Create(dest)) await file.CopyToAsync(fs);

        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(saved.RectsJson) ?? new();
            rects = raw.ToDictionary(kv => kv.Key,
                kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "The saved layout is unreadable — please recalibrate."; return RedirectToAction("Details", new { id = batchId }); }

        await ProcessFiles(new[] { dest }, token, batch, rects, saved.NameField);
        await _db.SaveChangesAsync();
        try { Directory.Delete(CalDir(token), true); } catch { /* best effort cleanup */ }

        var newest = await _db.ProcessSheets.Where(s => s.BatchId == batchId).OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        if (newest == null) { TempData["Err"] = "Could not read that drawing."; return RedirectToAction("Details", new { id = batchId }); }

        TempData["Msg"] = "Drawing read using your saved layout — now add its process steps.";
        return RedirectToAction("Edit", "ProcessSheets", new { id = newest.Id });
    }

    [HttpGet]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ImportPdfBatch(int batchId)
    {
        ViewBag.BatchId = batchId;
        ViewBag.Saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Default");
        return View();
    }

    // Step 1: upload the drawing PDFs. If a layout is already saved from a previous
    // import, skip straight to extraction — no re-marking. Otherwise (or if the user
    // chose to recalibrate) go mark the first drawing as before.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    [RequestSizeLimit(256L * 1024 * 1024)]
    public async Task<IActionResult> UploadForCalibration(int batchId, List<IFormFile> pdfs, bool recalibrate = false)
    {
        var files = (pdfs ?? new()).Where(f => f.Length > 0 &&
            f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { TempData["Err"] = "Choose one or more drawing PDFs."; return RedirectToAction("ImportPdfBatch", new { batchId }); }

        string token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(CalDir(token));
        int i = 0;
        foreach (var f in files)
        {
            string dest = Path.Combine(CalDir(token), $"{i:D4}_{Path.GetFileName(f.FileName)}");
            await using var fs = System.IO.File.Create(dest);
            await f.CopyToAsync(fs);
            i++;
        }

        var saved = recalibrate ? null : await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Default");
        if (saved != null)
        {
            // Reuse the remembered layout automatically — no calibration screen this time.
            return await RunCalibration(token, batchId, saved.RectsJson, saved.NameField);
        }
        return RedirectToAction("Calibrate", new { token, batchId });
    }

    // Step 2: show the first drawing, let the planner mark where each field lives.
    [HttpGet]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public IActionResult Calibrate(string token, int batchId)
    {
        var dir = CalDir(token);
        var first = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.pdf").OrderBy(p => p).FirstOrDefault() : null;
        if (first == null) { TempData["Err"] = "Upload session expired — please upload the drawings again."; return RedirectToAction("ImportPdfBatch", new { batchId }); }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(System.IO.File.ReadAllBytes(first));
            ViewBag.PreviewImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render the first drawing: " + ex.Message;
            return RedirectToAction("ImportPdfBatch", new { batchId });
        }

        ViewBag.Token = token;
        ViewBag.BatchId = batchId;
        ViewBag.FileCount = Directory.GetFiles(dir, "*.pdf").Length;
        ViewBag.Fields = CalibrationFields;
        return View();
    }

    // Step 3: apply the calibrated regions to every drawing in the upload and create sheets.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ApplyCalibration(string token, int batchId, string rectsJson, string? nameField)
    {
        // Remember this layout so future imports skip straight to extraction.
        var saved = await _db.SavedCalibrations.FirstOrDefaultAsync(c => c.Name == "Default");
        if (saved == null) { saved = new SavedCalibration { Name = "Default" }; _db.SavedCalibrations.Add(saved); }
        saved.RectsJson = rectsJson ?? "{}";
        saved.NameField = nameField;
        saved.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        CalibrationBackup.Save(saved);

        return await RunCalibration(token, batchId, rectsJson, nameField);
    }

    // Shared by both the manual "mark it now" path and the "reuse the saved layout" path.
    private async Task<IActionResult> RunCalibration(string token, int batchId, string? rectsJson, string? nameField)
    {
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();
        var dir = CalDir(token);
        if (!Directory.Exists(dir)) { TempData["Err"] = "Upload session expired."; return RedirectToAction("ImportPdfBatch", new { batchId }); }

        Dictionary<string, double[]> rawRects;
        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            rawRects = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(rectsJson ?? "{}") ?? new();
            rects = rawRects.ToDictionary(kv => kv.Key,
                kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "Could not read the marked regions — please try calibrating again."; return RedirectToAction("Calibrate", new { token, batchId }); }

        var manifest = new CalManifest { BatchId = batchId, NameField = nameField, Rects = rawRects };
        var (created, failedFiles) = await ProcessFiles(Directory.GetFiles(dir, "*.pdf").OrderBy(p => p), token, batch, rects, nameField);
        manifest.Created = created;
        manifest.Failed = failedFiles;
        await SaveManifest(token, manifest);
        await _db.SaveChangesAsync();

        return RedirectToAction("CalibrationResult", new { token, batchId });
    }

    // Extracts + creates a sheet for each PDF; auto-names/renames the file from the chosen field.
    // Returns (sheetsCreated, filesStillNeedingAManualBox).
    // Everything in here is stateless and touches no shared DbContext, only files and pure
    // image/text processing — safe to run many of these concurrently across CPU cores.
    // This is what used to be inline in the per-file send loop; pulling it out is what lets
    // that loop's expensive rendering work happen in parallel instead of one file at a time.
    private class BuiltSheetResult
    {
        public bool Success;
        public string? FailReason;
        public string SrcFileName = "";
        public string OriginFolder = "";
        public string DrawingNo = "", PartNo = "", Section = "", Material = "", CutLength = "", ProjectName = "", NamingValue = "";
        public int? Qty;
        public byte[]? OriginalPdf, FullDrawingImage, DiagramImage, StampedDrawingImage;
        public double NormX, NormY, WidthPct, FontScale;
        public bool AnchorRight, AnchorBottom;
    }

    private static async Task<BuiltSheetResult> BuildSheetAsync(
        string src, string originFolder, Dictionary<string, DrawingRegionExtractor.NormRect> textRects,
        DrawingImageExtractor.NormRect? diagramRect, string? nameField, string token,
        string sector, string dateStr, string issue, string copyNo,
        double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale)
    {
        string fileOnly = Path.GetFileName(src);
        var result = new BuiltSheetResult { SrcFileName = fileOnly, OriginFolder = originFolder };
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(src);
            var vals = DrawingRegionExtractor.ExtractFields(bytes, textRects);

            // Angle/Plate are expected to match the calibrated layout, so a failed naming
            // extraction there is treated as a real read failure. Other/Images are already
            // known to be unclassified/mismatched — requiring the naming field to succeed
            // there too would silently discard every single one of them, instead of
            // creating the sheet with blank fields for Planning to fill in by hand as intended.
            bool requireNaming = originFolder != "other" && originFolder != "images";
            string namingValue = !string.IsNullOrWhiteSpace(nameField)
                ? SanitizeFileName(vals.GetValueOrDefault(nameField, "")) : "";
            if (requireNaming && !string.IsNullOrEmpty(nameField) && namingValue == "")
            {
                result.Success = false; result.FailReason = $"{fileOnly} (naming field \"{nameField}\" came back empty)";
                return result;
            }

            result.DrawingNo = vals.GetValueOrDefault("drawingNo", "");
            result.PartNo = vals.GetValueOrDefault("partNo", "");
            result.Section = vals.GetValueOrDefault("section", "");
            result.Material = vals.GetValueOrDefault("material", "");
            result.CutLength = vals.GetValueOrDefault("cutLength", "");
            result.ProjectName = vals.GetValueOrDefault("projectName", "");
            if (int.TryParse(vals.GetValueOrDefault("qty", ""), out int qty) && qty > 0) result.Qty = qty;
            result.NamingValue = namingValue;
            result.OriginalPdf = bytes;

            try { result.FullDrawingImage = DrawingImageExtractor.RenderPreview(bytes).Png; }
            catch { /* full-page capture is best-effort */ }

            if (diagramRect != null)
            {
                try { result.DiagramImage = DrawingImageExtractor.CropNormalized(bytes, diagramRect); }
                catch { /* diagram crop is best-effort */ }
            }

            var over = ReadFileOverride(token, originFolder, fileOnly);
            result.NormX = over?.NormX ?? normX; result.NormY = over?.NormY ?? normY;
            result.AnchorRight = over?.AnchorRight ?? anchorRight; result.AnchorBottom = over?.AnchorBottom ?? anchorBottom;
            result.WidthPct = over?.WidthPct ?? widthPct; result.FontScale = over?.FontScale ?? fontScale;
            try
            {
                var stampPng = DocControlStampRenderer.Render(sector, dateStr, issue, copyNo, result.FontScale);
                var basisImage = (result.FullDrawingImage != null && result.FullDrawingImage.Length > 0) ? result.FullDrawingImage
                    : (result.DiagramImage != null && result.DiagramImage.Length > 0) ? result.DiagramImage : null;
                if (basisImage != null)
                    result.StampedDrawingImage = DocControlStampRenderer.Composite(
                        basisImage, stampPng, result.NormX, result.NormY, result.AnchorRight, result.AnchorBottom, result.WidthPct);
            }
            catch { /* stamping is best-effort */ }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false; result.FailReason = $"{fileOnly} ({ex.GetType().Name}: {ex.Message})";
            return result;
        }
    }

    private async Task<(int created, List<string> failed)> ProcessFiles(
            IEnumerable<string> paths, string token, Batch batch,
            Dictionary<string, DrawingRegionExtractor.NormRect> rects, string? nameField)
    {
        var textRects = rects.Where(r => r.Key != "diagram").ToDictionary(r => r.Key, r => r.Value);
        DrawingImageExtractor.NormRect? diagramRect = rects.TryGetValue("diagram", out var dr)
            ? new DrawingImageExtractor.NormRect(dr.X, dr.Y, dr.W, dr.H) : null;

        int created = 0;
        var failed = new List<string>();

        foreach (var path in paths)
        {
            string fileOnly = Path.GetFileName(path);
            try
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(path);
                var vals = DrawingRegionExtractor.ExtractFields(bytes, textRects);

                // The field chosen to both name the file and identify the record.
                string namingValue = !string.IsNullOrWhiteSpace(nameField)
                    ? SanitizeFileName(vals.GetValueOrDefault(nameField, ""))
                    : "";
                if (!string.IsNullOrEmpty(nameField) && namingValue == "")
                {
                    // Naming box came back blank on this file — hold it for a manual retry.
                    failed.Add(fileOnly);
                    continue;
                }

                var sheet = new ProcessSheet
                {
                    BatchId = batch.Id,
                    DrawingNo = vals.GetValueOrDefault("drawingNo", ""),
                    PartNo = vals.GetValueOrDefault("partNo", ""),
                    Section = vals.GetValueOrDefault("section", ""),
                    Material = vals.GetValueOrDefault("material", ""),
                    CutLength = vals.GetValueOrDefault("cutLength", ""),
                    ProjectName = vals.GetValueOrDefault("projectName", batch.ProjectName ?? ""),
                    IncludeGalvanizing = false,
                    SheetNo = "1/1",
                };
                if (int.TryParse(vals.GetValueOrDefault("qty", ""), out int qty) && qty > 0) sheet.Qty = qty;
                sheet.Operations.Add(new ProcessOperation { OperationKey = "CUT_DRILL" });
                BatchFooter.ApplyTo(batch, sheet);

                // The whole page (title block, BOM tables, everything) — separate from the
                // small cropped diagram, so a reviewer can open the complete original drawing.
                try
                {
                    var full = DrawingImageExtractor.RenderPreview(bytes);
                    sheet.FullDrawingImage = full.Png;
                    sheet.FullDrawingMime = "image/png";
                }
                catch { /* full-page capture is best-effort */ }

                if (diagramRect != null)
                {
                    try
                    {
                        var crop = DrawingImageExtractor.CropNormalized(bytes, diagramRect);
                        if (crop != null) { sheet.DiagramImage = crop; sheet.DiagramMime = "image/png"; }
                    }
                    catch { /* diagram crop is best-effort */ }
                }

                _db.ProcessSheets.Add(sheet);

                // Rename a copy of the original PDF to "{value}_pr.pdf" for download.
                if (namingValue != "")
                {
                    var dest = UniqueRenamedPath(token, namingValue);
                    System.IO.File.Copy(path, dest, overwrite: false);
                }
                System.IO.File.Delete(path);   // processed — remove from the pending pile
                created++;
            }
            catch
            {
                failed.Add(fileOnly);
            }
        }
        return (created, failed);
    }

    // Results screen: how many were created, and which files still need a manual box.
    [HttpGet]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> CalibrationResult(string token, int batchId)
    {
        var manifest = await LoadManifest(token);
        if (manifest == null) { TempData["Err"] = "Session expired."; return RedirectToAction("Details", new { id = batchId }); }
        ViewBag.Token = token;
        ViewBag.BatchId = batchId;
        ViewBag.HasZip = Directory.Exists(RenamedDir(token)) && Directory.GetFiles(RenamedDir(token)).Length > 0;
        return View(manifest);
    }

    [HttpGet]
    [Authorize(Roles = "Planner,Admin")]
    public IActionResult DownloadRenamed(string token)
    {
        var dir = RenamedDir(token);
        if (!Directory.Exists(dir)) return NotFound();
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                var entry = zip.CreateEntry(Path.GetFileName(f), System.IO.Compression.CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = System.IO.File.OpenRead(f);
                fs.CopyTo(es);
            }
        }
        ms.Position = 0;
        return File(ms.ToArray(), "application/zip", "renamed-drawings.zip");
    }

    // ---- Retry: mark just the naming box on one file that came back blank ----
    [HttpGet]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> RetryOne(string token, int batchId, string file)
    {
        var manifest = await LoadManifest(token);
        if (manifest == null) { TempData["Err"] = "Session expired."; return RedirectToAction("Details", new { id = batchId }); }
        var path = Path.Combine(CalDir(token), file);
        if (!System.IO.File.Exists(path)) { TempData["Err"] = "That file is no longer available."; return RedirectToAction("CalibrationResult", new { token, batchId }); }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(await System.IO.File.ReadAllBytesAsync(path));
            ViewBag.PreviewImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render this drawing: " + ex.Message;
            return RedirectToAction("CalibrationResult", new { token, batchId });
        }

        ViewBag.Token = token;
        ViewBag.BatchId = batchId;
        ViewBag.File = file;
        ViewBag.NameFieldLabel = CalibrationFields.FirstOrDefault(f => f.Key == manifest.NameField).Label ?? manifest.NameField;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> ApplyRetryOne(string token, int batchId, string file, string rectJson)
    {
        var manifest = await LoadManifest(token);
        if (manifest == null) { TempData["Err"] = "Session expired."; return RedirectToAction("Details", new { id = batchId }); }
        var batch = await _db.Batches.FindAsync(batchId);
        if (batch == null) return NotFound();

        double[] boxArr;
        try { boxArr = System.Text.Json.JsonSerializer.Deserialize<double[]>(rectJson) ?? Array.Empty<double>(); }
        catch { boxArr = Array.Empty<double>(); }
        if (boxArr.Length < 4 || string.IsNullOrEmpty(manifest.NameField))
        {
            TempData["Err"] = "Draw a box first.";
            return RedirectToAction("RetryOne", new { token, batchId, file });
        }

        // Use the SAME batch-wide rects for the other fields, but this file's own box for naming.
        var rects = manifest.Rects.ToDictionary(kv => kv.Key,
            kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        rects[manifest.NameField] = new DrawingRegionExtractor.NormRect(boxArr[0], boxArr[1], boxArr[2], boxArr[3]);

        var path = Path.Combine(CalDir(token), file);
        var (created, stillFailed) = await ProcessFiles(new[] { path }, token, batch, rects, manifest.NameField);

        manifest.Created += created;
        manifest.Failed.Remove(file);
        if (stillFailed.Count > 0)
        {
            manifest.Failed.Add(file);
            TempData["Err"] = "Still blank in that spot — try marking a slightly different area.";
        }
        await SaveManifest(token, manifest);
        await _db.SaveChangesAsync();
        return RedirectToAction("CalibrationResult", new { token, batchId });
    }

    // Finish up: whatever wasn't resolved is simply left out of the batch.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public IActionResult FinishCalibration(string token, int batchId)
    {
        try { Directory.Delete(CalDir(token), true); } catch { /* best effort cleanup */ }
        TempData["Msg"] = "Import finished.";
        return RedirectToAction("Details", new { id = batchId });
    }

    [HttpGet]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> ImportCsv()
    {
        ViewBag.Recipients = await RecipientsAsync(User.GetUserId());
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> ImportCsv(IFormFile? file, string? batchName, string? projectName, int? sendTo, string? note)
    {
        if (file == null || file.Length == 0) { TempData["Err"] = "Choose a CSV or XLSX file."; return RedirectToAction("ImportCsv"); }

        List<string[]> table;
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms); ms.Position = 0;
            table = TableImport.Read(file.FileName, ms);
        }
        catch { TempData["Err"] = "Could not read that file. Save it as CSV or a standard .xlsx."; return RedirectToAction("ImportCsv"); }

        if (table.Count < 2) { TempData["Err"] = "The file has a header but no data rows."; return RedirectToAction("ImportCsv"); }

        var header = table[0];
        int Col(params string[] names)
        {
            for (int i = 0; i < header.Length; i++)
            {
                var h = header[i].Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace(".", "");
                if (names.Contains(h)) return i;
            }
            return -1;
        }
        int ciPart = Col("partno", "part"), ciDraw = Col("drawingno", "drawing"), ciProj = Col("projectname", "project"),
            ciSec = Col("section"), ciQty = Col("qty", "quantity"), ciLen = Col("cutlength", "length"),
            ciMat = Col("material"), ciOps = Col("operations", "ops"),
            ciContract = Col("contractno", "contract"), ciWo = Col("workorderno", "workorder"),
            ciGalv = Col("galvanize", "galvanized", "galv");

        if (ciPart < 0 || ciDraw < 0 || ciSec < 0)
        { TempData["Err"] = "Missing required columns: need at least Part No, Drawing No and Section."; return RedirectToAction("ImportCsv"); }

        int me = User.GetUserId();
        var batch = new Batch
        {
            Name = string.IsNullOrWhiteSpace(batchName) ? $"BATCH-{DateTime.Now:yyyyMMdd-HHmm}" : batchName.Trim(),
            ProjectName = projectName?.Trim(),
            CreatedById = me,
            CreatedAt = DateTime.Now,
            Status = BatchStatus.Draft,
        };
        _db.Batches.Add(batch);
        await _db.SaveChangesAsync();

        string Get(string[] r, int i) => i >= 0 && i < r.Length ? r[i].Trim() : "";
        int created = 0, skipped = 0;
        var toAdd = new List<ProcessSheet>();
        for (int rI = 1; rI < table.Count; rI++)
        {
            var r = table[rI];
            string part = Get(r, ciPart), draw = Get(r, ciDraw), sec = Get(r, ciSec);
            if (part == "" && draw == "" && sec == "") continue;
            if (part == "" || draw == "" || sec == "") { skipped++; continue; }

            int.TryParse(Get(r, ciQty), out int qty);
            string mat = Get(r, ciMat); if (mat == "") mat = OperationCatalog.DefaultMaterial(draw);
            string proj = Get(r, ciProj); if (proj == "") proj = projectName ?? "";
            string galv = Get(r, ciGalv).ToLowerInvariant();
            bool includeGalv = ciGalv < 0 || !(galv == "no" || galv == "false" || galv == "0");

            toAdd.Add(new ProcessSheet
            {
                PartNo = part,
                DrawingNo = draw,
                ProjectName = proj,
                Section = sec,
                Qty = qty,
                CutLength = Get(r, ciLen),
                Material = mat,
                ContractNo = Get(r, ciContract),
                WorkOrderNo = Get(r, ciWo),
                IncludeGalvanizing = includeGalv,
                SheetNo = "1/1",
                BatchId = batch.Id,
                CreatedAt = DateTime.Now,
                Operations = BuildOps(Get(r, ciOps)),
            });
            created++;
        }
        _db.ProcessSheets.AddRange(toAdd);
        FinishImport(batch, me, sendTo, note, "Created (CSV/XLSX import)");
        await _db.SaveChangesAsync();

        TempData["Msg"] = $"Imported {created} sheet(s)" + (skipped > 0 ? $", skipped {skipped} incomplete row(s)." : ".");
        return RedirectToAction("Details", new { id = batch.Id });
    }

    [HttpGet]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> ImportPdf()
    {
        ViewBag.Recipients = await RecipientsAsync(User.GetUserId());
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> ImportPdf(List<IFormFile> files, string? batchName, string? projectName,
        string? operations, int? sendTo, string? note)
    {
        if (files == null || files.Count == 0) { TempData["Err"] = "Choose one or more PDF files."; return RedirectToAction("ImportPdf"); }

        int me = User.GetUserId();
        var batch = new Batch
        {
            Name = string.IsNullOrWhiteSpace(batchName) ? $"BATCH-{DateTime.Now:yyyyMMdd-HHmm}" : batchName.Trim(),
            ProjectName = projectName?.Trim(),
            CreatedById = me,
            CreatedAt = DateTime.Now,
            Status = BatchStatus.Draft,
        };
        _db.Batches.Add(batch);
        await _db.SaveChangesAsync();

        int created = 0, failed = 0;
        var toAdd = new List<ProcessSheet>();
        foreach (var f in files)
        {
            if (f.Length == 0) continue;
            try
            {
                DrawingData d;
                using (var ms = new MemoryStream()) { await f.CopyToAsync(ms); ms.Position = 0; d = DrawingParser.Parse(ms); }
                string draw = !string.IsNullOrWhiteSpace(d.Item) ? d.Item! : Path.GetFileNameWithoutExtension(f.FileName);
                string proj = !string.IsNullOrWhiteSpace(d.ProjectName) ? d.ProjectName! : (projectName ?? "");
                string mat = !string.IsNullOrWhiteSpace(d.MaterialOption) ? d.MaterialOption! : OperationCatalog.DefaultMaterial(draw);
                toAdd.Add(new ProcessSheet
                {
                    PartNo = draw,
                    DrawingNo = draw,
                    ProjectName = proj,
                    Section = d.Section ?? "",
                    Qty = int.TryParse(d.Qty, out int q) ? q : 0,
                    CutLength = d.Length ?? "",
                    Material = mat,
                    IncludeGalvanizing = false,
                    SheetNo = "1/1",
                    BatchId = batch.Id,
                    CreatedAt = DateTime.Now,
                    Operations = BuildOps(operations),
                });
                created++;
            }
            catch { failed++; }
        }
        _db.ProcessSheets.AddRange(toAdd);
        FinishImport(batch, me, sendTo, note, "Created (PDF import)");
        await _db.SaveChangesAsync();

        TempData["Msg"] = $"Imported {created} sheet(s)" + (failed > 0 ? $", {failed} could not be read." : ".");
        return RedirectToAction("Details", new { id = batch.Id });
    }

    private void FinishImport(Batch batch, int me, int? sendTo, string? note, string createAction)
    {
        batch.Transfers.Add(new BatchTransfer { ToUserId = me, Action = createAction, At = DateTime.Now });
        if (sendTo.HasValue)
        {
            batch.CurrentHolderId = sendTo.Value;
            batch.Status = BatchStatus.InReview;
            batch.Transfers.Add(new BatchTransfer { FromUserId = me, ToUserId = sendTo.Value, Action = "Sent", Note = note?.Trim(), At = DateTime.Now });
        }
    }

    // ================= EXPORT / VIEW ALL SHEETS =================

    private static string SafeName(string s) =>
        string.Concat(s.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));

    // All sheets on one page. format=word downloads a .doc; otherwise it's the
    // print/PDF view (open, then Print → Save as PDF).
    public async Task<IActionResult> Export(int id, string? format)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var sheets = await _db.ProcessSheets
            .Where(s => s.BatchId == id).OrderBy(s => s.Id)
            .Include(s => s.Operations).Include(s => s.Reviews).Include(s => s.Batch).ThenInclude(bt => bt!.Placements)
            .ToListAsync();

        var vm = new BatchExportVm { Batch = batch, Sheets = sheets };
        var result = View("ExportSheets", vm);
        if (format == "word")
        {
            result.ContentType = "application/msword";
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"Batch-{SafeName(batch.Name)}.doc\"");
        }
        else if (format == "excel")
        {
            // Excel opens the route-card HTML as a formatted spreadsheet (merged cells, borders).
            result.ContentType = "application/vnd.ms-excel";
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"Batch-{SafeName(batch.Name)}.xls\"");
        }
        return result;
    }

    // Native .xlsx: each sheet a formatted worksheet with the drawing embedded.
    public async Task<IActionResult> ExportExcel(int id)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var sheets = await _db.ProcessSheets
            .Where(s => s.BatchId == id).OrderBy(s => s.Id)
            .Include(s => s.Operations).Include(s => s.Reviews).Include(s => s.Batch)
            .ToListAsync();

        var bytes = ExcelRouteCardWriter.Build(sheets);
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Batch-{SafeName(batch.Name)}.xlsx");
    }

    // Download every drawing in a batch — either combined into one PDF, or as separate
    // PDFs bundled in a zip. Not the process-sheet paperwork — the actual drawing images.
    public async Task<IActionResult> DownloadDrawings(int id, string mode, bool onlySentToMe = false)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var query = _db.ProcessSheets.Where(s => s.BatchId == id);
        if (onlySentToMe) query = query.Where(s => s.SentToQualityUserId == User.GetUserId());

        var sheets = await query
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.DrawingNo, s.StampedDrawingImage, s.FullDrawingImage, s.DiagramImage })
            .ToListAsync();

        var items = new List<DrawingPdfBuilder.Item>();
        foreach (var s in sheets)
        {
            var img = (s.StampedDrawingImage != null && s.StampedDrawingImage.Length > 0) ? s.StampedDrawingImage
                : (s.FullDrawingImage != null && s.FullDrawingImage.Length > 0) ? s.FullDrawingImage
                : (s.DiagramImage != null && s.DiagramImage.Length > 0) ? s.DiagramImage : null;
            if (img == null) continue;
            string name = string.IsNullOrWhiteSpace(s.DrawingNo) ? $"drawing-{s.Id}" : s.DrawingNo;
            items.Add(new DrawingPdfBuilder.Item(name, img));
        }

        if (items.Count == 0)
        {
            TempData["Err"] = "No drawing images are stored in this batch to download.";
            return RedirectToAction("Details", new { id });
        }

        string safeBatchName = SafeName(batch.Name);
        if (mode == "zip")
        {
            var zipBytes = DrawingPdfBuilder.BuildZipOfPdfs(items);
            return File(zipBytes, "application/zip", $"Drawings-{safeBatchName}.zip");
        }
        else
        {
            var pdfBytes = DrawingPdfBuilder.BuildSinglePdf(items);
            return File(pdfBytes, "application/pdf", $"Drawings-{safeBatchName}.pdf");
        }
    }

    // Download the actual process sheets (route cards) — only ones that have genuinely
    // been converted (Research drawings that are still just drawings don't have real
    // process-sheet data to export). Reuses the same Word-compatible HTML export the
    // "View all / PDF" button already produces, either combined into one file or as a
    // separate file per sheet in a zip.
    public async Task<IActionResult> DownloadProcessSheets(int id, string mode, bool onlySentToMe = false)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var query = _db.ProcessSheets.Where(s => s.BatchId == id && s.ConvertedToProcess);
        if (onlySentToMe) query = query.Where(s => s.SentToQualityUserId == User.GetUserId());

        var sheets = await query
            .OrderBy(s => s.Id)
            .Include(s => s.Operations).Include(s => s.Reviews).Include(s => s.Batch).ThenInclude(bt => bt!.Placements)
            .ToListAsync();

        if (sheets.Count == 0)
        {
            TempData["Err"] = "No sheets in this batch have been converted to a process sheet yet.";
            return RedirectToAction("Details", new { id });
        }

        string safeBatchName = SafeName(batch.Name);
        if (mode == "zip")
        {
            using var zipStream = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sheet in sheets)
                {
                    var html = await RenderViewToStringAsync("ExportSheets", new BatchExportVm { Batch = batch, Sheets = new List<ProcessSheet> { sheet } });
                    string baseName = SafeName(string.IsNullOrWhiteSpace(sheet.DrawingNo) ? $"sheet-{sheet.Id}" : sheet.DrawingNo);
                    string fileName = baseName + ".doc";
                    int n = 2;
                    while (!usedNames.Add(fileName)) { fileName = $"{baseName} ({n}).doc"; n++; }
                    var entry = zip.CreateEntry(fileName, System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                    await entryStream.WriteAsync(bytes);
                }
            }
            return File(zipStream.ToArray(), "application/zip", $"ProcessSheets-{safeBatchName}.zip");
        }
        else
        {
            var html = await RenderViewToStringAsync("ExportSheets", new BatchExportVm { Batch = batch, Sheets = sheets });
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            return File(bytes, "application/msword", $"ProcessSheets-{safeBatchName}.doc");
        }
    }

    // A plain one-row-per-sheet register.
    public async Task<IActionResult> ExportRegister(int id)
    {
        var batch = await _db.Batches.FindAsync(id);
        if (batch == null) return NotFound();

        var sheets = await _db.ProcessSheets
            .Where(s => s.BatchId == id).OrderBy(s => s.Id)
            .Include(s => s.Operations).ToListAsync();

        var headers = new List<string>
        {
            "#", "Drawing No", "Part No", "Section", "Qty", "Total Qty", "Material",
            "Cut Length", "Contract No", "Work Order", "Sheet No", "Operations",
            "Complied By", "Checked By",
        };
        var rows = new List<IList<string>>();
        int i = 1;
        foreach (var s in sheets)
        {
            rows.Add(new List<string>
            {
                (i++).ToString(), s.DrawingNo, s.PartNo, s.Section, s.Qty.ToString(),
                s.TotalQty ?? "", s.Material, s.CutLength ?? "", s.ContractNo ?? "",
                s.WorkOrderNo ?? "", s.SheetNo ?? "", s.Operations.Count.ToString(),
                s.CompiledBy ?? "", s.CheckedBy ?? "",
            });
        }
        var bytes = XlsxWriter.Write($"Batch {batch.Name}", headers, rows);
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Batch-{SafeName(batch.Name)}-list.xlsx");
    }

    // ---- Create a batch and pull in unassigned sheets ----
    [HttpGet]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> Create()
    {
        ViewBag.Unassigned = await _db.ProcessSheets.CountAsync(s => s.BatchId == null);
        ViewBag.Recipients = await RecipientsAsync(User.GetUserId());
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> Create(string name, string? projectName)
    {
        int me = User.GetUserId();
        var batch = new Batch
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"BATCH-{DateTime.Now:yyyyMMdd-HHmm}" : name.Trim(),
            ProjectName = projectName?.Trim(),
            CreatedById = me,
            CreatedAt = DateTime.Now,
            Status = BatchStatus.Draft,
        };
        _db.Batches.Add(batch);
        await _db.SaveChangesAsync();

        batch.Transfers.Add(new BatchTransfer { ToUserId = me, Action = "Created", At = DateTime.Now });
        await _db.SaveChangesAsync();
        return RedirectToAction("Details", new { id = batch.Id });
    }
}