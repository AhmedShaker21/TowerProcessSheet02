using TowerProcessSheet.Models;

namespace TowerProcessSheet.Data;

/// <summary>
/// Seeds three example route cards from the original drawings on first run:
/// 109H (cut+drill only), 779 (cut+drill, cutting), 113H (cut+drill, cutting, milling).
/// </summary>
public static class DbSeeder
{
    public static void Seed(AppDbContext db)
    {
        if (db.ProcessSheets.Any())
            return;

        db.ProcessSheets.AddRange(
            new ProcessSheet
            {
                ContractNo = null,
                PartNo = "109H",
                DrawingNo = "109H",
                ProjectName = "S2  220 kv",
                Section = "L50*5",
                Qty = 4,
                CutLength = "1643",
                Material = "S355J0 / ST.52",
                IncludeGalvanizing = true,
                SheetNo = "1/1",
                CompiledBy = "ENG EMAN MOHASSEB",
                CheckedBy = "EMAN MAHMOUD",
                CreatedAt = DateTime.Now.AddMinutes(-3),
                Operations = Ops("CUT_DRILL"),
            },
            new ProcessSheet
            {
                PartNo = "779",
                DrawingNo = "779",
                ProjectName = "S2  220 kv",
                Section = "L55*5",
                Qty = 4,
                CutLength = "5717",
                Material = "S235J0 / ST.37",
                IncludeGalvanizing = true,
                SheetNo = "1/1",
                CompiledBy = "ENG EMAN MOHASSEB",
                CheckedBy = "EMAN MAHMOUD",
                CreatedAt = DateTime.Now.AddMinutes(-2),
                Operations = Ops("CUT_DRILL", "CUTTING"),
            },
            new ProcessSheet
            {
                PartNo = "113H",
                DrawingNo = "113H",
                ProjectName = "S2  220 kv",
                Section = "L60*5",
                Qty = 4,
                CutLength = "980",
                Material = "S355J0 / ST.52",
                IncludeGalvanizing = true,
                SheetNo = "1/1",
                CompiledBy = "ENG EMAN MOHASSEB",
                CheckedBy = "EMAN MAHMOUD",
                CreatedAt = DateTime.Now.AddMinutes(-1),
                Operations = Ops("CUT_DRILL", "CUTTING", "MILLING"),
            }
        );

        db.SaveChanges();
    }

    private static List<ProcessOperation> Ops(params string[] keys)
    {
        var list = new List<ProcessOperation>();
        for (int i = 0; i < keys.Length; i++)
            list.Add(new ProcessOperation { OperationKey = keys[i], SortOrder = i });
        return list;
    }

    /// <summary>
    /// Seeds an admin plus a few demo reviewers on first run, and drops the
    /// example sheets into one demo batch sitting in the operator's inbox.
    /// Default passwords are the username + "123" — change them after logging in.
    /// </summary>
    public static void SeedUsers(AppDbContext db)
    {
        if (db.Users.Any())
        {
            SeedExtras(db);   // idempotent: display user, departments, catalog sync
            return;
        }

        var admin = new AppUser
        {
            Username = "admin",
            FullName = "System Administrator",
            Department = "IT",
            Slot = SignOffSlot.ApprovedBy,
            IsAdmin = true,
            IsPlanner = true,
            PasswordHash = Services.PasswordHasher.Hash("admin123"),
        };
        // Planning team — six named accounts. Eman Mahmoud is fixed as "Checked by" on
        // every sheet she signs, and is the coordinator who splits incoming drawings
        // among the others (e.g. 1000 drawings -> 500 to Hala, 500 to Azza).
        var emanMahmoud = new AppUser
        {
            Username = "eman.mahmoud",
            FullName = "م/ ايمان محمود",
            Department = "Planning",
            Slot = SignOffSlot.CheckedBy,
            IsPlanner = true,
            IsCoordinator = true,
            CheckedByName = "Eman Mahmoud",
            StampCode = "4",
            PasswordHash = Services.PasswordHasher.Hash("eman.mahmoud123"),
        };
        var azza = new AppUser
        {
            Username = "aziza",
            FullName = "م/ عزيزة",
            Department = "Planning",
            Slot = SignOffSlot.CompiledBy,
            IsPlanner = true,
            StampCode = "PLN 2",
            PasswordHash = Services.PasswordHasher.Hash("aziza123"),
        };
        var hala = new AppUser
        {
            Username = "hala",
            FullName = "م/ هالة",
            Department = "Planning",
            Slot = SignOffSlot.CompiledBy,
            IsPlanner = true,
            StampCode = "PLN 3",
            PasswordHash = Services.PasswordHasher.Hash("hala123"),
        };
        var emanMohasseb = new AppUser
        {
            Username = "eman.mohasseb",
            FullName = "م/ ايمان محسب",
            Department = "Planning",
            Slot = SignOffSlot.CompiledBy,
            IsPlanner = true,
            StampCode = "PLN 4",
            PasswordHash = Services.PasswordHasher.Hash("eman.mohasseb123"),
        };
        var atef = new AppUser
        {
            Username = "atef",
            FullName = "أ/ عاطف خليفة",
            Department = "Planning",
            Slot = SignOffSlot.CompiledBy,
            IsPlanner = true,
            StampCode = "PLN 5",
            PasswordHash = Services.PasswordHasher.Hash("atef123"),
        };
        var hassan = new AppUser
        {
            Username = "hassan",
            FullName = "أ/ حسن أحمد",
            Department = "Planning",
            Slot = SignOffSlot.CompiledBy,
            IsPlanner = true,
            StampCode = "PLN 6",
            PasswordHash = Services.PasswordHasher.Hash("hassan123"),
        };
        var sectorManager = new AppUser
        {
            Username = "manager",
            FullName = "Sector Manager",
            Department = "Management",
            Slot = SignOffSlot.ApprovedBy,
            IsSectorManager = true,
            PasswordHash = Services.PasswordHasher.Hash("manager123"),
        };
        var operatorUser = new AppUser
        {
            Username = "operator",
            FullName = "Line Operator",
            Department = "Fabrication",
            Slot = SignOffSlot.Operator,
            StampCode = "OP 12",
            PasswordHash = Services.PasswordHasher.Hash("operator123"),
        };
        var inspector = new AppUser
        {
            Username = "inspector",
            FullName = "Quality Inspector",
            Department = "Quality",
            Slot = SignOffSlot.Inspector,
            StampCode = "HAF 27",
            PasswordHash = Services.PasswordHasher.Hash("inspector123"),
        };
        var quality = new AppUser
        {
            Username = "quality",
            FullName = "Quality Manager",
            Department = "Quality",
            Slot = SignOffSlot.QualityInspector,
            StampCode = "5",
            Sector = "Production Sector",
            DocIssue = "1",
            PasswordHash = Services.PasswordHasher.Hash("quality123"),
        };
        // The rest of the real chain: Research (splits/extracts/stamps drawings before
        // they reach Planning), Programs & Operation (adds the contract/quantity and
        // their own Document-Control stamp), Production Control (dispatches to workers),
        // and more than one named worker — each has their own stamp code, e.g. HAF 59/45/25.
        var research = new AppUser
        {
            Username = "research",
            FullName = "قطاع البحوث",
            IsCoordinator = true,
            Department = "Research",
            Slot = SignOffSlot.ApprovedBy,
            Sector = "R & D Sector",
            DocIssue = "1",
            PasswordHash = Services.PasswordHasher.Hash("research123"),
        };
        var workshop14aa = new AppUser
        {
            Username = "workshop.14aa",
            FullName = "ورشة الاجزاء 14AA",
            Department = "Workshop",
            Slot = SignOffSlot.Operator,
            StampCode = "14AA",
            IsPlateWorkshop = true,
            PasswordHash = Services.PasswordHasher.Hash("workshop123"),
        };
        var workshop107 = new AppUser
        {
            Username = "workshop.107",
            FullName = "ورشة الأبراج 107",
            Department = "Workshop",
            Slot = SignOffSlot.Operator,
            StampCode = "107",
            IsAngleWorkshop = true,
            PasswordHash = Services.PasswordHasher.Hash("workshop107123"),
        };
        var programs = new AppUser
        {
            Username = "programs",
            FullName = "Programs & Operation",
            Department = "Programs & Operation",
            Slot = SignOffSlot.CheckedBy,
            IsPlanner = true,
            Sector = "Production Sector",
            DocIssue = "1",
            PasswordHash = Services.PasswordHasher.Hash("programs123"),
        };
        var prodControl = new AppUser
        {
            Username = "prodcontrol",
            FullName = "Production Control",
            Department = "Production Control",
            Slot = SignOffSlot.ApprovedBy,
            StampCode = "Material Insp.",
            PasswordHash = Services.PasswordHasher.Hash("prodcontrol123"),
        };
        var worker2 = new AppUser
        {
            Username = "worker2",
            FullName = "Line Operator 2",
            Department = "Fabrication",
            Slot = SignOffSlot.Operator,
            StampCode = "HAF 45",
            PasswordHash = Services.PasswordHasher.Hash("worker2123"),
        };
        var worker3 = new AppUser
        {
            Username = "worker3",
            FullName = "Line Operator 3",
            Department = "Fabrication",
            Slot = SignOffSlot.Operator,
            StampCode = "HAF 25",
            PasswordHash = Services.PasswordHasher.Hash("worker3123"),
        };
        db.Users.AddRange(admin, emanMahmoud, azza, hala, emanMohasseb, atef, hassan, sectorManager,
            operatorUser, inspector, quality,
            research, workshop14aa, workshop107, programs, prodControl, worker2, worker3);
        db.SaveChanges();

        // Put the example sheets into one demo batch, in the operator's inbox.
        var sheets = db.ProcessSheets.ToList();
        if (sheets.Count > 0)
        {
            var batch = new Batch
            {
                Name = "BATCH-0001",
                ProjectName = "S2  220 kv",
                CreatedById = admin.Id,
                Status = BatchStatus.InReview,
                CurrentHolderId = operatorUser.Id,
                CreatedAt = DateTime.Now,
            };
            batch.Sheets.AddRange(sheets);
            batch.Transfers.Add(new BatchTransfer
            {
                FromUserId = admin.Id,
                ToUserId = operatorUser.Id,
                Action = "Sent",
                Note = "Demo batch for review",
                At = DateTime.Now,
            });
            db.Batches.Add(batch);
            db.SaveChanges();
        }
        SeedExtras(db);
    }

    /// <summary>Idempotent extras: wall-display user, default departments, custom-step catalog sync.</summary>
    public static void SeedExtras(AppDbContext db)
    {
        if (!db.Users.Any(u => u.IsDisplay))
        {
            db.Users.Add(new AppUser
            {
                Username = "display",
                FullName = "Workshop Screen",
                Department = "Production",
                Slot = SignOffSlot.Operator,
                IsDisplay = true,
                PasswordHash = Services.PasswordHasher.Hash("display123"),
            });
        }
        if (!db.Departments.Any())
        {
            db.Departments.AddRange(
                new Department { Name = "Research" },
                new Department { Name = "Planning" },
                new Department { Name = "Operation" },
                new Department { Name = "Programs & Operation" },
                new Department { Name = "Production Control" },
                new Department { Name = "Fabrication" },
                new Department { Name = "Quality Assurance" },
                new Department { Name = "Quality Control" });
        }
        db.SaveChanges();
        SyncCatalog(db);
    }

    /// <summary>Merge admin-defined steps into the pickable operation catalog (keys CUSTOM_*).</summary>
    public static void SyncCatalog(AppDbContext db)
    {
        var customs = db.CustomSteps.OrderBy(s => s.Id).ToList();
        OperationCatalog.Operations.RemoveAll(o => o.Key.StartsWith("CUSTOM_"));
        foreach (var s in customs)
        {
            OperationCatalog.Operations.Add(new OperationDef
            {
                Key = $"CUSTOM_{s.Id}",
                Name = s.Name,
                CostCenter = s.CostCenter,
                Setting = s.Setting,
                TimeAllowed = s.TimeAllowed,
                Pattern = string.IsNullOrWhiteSpace(s.Description) ? s.Name : s.Description,
                InspectionCostCenter = s.AddInspection ? "5130" : "",
            });
        }
    }
}