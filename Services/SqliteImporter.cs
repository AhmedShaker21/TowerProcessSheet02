using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TowerProcessSheet.Data;

namespace TowerProcessSheet.Services;

/// <summary>
/// One-time copy of every table of an existing SQLite file (route_cards.db) into the configured
/// SQL Server database, keeping every Id as it was so all the links between rows stay valid.
/// Run with:  TowerSystem.exe --import-sqlite "C:\path\route_cards.db"
/// The SQL Server database must still be empty (it is created on the same start, before the seed data).
/// </summary>
public static class SqliteImporter
{
    private const int Chunk = 100;   // rows per round trip — kept small because sheets carry large images/PDFs

    public static async Task RunAsync(AppDbContext target, string sqlitePath)
    {
        if (!File.Exists(sqlitePath))
        {
            Console.WriteLine($"  [import] SQLite file not found: {sqlitePath}");
            return;
        }
        if (await target.Users.AnyAsync() || await target.ProcessSheets.AnyAsync() || await target.Batches.AnyAsync())
        {
            Console.WriteLine("  [import] The SQL Server database already has data — import only runs into an empty database. Nothing was copied.");
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={sqlitePath}").Options;
        using var source = new AppDbContext(options);

        var entityTypes = target.Model.GetEntityTypes()
            .Where(e => e.GetTableName() != null && e.FindPrimaryKey() != null && !e.IsOwned())
            .ToList();

        // IDENTITY_INSERT is per connection, so one connection is held open for the whole copy.
        await target.Database.OpenConnectionAsync();
        try
        {
            // Rows are copied table by table in any order, so foreign keys are switched off meanwhile.
            foreach (var et in entityTypes)
                await target.Database.ExecuteSqlRawAsync("ALTER TABLE [" + et.GetTableName() + "] NOCHECK CONSTRAINT ALL");

            var method = typeof(SqliteImporter).GetMethod(nameof(CopyAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var et in entityTypes)
            {
                var task = (Task<int>)method.MakeGenericMethod(et.ClrType).Invoke(null, new object[] { source, target, et })!;
                int n = await task;
                Console.WriteLine($"  [import] {et.GetTableName()}: {n} row(s).");
            }

            foreach (var et in entityTypes)
                await target.Database.ExecuteSqlRawAsync("ALTER TABLE [" + et.GetTableName() + "] CHECK CONSTRAINT ALL");
        }
        finally
        {
            await target.Database.CloseConnectionAsync();
        }
        Console.WriteLine("  [import] Done. Start the app normally now.");
    }

    private static async Task<int> CopyAsync<T>(AppDbContext src, AppDbContext dst, IEntityType et) where T : class
    {
        var key = et.FindPrimaryKey()!;
        bool keyedByInt = key.Properties.Count == 1 && key.Properties[0].ClrType == typeof(int);
        string keyName = keyedByInt ? key.Properties[0].Name : "";
        bool identity = keyedByInt && key.Properties[0].ValueGenerated == ValueGenerated.OnAdd;
        string table = "[" + (et.GetSchema() ?? "dbo") + "].[" + et.GetTableName() + "]";

        if (identity) await dst.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT " + table + " ON");
        try
        {
            int copied = 0, last = 0;
            var keyProp = keyedByInt ? typeof(T).GetProperty(keyName) : null;
            while (true)
            {
                List<T> rows;
                if (keyedByInt)
                {
                    int lastId = last;
                    string k = keyName;
                    rows = await src.Set<T>().AsNoTracking()
                        .Where(e => EF.Property<int>(e, k) > lastId)
                        .OrderBy(e => EF.Property<int>(e, k))
                        .Take(Chunk).ToListAsync();
                }
                else
                {
                    rows = await src.Set<T>().AsNoTracking().ToListAsync();
                }
                if (rows.Count == 0) break;

                dst.Set<T>().AddRange(rows);
                await dst.SaveChangesAsync();
                dst.ChangeTracker.Clear();
                copied += rows.Count;

                if (!keyedByInt || keyProp == null) break;
                last = (int)keyProp.GetValue(rows[^1])!;
            }
            return copied;
        }
        finally
        {
            if (identity) await dst.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT " + table + " OFF");
        }
    }
}