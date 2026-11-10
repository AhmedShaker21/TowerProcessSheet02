using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Models;

namespace TowerProcessSheet.Services;

/// <summary>
/// The drawing-field layout (which box goes where) is small, rarely changes, and is
/// painful to re-mark by hand. Schema updates require wiping the SQLite database, which
/// would otherwise erase it every time. This keeps a plain-file backup right next to the
/// database, completely separate from it, so a fresh database can restore it automatically
/// on the very next startup — no re-marking, ever, after the first time.
///
/// Planning ("Default") and Research ("Research") keep independent layouts, since Research
/// only ever marks Part No + Section while Planning's flows need the full field set — each
/// is backed up and restored under its own name so neither can overwrite the other.
/// </summary>
public static class CalibrationBackup
{
    private static string FilePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"saved_calibration_backup_{name.ToLowerInvariant()}.json");

    private class BackupModel
    {
        public string RectsJson { get; set; } = "{}";
        public string? NameField { get; set; }
    }

    public static void Save(SavedCalibration saved)
    {
        try
        {
            var model = new BackupModel { RectsJson = saved.RectsJson, NameField = saved.NameField };
            File.WriteAllText(FilePath(saved.Name), JsonSerializer.Serialize(model));
        }
        catch { /* the backup is best-effort — the database copy still works either way */ }
    }

    /// <summary>If the database has no saved layout under this name but a backup file
    /// exists, restore it. Safe to call on every startup for every known name — does
    /// nothing once a layout already exists under that name.</summary>
    public static async Task RestoreIfMissingAsync(Data.AppDbContext db)
    {
        foreach (var name in new[] { "Default", "Research" })
        {
            try
            {
                if (await db.SavedCalibrations.AnyAsync(c => c.Name == name)) continue;
                if (!File.Exists(FilePath(name))) continue;

                var model = JsonSerializer.Deserialize<BackupModel>(await File.ReadAllTextAsync(FilePath(name)));
                if (model == null || string.IsNullOrWhiteSpace(model.RectsJson) || model.RectsJson == "{}") continue;

                db.SavedCalibrations.Add(new SavedCalibration
                {
                    Name = name, RectsJson = model.RectsJson, NameField = model.NameField, UpdatedAt = DateTime.Now,
                });
                await db.SaveChangesAsync();
            }
            catch { /* best effort — worst case, that one gets marked again this once */ }
        }
    }
}
