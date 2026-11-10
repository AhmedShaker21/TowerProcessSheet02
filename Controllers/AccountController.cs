using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;
using TowerProcessSheet.Services;

namespace TowerProcessSheet.Controllers;

[Authorize]
public class AccountController : Controller
{
    private readonly AppDbContext _db;
    public AccountController(AppDbContext db) => _db = db;

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user == null || !PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            ViewBag.Error = "Wrong username or password.";
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new("Username", user.Username),
            new("Slot", user.Slot.ToString()),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        if (user.IsPlanner || user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Planner"));
        if (user.IsSectorManager) claims.Add(new Claim(ClaimTypes.Role, "SectorManager"));
        if (user.IsCoordinator) claims.Add(new Claim(ClaimTypes.Role, "Coordinator"));
        if (user.IsDisplay) claims.Add(new Claim(ClaimTypes.Role, "Display"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        if (user.IsDisplay) return RedirectToAction("Wall", "Batches");
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        // Projects is now everyone's landing page — it shows exactly the batches/projects
        // that concern them, whatever their role (Planning, Quality, a workshop, etc.).
        return RedirectToAction("Projects", "Batches");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }

    // ---- Configure once where Date / Issue / Copy No. sit on the user's own stamp ----
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> StampFields()
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        if (!user.HasStamp) { TempData["Err"] = "Upload your stamp in Profile first."; return RedirectToAction("Profile"); }
        return View(user);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StampFields(string? fieldsJson)
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        user.StampFieldsJson = string.IsNullOrWhiteSpace(fieldsJson) ? null : fieldsJson.Trim();
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Stamp fields saved — they'll auto-fill every time you place this stamp.";
        return RedirectToAction("Profile");
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult SetLang(string lang, string? returnUrl)
    {
        Response.Cookies.Append("lang", lang == "en" ? "en" : "ar",
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
        return Redirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        ViewBag.Signatures = await _db.UserSignatures
            .Where(s => s.UserId == user.Id).OrderBy(s => s.Id).ToListAsync();
        return View(user);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSignature(IFormFile file, string? label)
    {
        if (file == null || file.Length == 0)
        {
            TempData["Err"] = "Choose an image file first.";
            return RedirectToAction("Profile");
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        _db.UserSignatures.Add(new UserSignature
        {
            UserId = User.GetUserId(),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Image = ms.ToArray(),
            Mime = string.IsNullOrWhiteSpace(file.ContentType) ? "image/png" : file.ContentType,
        });
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Signature added.";
        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSignature(int id)
    {
        var sig = await _db.UserSignatures.FirstOrDefaultAsync(s => s.Id == id && s.UserId == User.GetUserId());
        if (sig != null) { _db.UserSignatures.Remove(sig); await _db.SaveChangesAsync(); }
        return RedirectToAction("Profile");
    }

    // Renders one saved signature image.
    [HttpGet]
    public async Task<IActionResult> SignatureImg(int id)
    {
        var sig = await _db.UserSignatures.FindAsync(id);
        if (sig?.Image == null || sig.Image.Length == 0) return NotFound();
        return File(sig.Image, sig.Mime ?? "image/png");
    }

    // Set once where this user's auto-generated Document Control stamp goes — drag it on
    // a sample sheet, save, and every future auto-stamp uses that same spot.
    [HttpGet]
    public async Task<IActionResult> StampPosition()
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        var sample = await _db.ProcessSheets.OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        ViewBag.Sample = sample;
        var png = DocControlStampRenderer.Render(user.Sector ?? "R & D Sector",
            DateTime.Now.ToString("d/ M / yyyy"), user.DocIssue ?? "1", "14AA", user.AutoStampFontScale);
        ViewBag.PreviewSrc = "data:image/png;base64," + Convert.ToBase64String(png);
        return View(user);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StampPosition(double normX, double normY, bool anchorRight, bool anchorBottom, double widthPct, double fontScale = 1.0)
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        user.AutoStampNormX = Math.Clamp(normX, 0, 1);
        user.AutoStampNormY = Math.Clamp(normY, 0, 1);
        user.AutoStampAnchorRight = anchorRight;
        user.AutoStampAnchorBottom = anchorBottom;
        user.AutoStampWidthPct = Math.Clamp(widthPct, 5, 60);
        user.AutoStampFontScale = Math.Clamp(fontScale, 0.4, 2.5);
        await _db.SaveChangesAsync();
        TempData["Msg"] = "Stamp position saved.";
        return RedirectToAction("Profile");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(string fullName, string? department,
        string? stampCode, string? sector, string? docIssue, string? compliedByName, string? checkedByName,
        string? docControlColor, IFormFile? stamp, IFormFile? signature, string? newPassword)
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(fullName)) user.FullName = fullName.Trim();
        user.Department = department?.Trim();
        user.StampCode = stampCode?.Trim();
        user.Sector = sector?.Trim();
        user.DocIssue = docIssue?.Trim();
        user.CompliedByName = compliedByName?.Trim();
        user.CheckedByName = checkedByName?.Trim();
        if (!string.IsNullOrWhiteSpace(docControlColor)) user.DocControlColor = docControlColor.Trim();

        if (stamp != null && stamp.Length > 0)
        {
            using var ms = new MemoryStream();
            await stamp.CopyToAsync(ms);
            user.StampImage = ms.ToArray();
            user.StampMime = string.IsNullOrWhiteSpace(stamp.ContentType) ? "image/png" : stamp.ContentType;
        }

        if (signature != null && signature.Length > 0)
        {
            using var ms2 = new MemoryStream();
            await signature.CopyToAsync(ms2);
            user.SignatureImage = ms2.ToArray();
            user.SignatureMime = string.IsNullOrWhiteSpace(signature.ContentType) ? "image/png" : signature.ContentType;
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
            user.PasswordHash = PasswordHasher.Hash(newPassword);

        await _db.SaveChangesAsync();
        TempData["Msg"] = "Profile saved.";
        return RedirectToAction("Profile");
    }

    // Renders a user's stamp image (used on sheets and in the profile preview).
    [HttpGet]
    public async Task<IActionResult> Stamp(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user?.StampImage == null || user.StampImage.Length == 0) return NotFound();
        return File(user.StampImage, user.StampMime ?? "image/png");
    }
}