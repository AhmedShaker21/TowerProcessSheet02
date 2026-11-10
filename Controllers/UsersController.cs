using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Data;
using TowerProcessSheet.Models;
using TowerProcessSheet.Services;

namespace TowerProcessSheet.Controllers;

[Authorize(Roles = "Admin")]
public class UsersController : Controller
{
    private readonly AppDbContext _db;
    public UsersController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
        => View(await _db.Users.OrderBy(u => u.Username).ToListAsync());

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        ViewBag.Departments = await _db.Departments.OrderBy(d => d.Name).Select(d => d.Name).ToListAsync();
        return View(new AppUser());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AppUser model, string password, IFormFile? stamp)
    {
        if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(password))
            ModelState.AddModelError("", "Username and password are required.");
        if (await _db.Users.AnyAsync(u => u.Username == model.Username))
            ModelState.AddModelError("", "That username already exists.");
        if (!ModelState.IsValid) return View(model);

        model.Username = model.Username.Trim();
        model.PasswordHash = PasswordHasher.Hash(password);
        if (stamp != null && stamp.Length > 0)
        {
            using var ms = new MemoryStream();
            await stamp.CopyToAsync(ms);
            model.StampImage = ms.ToArray();
            model.StampMime = string.IsNullOrWhiteSpace(stamp.ContentType) ? "image/png" : stamp.ContentType;
        }
        _db.Users.Add(model);
        await _db.SaveChangesAsync();
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await _db.Users.FindAsync(id);
        ViewBag.Departments = await _db.Departments.OrderBy(d => d.Name).Select(d => d.Name).ToListAsync();
        return user == null ? NotFound() : View(user);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string fullName, string? department,
        SignOffSlot slot, string? stampCode, string? sector, string? docIssue,
        bool isAdmin, bool isPlanner, bool isDisplay, bool isActive,
        bool isCoordinator, bool isSectorManager, bool isAngleWorkshop, bool isPlateWorkshop,
        string? newPassword, IFormFile? stamp, string? username = null)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        // Renaming the login: must not be empty or taken by another account.
        var newName = username?.Trim();
        if (!string.IsNullOrEmpty(newName) && !string.Equals(newName, user.Username, StringComparison.Ordinal))
        {
            bool taken = await _db.Users.AnyAsync(u => u.Id != id && u.Username.ToLower() == newName.ToLower());
            if (taken)
            {
                ModelState.AddModelError("", $"The username \"{newName}\" is already used by another account.");
                ViewBag.Departments = await _db.Departments.OrderBy(d => d.Name).Select(d => d.Name).ToListAsync();
                return View(user);
            }
            user.Username = newName;
        }

        user.FullName = fullName?.Trim() ?? user.FullName;
        user.Department = department?.Trim();
        user.Slot = slot;
        user.StampCode = stampCode?.Trim();
        user.Sector = sector?.Trim();
        user.DocIssue = docIssue?.Trim();
        user.IsPlanner = isPlanner;
        user.IsAdmin = isAdmin;
        user.IsDisplay = isDisplay;
        user.IsActive = isActive;
        user.IsCoordinator = isCoordinator;
        user.IsSectorManager = isSectorManager;
        // Only one account holds each flag — turning it on here turns it off everywhere else.
        if (isAngleWorkshop)
            await _db.Users.Where(u => u.Id != id && u.IsAngleWorkshop).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAngleWorkshop, false));
        if (isPlateWorkshop)
            await _db.Users.Where(u => u.Id != id && u.IsPlateWorkshop).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlateWorkshop, false));
        user.IsAngleWorkshop = isAngleWorkshop;
        user.IsPlateWorkshop = isPlateWorkshop;
        if (!string.IsNullOrWhiteSpace(newPassword))
            user.PasswordHash = PasswordHasher.Hash(newPassword);
        if (stamp != null && stamp.Length > 0)
        {
            using var ms = new MemoryStream();
            await stamp.CopyToAsync(ms);
            user.StampImage = ms.ToArray();
            user.StampMime = string.IsNullOrWhiteSpace(stamp.ContentType) ? "image/png" : stamp.ContentType;
        }
        await _db.SaveChangesAsync();
        return RedirectToAction("Index");
    }
}