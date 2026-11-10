using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using TowerProcessSheet.Data;

// Startup log next to the exe: shows whether the process started and, if it dies at startup
// (e.g. SQL Server refusing the service account), the exact exception. Useful because a Windows
// Service has no console to show errors in.
static void StartupLog(string text)
{
    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine); }
    catch { /* logging must never break startup */ }
}
AppDomain.CurrentDomain.UnhandledException += (_, e) => StartupLog("CRASH: " + e.ExceptionObject);
StartupLog("Starting. args=[" + string.Join(" ", args) + "]  user=" + Environment.UserName
           + "  asService=" + Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());

var builder = WebApplication.CreateBuilder(args);

// Lets the exe run as a Windows Service (starts with Windows, keeps running with no console window
// and no user logged in). When started by hand (double-click / dotnet run) this does nothing.
builder.Host.UseWindowsService(o => o.ServiceName = "TowerSystem");

// Always read appsettings.json from the folder the exe sits in — not from whatever folder the app
// happened to be started from (a startup task or shortcut can start it somewhere else, which would
// silently ignore the SQL Server settings and fall back to SQLite).
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);

// Bind to all network interfaces (not just this machine) so other PCs on the LAN can
// reach it — e.g. http://192.168.1.50:5080 from any other computer on the same network.
// Still reachable as http://localhost:5080 from this machine too.
var appUrl = builder.Configuration["AppUrl"] ?? "http://0.0.0.0:5080";   // appsettings.json can override
builder.WebHost.UseUrls(appUrl);

builder.Services.AddControllersWithViews();

// Keep login sessions valid across app restarts (including a crash-and-relaunch, e.g. from
// a bad PDF during a bulk import). Without this, every restart silently generates new
// encryption keys, which immediately invalidates every existing browser's login cookie —
// that would look exactly like being randomly logged out mid-task.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "keys")))
    .SetApplicationName("TowerProcessSheet");

// Cookie-based login. Unauthenticated users are sent to /Account/Login.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// Allow the (base64) cropped diagram + PDF upload to post comfortably.
builder.Services.Configure<FormOptions>(o =>
{
    o.ValueLengthLimit = 256 * 1024 * 1024;         // 256 MB per form field
    o.MultipartBodyLengthLimit = 256 * 1024 * 1024; // 256 MB total
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024 * 1024);

// Keep the SQLite file next to the executable, wherever it is launched from.
// Database: SQLite by default (zero-install, single file); switch to SQL Server in appsettings.json
// ("Database:Provider": "SqlServer" + ConnectionStrings:SqlServer). The database is created automatically.
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var cs = builder.Configuration.GetConnectionString("SqlServer")
             ?? "Server=.;Database=ProcessSheetSystem;Trusted_Connection=True;TrustServerCertificate=True";
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(cs));
}
else
{
    var dbPath = Path.Combine(AppContext.BaseDirectory, "route_cards.db");
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}").AddInterceptors(new SqlitePragmaInterceptor()));
}
//  To use SQL Server instead, swap the line above for:
//  options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Serve css/js from resources embedded in the .exe, so it runs with no wwwroot
// folder. A physical wwwroot (if present, e.g. during development) still wins,
// which keeps live-editing of styles working.
var embeddedWwwroot = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.Environment.WebRootFileProvider = new CompositeFileProvider(
    app.Environment.WebRootFileProvider, embeddedWwwroot);

// Apply any pending migrations (creates the database on first run too) and seed the
// examples. Using Migrate() instead of EnsureCreated() is what lets future schema
// changes apply on top of existing data — no more wiping the database each time.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // The Migrations folder was generated while this project targeted SQL Server, so
    // it has SQL-Server-specific column types (e.g. nvarchar(max)) baked directly into
    // the generated C# — SQLite's parser rejects that syntax outright. EnsureCreated
    // builds the schema straight from the current model using SQLite's own type
    // mapping instead of replaying that incompatible migration history. This does mean
    // a SQLite database won't pick up future schema changes automatically the way
    // Migrate() does for SQL Server — if a later update adds new columns, delete the
    // route_cards.db file next to the exe and let it recreate fresh.
    if (db.Database.IsSqlite())
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // No Migrations folder ships with this project (and one generated earlier would be out of
        // date), so Migrate() would build nothing. EnsureCreated builds the whole schema from the
        // current model the first time — creating the database too if the login is allowed to —
        // and PatchSqlServerSchema then adds any table or column added to the model afterward,
        // keeping existing data, exactly like the SQLite patch below does.
        db.Database.EnsureCreated();
        PatchSqlServerSchema(db);
    }
    // Belt-and-suspenders for SQLite: EnsureCreated only builds tables the very first
    // time the file is created — a column added to the model afterward (as this project
    // keeps growing) would otherwise need the whole file deleted and recreated, losing
    // all its data, every single time. This compares each table's actual columns
    // against what the current model expects and adds whatever's missing, automatically,
    // on every startup — so schema growth just works from here on, the same way
    // Migrate() already does for SQL Server.
    if (db.Database.IsSqlite())
    {
        CreateMissingSqliteTables(db);   // brand-new tables first, then columns on existing ones
        PatchSqliteSchema(db);
        // journal_mode is a persistent property of the database file itself (unlike
        // synchronous/busy_timeout, which are per-connection and handled by
        // SqlitePragmaInterceptor instead, registered below) — WAL lets reads and
        // writes proceed concurrently instead of blocking each other, which was
        // causing even plain primary-key lookups to take tens of seconds while a large
        // send was in progress.
        using var pragmaCmd = db.Database.GetDbConnection().CreateCommand();
        if (pragmaCmd.Connection!.State != System.Data.ConnectionState.Open) pragmaCmd.Connection.Open();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
        pragmaCmd.ExecuteNonQuery();
    }
    // One-time move of an existing SQLite database into SQL Server:
    //   TowerSystem.exe --import-sqlite "C:\\path\\route_cards.db"
    // Runs on the still-empty SQL Server database (before the seeders add their defaults), then exits.
    var importAt = Array.IndexOf(args, "--import-sqlite");
    if (importAt >= 0)
    {
        if (importAt + 1 >= args.Length) { Console.WriteLine("  Usage: --import-sqlite \"<path to route_cards.db>\""); return; }
        if (db.Database.IsSqlite()) { Console.WriteLine("  Set \"Database:Provider\": \"SqlServer\" in appsettings.json first — the import copies INTO SQL Server."); return; }
        await TowerProcessSheet.Services.SqliteImporter.RunAsync(db, args[importAt + 1]);
        return;
    }
    DbSeeder.Seed(db);
    DbSeeder.SeedUsers(db);
    await TowerProcessSheet.Services.CalibrationBackup.RestoreIfMissingAsync(db);
    await LoadBuiltInSignaturesAsync(db);
    await LoadBuiltInStampsAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Batches}/{action=Index}/{id?}");

// Open the default browser at the app once the server is ready — using localhost
// specifically, since "0.0.0.0" (the bind address) isn't something a browser can open.
const string BrowseUrl = "http://localhost:5080";
if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(BrowseUrl));

Console.WriteLine();
Console.WriteLine("  Tower System is running at " + BrowseUrl);
try
{
    foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        Console.WriteLine("  On this network:  http://" + ip + ":5080");
}
catch { /* the banner is a convenience only */ }
StartupLog("Ready - listening on " + appUrl);
Console.WriteLine("  Keep this window open. Close it (or press Ctrl+C) to stop.");
Console.WriteLine();

app.Run();

// EnsureCreated only builds tables the very first time the file is created, and
// PatchSqliteSchema only adds columns to tables that already exist — so a table added to
// the model later (e.g. ProjectPartLists) would never appear in an existing database and
// every query against it would fail with "no such table". This builds just the tables that
// are missing, straight from the model's own create script, and leaves everything that
// already exists (and all its data) completely alone.
static void CreateMissingSqliteTables(AppDbContext db)
{
    try
    {
        var conn = db.Database.GetDbConnection();
        bool wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var script = db.Database.GenerateCreateScript();
            foreach (var part in System.Text.RegularExpressions.Regex.Split(script, @";[ \t]*\r?\n"))
            {
                var stmt = part.Trim().TrimEnd(';').Trim();
                if (stmt.Length == 0) continue;

                var table = System.Text.RegularExpressions.Regex.Match(stmt, "^CREATE TABLE \"([^\"]+)\"");
                var index = System.Text.RegularExpressions.Regex.Match(stmt, "^CREATE (?:UNIQUE )?INDEX \"[^\"]+\" ON \"([^\"]+)\"");
                string? tableName = table.Success ? table.Groups[1].Value : index.Success ? index.Groups[1].Value : null;
                if (tableName == null) continue;
                // A table's own indexes are only created together with the table itself.
                if (table.Success ? existing.Contains(tableName) : !created.Contains(tableName)) continue;

                try
                {
                    using var run = conn.CreateCommand();
                    run.CommandText = stmt;
                    run.ExecuteNonQuery();
                    if (table.Success) { created.Add(tableName); Console.WriteLine($"  [schema-patch] Created missing table \"{tableName}\"."); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [schema-patch] Could not run for \"{tableName}\": {ex.Message}");
                }
            }
        }
        finally { if (wasClosed) conn.Close(); }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [schema-patch] Missing-table check failed: {ex.Message}");
    }
}

// Adds any column the current model expects but an existing SQLite file doesn't have
// yet — the SQLite equivalent of what Migrate() does for SQL Server, since SQLite has
// no real migration history to replay against here (see the note where this is called).
static void PatchSqliteSchema(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    bool wasClosed = conn.State != System.Data.ConnectionState.Open;
    if (wasClosed) conn.Open();
    try
    {
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName == null) continue;

            var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
                using var reader = pragmaCmd.ExecuteReader();
                while (reader.Read()) actualColumns.Add(reader.GetString(1));   // column name
            }
            if (actualColumns.Count == 0) continue;   // table doesn't exist at all yet — nothing to patch

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (actualColumns.Contains(columnName)) continue;

                var columnType = property.GetColumnType();
                string defaultClause = "";
                if (!property.IsColumnNullable())
                {
                    // SQLite requires a default when adding a NOT NULL column to a table
                    // that may already have rows — picking one per CLR type so this
                    // doesn't need updating for every new field added later.
                    var t = property.ClrType;
                    defaultClause =
                        t == typeof(bool) ? " DEFAULT 0" :
                        t == typeof(int) || t == typeof(long) || t == typeof(short) ? " DEFAULT 0" :
                        t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? " DEFAULT 0" :
                        t == typeof(string) ? " DEFAULT ''" :
                        t == typeof(DateTime) ? " DEFAULT '0001-01-01 00:00:00'" : "";
                }

                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType}{defaultClause}";
                    alterCmd.ExecuteNonQuery();
                    Console.WriteLine($"  [schema-patch] Added missing column \"{tableName}\".\"{columnName}\" ({columnType}).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [schema-patch] Could not add \"{tableName}\".\"{columnName}\": {ex.Message}");
                }
            }
        }
    }
    finally { if (wasClosed) conn.Close(); }
}

// SQL Server counterpart of CreateMissingSqliteTables + PatchSqliteSchema: builds any table the
// model has but the database doesn't, then adds any column an existing table is missing. Existing
// tables and all their data are left alone, so the app can keep growing without a migration step.
static void PatchSqlServerSchema(AppDbContext db)
{
    try
    {
        var conn = db.Database.GetDbConnection();
        bool wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var t = reader.GetString(0);
                    if (!columns.TryGetValue(t, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        columns[t] = set;
                    }
                    set.Add(reader.GetString(1));
                }
            }

            // 1) Tables that don't exist yet — from the model's own create script (batches split on GO).
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var script = db.Database.GenerateCreateScript();
            foreach (var part in System.Text.RegularExpressions.Regex.Split(script, @"^[ \t]*GO[ \t]*\r?$", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                var stmt = part.Trim();
                if (stmt.Length == 0) continue;

                var table = System.Text.RegularExpressions.Regex.Match(stmt, @"^CREATE TABLE (?:\[[^\]]+\]\.)?\[([^\]]+)\]");
                var index = System.Text.RegularExpressions.Regex.Match(stmt, @"^CREATE (?:UNIQUE )?(?:NONCLUSTERED )?INDEX \[[^\]]+\] ON (?:\[[^\]]+\]\.)?\[([^\]]+)\]");
                string? tableName = table.Success ? table.Groups[1].Value : index.Success ? index.Groups[1].Value : null;
                if (tableName == null) continue;
                // A table's own indexes are only created together with the table itself.
                if (table.Success ? columns.ContainsKey(tableName) : !created.Contains(tableName)) continue;

                try
                {
                    using var run = conn.CreateCommand();
                    run.CommandText = stmt;
                    run.ExecuteNonQuery();
                    if (table.Success) { created.Add(tableName); Console.WriteLine($"  [schema-patch] Created missing table [{tableName}]."); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [schema-patch] Could not run for [{tableName}]: {ex.Message}");
                }
            }

            // 2) Columns an existing table is missing.
            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (tableName == null || !columns.TryGetValue(tableName, out var actual)) continue;

                foreach (var property in entityType.GetProperties())
                {
                    var columnName = property.GetColumnName();
                    if (actual.Contains(columnName)) continue;

                    var columnType = property.GetColumnType();
                    string clause = " NULL";
                    if (!property.IsColumnNullable())
                    {
                        // A NOT NULL column added to a table that already has rows needs a default.
                        var t = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                        string def =
                            t == typeof(bool) || t.IsEnum || t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long)
                                || t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? "0" :
                            t == typeof(string) ? "''" :
                            t == typeof(DateTime) ? "'0001-01-01T00:00:00'" :
                            t == typeof(Guid) ? "'00000000-0000-0000-0000-000000000000'" :
                            t == typeof(byte[]) ? "0x" : "";
                        clause = def.Length > 0 ? " NOT NULL DEFAULT " + def : " NULL";
                    }

                    try
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE [{tableName}] ADD [{columnName}] {columnType}{clause}";
                        alter.ExecuteNonQuery();
                        Console.WriteLine($"  [schema-patch] Added missing column [{tableName}].[{columnName}] ({columnType}).");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [schema-patch] Could not add [{tableName}].[{columnName}]: {ex.Message}");
                    }
                }
            }
        }
        finally { if (wasClosed) conn.Close(); }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [schema-patch] SQL Server schema check failed: {ex.Message}");
    }
}

// Built-in signatures, no upload needed: drop a PNG/JPG at
// <folder next to the exe>/builtin-signatures/<username>.png (or .jpg) and it's loaded
// into that account's signature on every startup — replace the file and restart to
// update it. Doesn't touch anyone who has no matching file, or accounts not listed here.
static async Task LoadBuiltInSignaturesAsync(AppDbContext db)
{
    var dir = Path.Combine(AppContext.BaseDirectory, "builtin-signatures");
    Directory.CreateDirectory(dir);
    string[] usernames = { "eman.mahmoud", "eman.mohasseb" };
    foreach (var username in usernames)
    {
        string? found = new[] { ".png", ".jpg", ".jpeg" }
            .Select(ext => Path.Combine(dir, username + ext))
            .FirstOrDefault(File.Exists);
        if (found == null) continue;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) continue;
        try
        {
            user.SignatureImage = await File.ReadAllBytesAsync(found);
            user.SignatureMime = found.EndsWith(".png") ? "image/png" : "image/jpeg";
        }
        catch { /* best effort — a bad/locked file just leaves the existing signature alone */ }
    }
    await db.SaveChangesAsync();
}

// Same idea as signatures, but for the actual review stamp images (Quality, Programs &
// Operation) — drop <same folder as the exe>/builtin-stamps/<username>.png (or .jpg) and
// it's loaded as that account's stamp on every startup.
static async Task LoadBuiltInStampsAsync(AppDbContext db)
{
    var dir = Path.Combine(AppContext.BaseDirectory, "builtin-stamps");
    Directory.CreateDirectory(dir);
    string[] usernames = { "quality", "programs" };
    foreach (var username in usernames)
    {
        string? found = new[] { ".png", ".jpg", ".jpeg" }
            .Select(ext => Path.Combine(dir, username + ext))
            .FirstOrDefault(File.Exists);
        if (found == null)
        {
            Console.WriteLine($"  [builtin-stamps] No file found for \"{username}\" in {dir} — looked for {username}.png/.jpg/.jpeg.");
            continue;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            Console.WriteLine($"  [builtin-stamps] Found {found}, but no user with username \"{username}\" exists in the database — skipped.");
            continue;
        }
        try
        {
            user.StampImage = await File.ReadAllBytesAsync(found);
            user.StampMime = found.EndsWith(".png") ? "image/png" : "image/jpeg";
            Console.WriteLine($"  [builtin-stamps] Loaded {found} ({user.StampImage.Length} bytes) onto user \"{username}\".");
        }
        catch (Exception ex) { Console.WriteLine($"  [builtin-stamps] Found {found} but couldn't read it: {ex.Message}"); }
    }
    await db.SaveChangesAsync();
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
    }
    catch
    {
        // If the browser can't be opened automatically, the URL is printed above.
    }
}

// Applied to every connection EF Core opens (not just a one-off startup connection,
// since synchronous/busy_timeout don't persist in the database file the way
// journal_mode does). NORMAL synchronous mode skips most of the redundant fsyncs that
// were making every single write painfully slow for this app's workload (large image
// blobs written one row at a time) — an OS crash right at the moment of write could
// lose the last commit, but won't corrupt the database, which is the standard,
// widely-recommended trade-off for this kind of app. busy_timeout makes a connection
// wait and retry for a few seconds on a lock conflict instead of failing immediately.
// This class declaration must stay at the very end of the file — top-level statements
// (everything above, including app.Run()) are only legal before any type declaration.
class SqlitePragmaInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    public override void ConnectionOpened(System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;";
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}