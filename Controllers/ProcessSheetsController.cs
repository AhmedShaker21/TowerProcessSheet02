using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;
using TowerProcessSheet.Services;

namespace TowerProcessSheet.Controllers;

[Authorize]
public class ProcessSheetsController : Controller
{
    private readonly AppDbContext _db;

    public ProcessSheetsController(AppDbContext db) => _db = db;

    // GET: /  -> list of all saved sheets, for Planning/Admin only
    [Authorize(Roles = "Planner,Admin")]
    public async Task<IActionResult> Index()
    {
        var sheets = await _db.ProcessSheets
            .Include(s => s.Operations)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        return View(sheets);
    }

    // GET: /ProcessSheets/Details/5  -> the generated, form-style route card
    public async Task<IActionResult> Details(int id)
    {
        var sheet = await LoadWithOperationsAsync(id);
        if (sheet == null) return NotFound();

        // Research sends stamped drawings, not fabrication route cards — a sheet with a
        // stamped image has nothing meaningful in the process-sheet fields, so open the
        // actual drawing instead of a mostly-empty route card. Once Planning converts it
        // (adds the real fields and Steps), it becomes a genuine route card again.
        if (!sheet.ConvertedToProcess && sheet.StampedDrawingImage != null && sheet.StampedDrawingImage.Length > 0)
            return RedirectToAction("FullDrawing", new { id });

        ViewBag.Rows = OperationCatalog.Generate(sheet);
        int myUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int uid) ? uid : 0;
        var me = await _db.Users.FindAsync(myUserId);
        ViewBag.IsQualityInspector = me != null && me.Slot == SignOffSlot.QualityInspector;

        var latestQualityReview = sheet.Reviews.Where(r => r.Slot == SignOffSlot.QualityInspector)
            .OrderByDescending(r => r.ReviewedAt).FirstOrDefault();
        bool isRejected = latestQualityReview?.Decision == ReviewDecision.Rejected;
        bool alreadyResent = isRejected && sheet.ResentForReviewAt.HasValue && sheet.ResentForReviewAt > latestQualityReview!.ReviewedAt;
        ViewBag.LatestQualityReview = latestQualityReview;
        ViewBag.IsRejected = isRejected;
        ViewBag.AlreadyResent = alreadyResent;
        ViewBag.CanResend = isRejected && !alreadyResent && me != null && (me.IsPlanner || me.IsCoordinator || User.IsAdmin());
        if (sheet.BatchId.HasValue)
        {
            var nextId = await _db.ProcessSheets
                .Where(s => s.BatchId == sheet.BatchId && s.Id != id
                    && s.OriginalPdf != null && s.OriginalPdf.Length > 0 && !s.ConvertedToProcess)
                .OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync();
            ViewBag.NextToConvertId = nextId;
        }
        return View(sheet);
    }

    // GET: /ProcessSheets/Create  (optionally into a specific batch)
    public async Task<IActionResult> Create(int? batchId)
    {
        if (batchId.HasValue)
        {
            var batch = await _db.Batches.FindAsync(batchId.Value);
            ViewBag.Batch = batch;
        }
        var model = new ProcessSheet
        {
            BatchId = batchId,
            IncludeGalvanizing = false,
            SheetNo = "1/1",
            Operations = { new ProcessOperation { OperationKey = "CUT_DRILL" } },
        };
        return View(model);
    }

    // POST: /ProcessSheets/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProcessSheet sheet, IFormFile? drawingFile,
        string? diagramData, double? cropX, double? cropY, double? cropW, double? cropH,
        int? batchId, string? andAdd)
    {
        CleanAndReindexOperations(sheet);

        if (!ModelState.IsValid)
        {
            if (batchId.HasValue) ViewBag.Batch = await _db.Batches.FindAsync(batchId.Value);
            EnsureAtLeastOneOperation(sheet);
            return View(sheet);
        }

        await AttachDiagramAsync(sheet, diagramData, drawingFile, cropX, cropY, cropW, cropH);

        sheet.BatchId = batchId;
        if (batchId.HasValue)
        {
            var batch = await _db.Batches.FindAsync(batchId.Value);
            if (batch != null) BatchFooter.ApplyTo(batch, sheet);
        }
        _db.ProcessSheets.Add(sheet);
        await _db.SaveChangesAsync();

        // Building up a batch: keep the form open to add the next sheet.
        if (batchId.HasValue)
        {
            if (!string.IsNullOrEmpty(andAdd))
            {
                TempData["Msg"] = $"Sheet {sheet.DrawingNo} added to the batch.";
                return RedirectToAction(nameof(Create), new { batchId });
            }
            TempData["Msg"] = "Sheet created — now add its process steps.";
            return RedirectToAction(nameof(Edit), new { id = sheet.Id });
        }
        TempData["Msg"] = "Sheet created — now add its process steps.";
        return RedirectToAction(nameof(Edit), new { id = sheet.Id });
    }

    // GET: /ProcessSheets/Edit/5
    // POST: /ProcessSheets/ResendForReview/5 -> after fixing a Rejected sheet, send it back
    // to Quality's attention without touching their stamp or review history at all.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ResendForReview(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        sheet.ResentForReviewAt = DateTime.Now;
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Sent back to Quality for re-review.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var sheet = await LoadWithOperationsAsync(id);
        if (sheet == null) return NotFound();
        EnsureAtLeastOneOperation(sheet);

        if (sheet.BatchId.HasValue)
        {
            var nextId = await _db.ProcessSheets
                .Where(s => s.BatchId == sheet.BatchId && s.Id != id
                    && s.OriginalPdf != null && s.OriginalPdf.Length > 0 && !s.ConvertedToProcess)
                .OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync();
            ViewBag.NextToConvertId = nextId;

            // Reference Part List for this sheet's project, when one was uploaded — handy
            // right here, when a field didn't come out of the drawing and needs filling by hand.
            var projectId = await _db.Batches.Where(b => b.Id == sheet.BatchId)
                .Select(b => b.ProjectId).FirstOrDefaultAsync();
            if (projectId.HasValue && await _db.ProjectPartLists.AnyAsync(p => p.ProjectId == projectId))
                ViewBag.PartListProjectId = projectId;
        }
        return View(sheet);
    }

    // POST: /ProcessSheets/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ProcessSheet sheet, IFormFile? drawingFile,
        string? diagramData, double? cropX, double? cropY, double? cropW, double? cropH)
    {
        if (id != sheet.Id) return NotFound();

        CleanAndReindexOperations(sheet);

        if (!ModelState.IsValid)
        {
            EnsureAtLeastOneOperation(sheet);
            return View(sheet);
        }

        var existing = await _db.ProcessSheets
            .Include(s => s.Operations)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (existing == null) return NotFound();

        existing.ContractNo = sheet.ContractNo;
        existing.PartNo = sheet.PartNo;
        existing.DrawingNo = sheet.DrawingNo;
        existing.ProjectName = sheet.ProjectName;
        existing.WorkOrderNo = sheet.WorkOrderNo;
        existing.Section = sheet.Section;
        existing.Qty = sheet.Qty;
        existing.CutLength = sheet.CutLength;
        existing.Material = sheet.Material;
        existing.IncludeGalvanizing = sheet.IncludeGalvanizing;
        existing.TotalQty = sheet.TotalQty;
        existing.AssembledOn = sheet.AssembledOn;
        existing.QualityInsp = sheet.QualityInsp;
        // CompiledBy / CheckedBy are intentionally NOT mapped from the posted form here —
        // the Steps form has no input fields for them, so they'd always come through blank
        // and wipe out whatever "Convert to Process" correctly auto-set.
        existing.SheetNo = sheet.SheetNo;
        existing.SheetDate = sheet.SheetDate;

        // Replace the diagram only if a new image/PDF was provided; otherwise keep it.
        await AttachDiagramAsync(existing, diagramData, drawingFile, cropX, cropY, cropW, cropH);

        _db.ProcessOperations.RemoveRange(existing.Operations);
        existing.Operations = sheet.Operations.Select(o => { o.Id = 0; return o; }).ToList();

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET: /ProcessSheets/DiagramImage/5  -> the cropped diagram
    public async Task<IActionResult> DiagramImage(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet?.DiagramImage == null || sheet.DiagramImage.Length == 0)
            return NotFound();
        return File(sheet.DiagramImage, sheet.DiagramMime ?? "image/png");
    }

    // GET: /ProcessSheets/FullDrawingImage/5 -> the whole original page, tables included
    // (or the Document-Control-stamped version, if Research has stamped this one)
    public async Task<IActionResult> FullDrawingImage(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (sheet.StampedDrawingImage != null && sheet.StampedDrawingImage.Length > 0)
            return File(sheet.StampedDrawingImage, "image/png");
        if (sheet.FullDrawingImage == null || sheet.FullDrawingImage.Length == 0)
            return NotFound();
        return File(sheet.FullDrawingImage, sheet.FullDrawingMime ?? "image/png");
    }

    // GET: /ProcessSheets/FullDrawing/5 -> opens the full drawing filling the whole window
    // GET: /ProcessSheets/Preview/5 -> the route card alone, no layout — embedded scaled
    // down (via iframe) as a real live preview in thumbnail cards.
    // GET: /ProcessSheets/Preview/5 -> the route card alone, no layout — embedded scaled
    // down (via iframe) as a real live preview in thumbnail cards.
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Preview(int id)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        var sheet = await LoadWithOperationsAsync(id);
        if (sheet == null) return NotFound();
        return View("Preview", sheet);
    }

    public async Task<IActionResult> FullDrawing(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        bool hasStamped = sheet.StampedDrawingImage != null && sheet.StampedDrawingImage.Length > 0;
        bool hasFull = sheet.FullDrawingImage != null && sheet.FullDrawingImage.Length > 0;
        bool hasCropped = sheet.DiagramImage != null && sheet.DiagramImage.Length > 0;
        if (!hasStamped && !hasFull && !hasCropped) return NotFound();
        ViewBag.HasFull = hasStamped || hasFull;
        ViewBag.DrawingNo = sheet.DrawingNo;
        return View(sheet);
    }

    // ---- Convert a Research-originated drawing into a full process sheet: pull the rest
    // of the fields (Drawing No, Qty, Material) from the kept PDF, add the default Steps,
    // then go straight to the Steps editor. Uses the shared "Default" layout, the same one
    // Planning's own Import Drawings already relies on — usually needs marking only once,
    // ever, across the whole system. ----
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertToProcess(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (sheet.OriginalPdf == null || sheet.OriginalPdf.Length == 0)
        {
            TempData["Err"] = "The original drawing file isn't available for this sheet, so it can't be auto-converted.";
            return RedirectToAction("FullDrawing", new { id });
        }

        // Each batch (each delivery from outside — Research or anywhere else) gets its
        // own layout, marked once on its first conversion and reused automatically for
        // every other sheet in that same batch. A different batch's drawings can look
        // completely different, so nothing is shared globally here.
        var batch = sheet.BatchId.HasValue ? await _db.Batches.FindAsync(sheet.BatchId.Value) : null;
        if (batch == null || string.IsNullOrWhiteSpace(batch.ConvertRectsJson))
            return RedirectToAction("ConvertCalibrate", new { id });

        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(batch.ConvertRectsJson) ?? new();
            rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { return RedirectToAction("ConvertCalibrate", new { id }); }

        int myUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int uid) ? uid : 0;
        var me = await _db.Users.FindAsync(myUserId);
        var ocrRead = ApplyExtractionToSheet(sheet, rects, batch.ConvertNameField, me);
        await _db.SaveChangesAsync();

        // This sheet came from Research's "Other" bucket — the batch-wide layout above
        // was calibrated on a different (correctly-classified) sheet in the same batch,
        // so it may not actually line up with this one. If nothing usable came out,
        // don't leave it silently blank — send them to mark this one sheet specifically.
        if (sheet.IsScannedImage && string.IsNullOrWhiteSpace(sheet.PartNo) && string.IsNullOrWhiteSpace(sheet.Section))
        {
            TempData["Msg"] = "This drawing is a scanned image with no text layer at all — there's nothing here for a marked box " +
                "to find no matter where it's placed. Please type its fields in by hand below.";
            return RedirectToAction("ConvertPreviewDiagram", new { id });
        }
        if (sheet.IsUnclassified && string.IsNullOrWhiteSpace(sheet.PartNo) && string.IsNullOrWhiteSpace(sheet.Section))
        {
            TempData["Msg"] = "This drawing was in Research's \"Other\" bucket and its fields came back empty using this batch's " +
                "layout — mark them for this drawing specifically, or type them in by hand.";
            return RedirectToAction("ConvertCalibrateThisSheet", new { id });
        }
        TempData["Msg"] = "Converted — add the Steps for this sheet, then move on to the next drawing." + OcrNote(ocrRead);
        return RedirectToAction("ConvertPreviewDiagram", new { id });
    }

    // ---- A sheet from Research's "Other" bucket may not match the batch-wide layout
    // (calibrated on a different, correctly-classified sheet). This marks fields for
    // just this one sheet, without touching the batch's shared layout. ----
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertCalibrateThisSheet(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (sheet.OriginalPdf == null || sheet.OriginalPdf.Length == 0)
        {
            TempData["Err"] = "The original drawing file isn't available for this sheet.";
            return RedirectToAction("Details", new { id });
        }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(sheet.OriginalPdf);
            ViewBag.PreviewImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render this drawing: " + ex.Message;
            return RedirectToAction("Details", new { id });
        }

        ViewBag.IsConvertThisSheetOnly = true;
        ViewBag.SheetId = id;
        ViewBag.SheetBatchId = sheet.BatchId;   // Cancel goes back to this batch
        ViewBag.FileCount = 1;
        ViewBag.Fields = BatchesController.CalibrationFields;
        return View("~/Views/Batches/Calibrate.cshtml");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertApplyThisSheetOnly(int id, string rectsJson, string? nameField)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (string.IsNullOrWhiteSpace(rectsJson) || rectsJson == "{}")
        {
            TempData["Err"] = "Mark at least one field before continuing, or type the values in by hand instead.";
            return RedirectToAction("ConvertCalibrateThisSheet", new { id });
        }

        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(rectsJson) ?? new();
            rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "Could not read what you marked — please try again."; return RedirectToAction("ConvertCalibrateThisSheet", new { id }); }

        int myUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int uid) ? uid : 0;
        var me = await _db.Users.FindAsync(myUserId);
        var ocrRead = ApplyExtractionToSheet(sheet, rects, nameField, me);
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Converted using this drawing's own field positions — add the Steps for this sheet, then move on." + OcrNote(ocrRead);
        return RedirectToAction("ConvertPreviewDiagram", new { id });
    }

    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertCalibrate(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (sheet.OriginalPdf == null || sheet.OriginalPdf.Length == 0)
        {
            TempData["Err"] = "The original drawing file isn't available for this sheet.";
            return RedirectToAction("FullDrawing", new { id });
        }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(sheet.OriginalPdf);
            ViewBag.PreviewImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render this drawing: " + ex.Message;
            return RedirectToAction("FullDrawing", new { id });
        }

        ViewBag.IsConvert = true;
        ViewBag.SheetId = id;
        ViewBag.SheetBatchId = sheet.BatchId;   // Cancel goes back to this batch
        ViewBag.FileCount = 1;
        ViewBag.Fields = BatchesController.CalibrationFields;
        return View("~/Views/Batches/Calibrate.cshtml");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertApplyCalibration(int id, string rectsJson, string? nameField)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (sheet.OriginalPdf == null || sheet.OriginalPdf.Length == 0)
        {
            TempData["Err"] = "The original drawing file isn't available for this sheet.";
            return RedirectToAction("FullDrawing", new { id });
        }
        var batch = sheet.BatchId.HasValue ? await _db.Batches.FindAsync(sheet.BatchId.Value) : null;
        if (batch == null)
        {
            TempData["Err"] = "This sheet isn't part of a batch, so a layout can't be saved for it.";
            return RedirectToAction("FullDrawing", new { id });
        }

        // Saved on the batch itself — every other sheet from this same delivery reuses
        // it automatically; a different delivery marks its own from scratch.
        batch.ConvertRectsJson = rectsJson ?? "{}";
        batch.ConvertNameField = nameField;
        await _db.SaveChangesAsync();

        Dictionary<string, DrawingRegionExtractor.NormRect> rects;
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(rectsJson ?? "{}") ?? new();
            rects = raw.ToDictionary(kv => kv.Key, kv => new DrawingRegionExtractor.NormRect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
        }
        catch { TempData["Err"] = "Could not read what you marked — please try again."; return RedirectToAction("ConvertCalibrate", new { id }); }

        int myUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int uid) ? uid : 0;
        var me = await _db.Users.FindAsync(myUserId);
        var ocrRead = ApplyExtractionToSheet(sheet, rects, nameField, me);
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Converted — add the Steps for this sheet, then move on to the next drawing." + OcrNote(ocrRead);
        return RedirectToAction("ConvertPreviewDiagram", new { id });
    }

    // ---- Every sheet with both Planning signatures placed, for a quick overview/audit ----
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> SignedSheets()
    {
        var sheets = await _db.ProcessSheets.Where(s => s.ConvertedToProcess)
            .OrderByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.DrawingNo,
                s.PartNo,
                s.Section,
                s.CheckedBy,
                s.CompiledBy,
                BatchName = s.Batch != null ? s.Batch.Name : null,
            })
            .ToListAsync();
        ViewBag.Sheets = sheets.Select(s => new SignedSheetRow
        {
            Id = s.Id,
            DrawingNo = s.DrawingNo,
            PartNo = s.PartNo,
            Section = s.Section,
            CheckedBy = s.CheckedBy,
            CompiledBy = s.CompiledBy,
            BatchName = s.BatchName,
        }).ToList();
        return View();
    }

    public class SignedSheetRow
    {
        public int Id { get; set; }
        public string DrawingNo { get; set; } = "";
        public string PartNo { get; set; } = "";
        public string Section { get; set; } = "";
        public string? CheckedBy { get; set; }
        public string? CompiledBy { get; set; }
        public string? BatchName { get; set; }
    }

    // ---- Set once where the two Planning signatures sit on every converted sheet.
    // Checked-by is always Eng. Eman Mahmoud's signature; Complied-by varies by who
    // converted each sheet, but both always land in the same spot. ----
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> SetSignaturePositions()
    {
        // Eman Mahmoud + her team — who currently has an active signature, and every
        // signature they've saved in their own gallery, so one can be removed or a
        // different one restored without them needing to touch their own Profile.
        var team = await _db.Users.Where(u => u.Department == "Planning" && u.IsActive)
            .OrderBy(u => u.FullName).ToListAsync();
        var teamRows = new List<TeamSignatureRow>();
        foreach (var u in team)
        {
            var gallery = await _db.UserSignatures.Where(s => s.UserId == u.Id).OrderByDescending(s => s.Id).ToListAsync();
            teamRows.Add(new TeamSignatureRow
            {
                UserId = u.Id,
                FullName = u.FullName,
                HasActive = u.SignatureImage != null && u.SignatureImage.Length > 0,
                Gallery = gallery.Select(g => new TeamSignatureRow.GalleryItem { Id = g.Id, Label = g.Label }).ToList(),
            });
        }
        ViewBag.TeamSignatures = teamRows;
        return View();
    }

    public class TeamSignatureRow
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = "";
        public bool HasActive { get; set; }
        public List<GalleryItem> Gallery { get; set; } = new();
        public class GalleryItem { public int Id { get; set; } public string? Label { get; set; } }
    }

    [HttpGet]
    public async Task<IActionResult> TeamSignatureThumb(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user?.SignatureImage == null || user.SignatureImage.Length == 0) return NotFound();
        return File(user.SignatureImage, user.SignatureMime ?? "image/png");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> RemoveActiveSignature(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user != null) { user.SignatureImage = null; user.SignatureMime = null; await _db.SaveChangesAsync(); }
        TempData["Msg"] = "Signature removed — it's still saved in their gallery if you want it back.";
        return RedirectToAction("SetSignaturePositions");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> UseGallerySignature(int userId, int signatureId)
    {
        var user = await _db.Users.FindAsync(userId);
        var sig = await _db.UserSignatures.FirstOrDefaultAsync(s => s.Id == signatureId && s.UserId == userId);
        if (user != null && sig != null) { user.SignatureImage = sig.Image; user.SignatureMime = sig.Mime; await _db.SaveChangesAsync(); }
        TempData["Msg"] = "That signature is now active.";
        return RedirectToAction("SetSignaturePositions");
    }

    // ---- Shown after every conversion, for every converter — the drawing itself, with a
    // chance to re-crop it — as a real box on the drawing, resizable in width and height
    // independently — before moving on to Steps. The starting box comes from the batch's
    // calibration, but different drawings can still need adjusting, so this stays a real
    // per-sheet step, not a one-time setup. ----
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertPreviewDiagram(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (sheet.OriginalPdf == null || sheet.OriginalPdf.Length == 0)
        {
            // No original file to re-crop from — fall back to whatever diagram already exists.
            return RedirectToAction("Edit", new { id });
        }

        try
        {
            var preview = DrawingImageExtractor.RenderPreview(sheet.OriginalPdf);
            ViewBag.DrawingSrc = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not render this drawing: " + ex.Message;
            return RedirectToAction("Edit", new { id });
        }

        // Starting box: this batch's saved "diagram" calibration rect, if there is one —
        // otherwise a sensible centered default the person can drag into place.
        double normX = 0.25, normY = 0.25, normW = 0.5, normH = 0.3;
        var batch = sheet.BatchId.HasValue ? await _db.Batches.FindAsync(sheet.BatchId.Value) : null;
        if (batch != null && !string.IsNullOrWhiteSpace(batch.ConvertRectsJson))
        {
            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(batch.ConvertRectsJson) ?? new();
                if (raw.TryGetValue("diagram", out var d) && d.Length == 4)
                {
                    normX = d[0]; normY = d[1]; normW = d[2]; normH = d[3];
                }
            }
            catch { /* keep the default box */ }
        }

        ViewBag.SheetId = id;
        ViewBag.NormX = normX; ViewBag.NormY = normY; ViewBag.NormW = normW; ViewBag.NormH = normH;
        return View(sheet);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Planner,Admin,Coordinator")]
    public async Task<IActionResult> ConvertPreviewDiagram(int id, double normX, double normY, double normW, double normH)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        if (sheet == null) return NotFound();

        if (sheet.OriginalPdf != null && sheet.OriginalPdf.Length > 0 && normW > 0.01 && normH > 0.01)
        {
            try
            {
                var crop = DrawingImageExtractor.CropNormalized(sheet.OriginalPdf,
                    new DrawingImageExtractor.NormRect(Math.Clamp(normX, 0, 1), Math.Clamp(normY, 0, 1), Math.Clamp(normW, 0.01, 1), Math.Clamp(normH, 0.01, 1)));
                if (crop != null) { sheet.DiagramImage = crop; sheet.DiagramMime = "image/png"; }
            }
            catch { /* keep whatever diagram already existed if re-cropping fails */ }
        }
        sheet.DiagramScalePct = 100;   // the box itself now controls the size — no separate scale needed
        await _db.SaveChangesAsync();
        return RedirectToAction("Edit", new { id });
    }

    // Returns the names of the fields that were read from the page IMAGE by OCR (empty when everything came from
    // the PDF's own text), so the person can be asked to double-check them.
    private static List<string> ApplyExtractionToSheet(ProcessSheet sheet, Dictionary<string, DrawingRegionExtractor.NormRect> rects, string? nameField, AppUser? convertedBy)
    {
        var textRects = rects.Where(r => r.Key != "diagram").ToDictionary(r => r.Key, r => r.Value);
        var vals = DrawingRegionExtractor.ExtractFields(sheet.OriginalPdf!, textRects, out var ocrKeys);
        var ocrRead = new List<string>();
        foreach (var k in ocrKeys)
        {
            var label = k switch
            {
                "drawingNo" => "Drawing No",
                "partNo" => "Part No",
                "section" => "Section",
                "projectName" => "Project",
                "material" => "Material",
                "cutLength" => "Length",
                "qty" => "Qty",
                _ => k,
            };
            if (!string.IsNullOrWhiteSpace(vals.GetValueOrDefault(k, ""))) ocrRead.Add(label);
        }

        if (!string.IsNullOrWhiteSpace(vals.GetValueOrDefault("drawingNo", ""))) sheet.DrawingNo = vals["drawingNo"];
        if (!string.IsNullOrWhiteSpace(vals.GetValueOrDefault("partNo", ""))) sheet.PartNo = vals["partNo"];
        if (!string.IsNullOrWhiteSpace(vals.GetValueOrDefault("section", ""))) sheet.Section = vals["section"];
        if (!string.IsNullOrWhiteSpace(vals.GetValueOrDefault("projectName", ""))) sheet.ProjectName = vals["projectName"];
        var rawMaterial = vals.GetValueOrDefault("material", "");
        if (!string.IsNullOrWhiteSpace(rawMaterial))
            sheet.Material = NormalizeMaterial(rawMaterial) ?? sheet.Material ?? "";
        sheet.CutLength = vals.GetValueOrDefault("cutLength", sheet.CutLength ?? "");
        if (int.TryParse(vals.GetValueOrDefault("qty", ""), out int qty) && qty > 0) sheet.Qty = qty;
        if (sheet.Operations.Count == 0) sheet.Operations.Add(new ProcessOperation { OperationKey = "CUT_DRILL" });

        if (rects.TryGetValue("diagram", out var dr) && (sheet.DiagramImage == null || sheet.DiagramImage.Length == 0))
        {
            try
            {
                var crop = DrawingImageExtractor.CropNormalized(sheet.OriginalPdf!, new DrawingImageExtractor.NormRect(dr.X, dr.Y, dr.W, dr.H));
                if (crop != null) { sheet.DiagramImage = crop; sheet.DiagramMime = "image/png"; }
            }
            catch { /* best effort */ }
        }

        sheet.ConvertedToProcess = true;

        // Checked by is always Eng. Eman Mahmoud for anything converted this way — Complied
        // by is whoever actually did the conversion, so their own signature can be placed.
        sheet.CheckedBy = "Eng. Eman Mahmoud";
        sheet.Approved = "Eng. Eman Mahmoud";
        if (convertedBy != null)
        {
            sheet.CompiledBy = convertedBy.FullName;
            sheet.CompliedByUserId = convertedBy.Id;
            sheet.WrittenBy = convertedBy.FullName;
        }
        return ocrRead;
    }

    private static string OcrNote(List<string> fields) =>
        fields.Count == 0 ? "" : " Read from the drawing image by OCR: " + string.Join(", ", fields) + " — please double-check them.";

    // The Material field is a fixed dropdown (6 known steel grades) — a drawing's title
    // block usually says something like "MATERIAL S355J0 / ST.52", which never matches
    // one of those options exactly. This finds which preset grade the raw text is
    // actually naming, so the dropdown shows it selected instead of blank.
    private static string? NormalizeMaterial(string raw)
    {
        string flat = raw.ToUpperInvariant().Replace(" ", "").Replace(".", "");
        foreach (var option in OperationCatalog.MaterialOptions)
        {
            string flatOption = option.ToUpperInvariant().Replace(" ", "").Replace(".", "");
            // Match on the grade code alone (before the "/"), e.g. "S355J0" — the ST.xx
            // equivalent after the slash is rarely written the same way on a drawing.
            string gradeCode = flatOption.Split('/')[0];
            if (flat.Contains(gradeCode)) return option;
        }
        return null;
    }

    // GET: /ProcessSheets/Delete/5
    public async Task<IActionResult> Delete(int id)
    {
        var sheet = await LoadWithOperationsAsync(id);
        if (sheet == null) return NotFound();
        return View(sheet);
    }

    // POST: /ProcessSheets/Delete/5
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var sheet = await _db.ProcessSheets.FindAsync(id);
        int? batchId = sheet?.BatchId;
        if (sheet != null)
        {
            _db.ProcessSheets.Remove(sheet);
            await _db.SaveChangesAsync();
        }
        // Deleting a sheet from inside a batch lands back on that batch, not on the full list.
        if (batchId.HasValue) return RedirectToAction("Details", "Batches", new { id = batchId.Value });
        return RedirectToAction(nameof(Index));
    }

    // POST: /ProcessSheets/ParseDrawing  -> reads the drawing PDF, returns title-block data
    [HttpPost]
    [IgnoreAntiforgeryToken] // read-only: extracts data from an uploaded PDF, no DB changes
    public IActionResult ParseDrawing(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return Json(new { ok = false, error = "No file uploaded." });

        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Json(new { ok = false, error = "Please upload the drawing as a PDF." });

        try
        {
            using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            var d = DrawingParser.Parse(new MemoryStream(bytes));

            if (!d.Found)
                return Json(new { ok = false, error = "Could not find the title-block table (ITEM / SECTION / MATERIAL) in this PDF." });

            // page preview + suggested crop for the interactive cropper
            string? pageImage = null;
            object? crop = null;
            string? previewError = null;
            try
            {
                var preview = DrawingImageExtractor.RenderPreview(bytes);
                pageImage = "data:image/png;base64," + Convert.ToBase64String(preview.Png);
                crop = new { x = preview.Suggested.X, y = preview.Suggested.Y, w = preview.Suggested.W, h = preview.Suggested.H };
            }
            catch (Exception ex)
            {
                previewError = "Preview render failed: " + ex.Message;
            }

            return Json(new
            {
                ok = true,
                partNo = d.Item,
                drawingNo = d.Item,
                projectName = d.ProjectName,
                section = d.Section,
                qty = d.Qty,
                cutLength = d.Length,
                material = d.MaterialOption,
                description = d.Description,
                pageImage,
                crop,
                previewError
            });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = "Failed to read PDF: " + ex.Message });
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// Stores the diagram image. Preferred path: the browser already cropped the preview
    /// and posted it as a base64 PNG (no server rendering needed). Fallback: crop the
    /// uploaded PDF server-side (to the user's box if given, otherwise auto).
    /// </summary>
    private static async Task AttachDiagramAsync(ProcessSheet sheet, string? diagramData,
        IFormFile? drawingFile, double? cropX, double? cropY, double? cropW, double? cropH)
    {
        byte[]? pdfBytes = null;
        if (drawingFile != null && drawingFile.Length > 0 && drawingFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var ms0 = new MemoryStream();
            await drawingFile.CopyToAsync(ms0);
            pdfBytes = ms0.ToArray();

            // The whole page, tables included — kept separately so a reviewer can open the
            // complete original drawing, not just the small cropped diagram graphic.
            try
            {
                var full = DrawingImageExtractor.RenderPreview(pdfBytes);
                sheet.FullDrawingImage = full.Png;
                sheet.FullDrawingMime = "image/png";
            }
            catch { /* full-page capture is best-effort; the cropped diagram still works without it */ }
        }

        // 1) Browser-cropped image (data URI)
        if (!string.IsNullOrWhiteSpace(diagramData) && diagramData.Contains(','))
        {
            try
            {
                var b64 = diagramData[(diagramData.IndexOf(',') + 1)..];
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length > 0)
                {
                    sheet.DiagramImage = bytes;
                    sheet.DiagramMime = "image/png";
                    return;
                }
            }
            catch (FormatException) { /* fall through to server crop */ }
        }

        // 2) Fallback: server-side crop of the uploaded PDF (reuse the bytes read above)
        if (pdfBytes == null) return;

        byte[]? png;
        if (cropX is { } x && cropY is { } y && cropW is { } w && cropH is { } h && w > 0 && h > 0)
            png = DrawingImageExtractor.CropNormalized(pdfBytes, new DrawingImageExtractor.NormRect(x, y, w, h));
        else
            png = DrawingImageExtractor.CropDiagram(pdfBytes);

        if (png != null && png.Length > 0)
        {
            sheet.DiagramImage = png;
            sheet.DiagramMime = "image/png";
        }
    }

    private async Task<ProcessSheet?> LoadWithOperationsAsync(int id)
    {
        var sheet = await _db.ProcessSheets
            .Include(s => s.Operations)
            .Include(s => s.Reviews)
            .Include(s => s.Batch).ThenInclude(bt => bt!.Placements)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sheet != null)
            sheet.Operations = sheet.Operations.OrderBy(o => o.SortOrder).ToList();

        return sheet;
    }

    private void CleanAndReindexOperations(ProcessSheet sheet)
    {
        sheet.Operations = (sheet.Operations ?? new List<ProcessOperation>())
            .Where(o => !string.IsNullOrWhiteSpace(o.OperationKey))
            .ToList();

        ModelState.Clear();
        TryValidateModel(sheet);

        if (sheet.Operations.Count == 0)
            ModelState.AddModelError(nameof(sheet.Operations),
                "Please add at least one fabrication operation.");

        for (int i = 0; i < sheet.Operations.Count; i++)
            sheet.Operations[i].SortOrder = i;
    }

    private static void EnsureAtLeastOneOperation(ProcessSheet sheet)
    {
        sheet.Operations ??= new List<ProcessOperation>();
        if (sheet.Operations.Count == 0)
            sheet.Operations.Add(new ProcessOperation { OperationKey = "CUT_DRILL" });
    }
}