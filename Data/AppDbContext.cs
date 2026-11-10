using Microsoft.EntityFrameworkCore;
using TowerProcessSheet.Models;

namespace TowerProcessSheet.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProcessSheet> ProcessSheets => Set<ProcessSheet>();
    public DbSet<ProcessOperation> ProcessOperations => Set<ProcessOperation>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<BatchTransfer> BatchTransfers => Set<BatchTransfer>();
    public DbSet<SheetReview> SheetReviews => Set<SheetReview>();
    public DbSet<StampPlacement> StampPlacements => Set<StampPlacement>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<SavedCalibration> SavedCalibrations => Set<SavedCalibration>();
    public DbSet<SignatureSettings> SignatureSettings => Set<SignatureSettings>();
    public DbSet<TowerProcessSheet.Models.Project> Projects => Set<TowerProcessSheet.Models.Project>();
    public DbSet<ProgramsFinalizeHistory> ProgramsFinalizeHistories => Set<ProgramsFinalizeHistory>();
    public DbSet<ProjectPartList> ProjectPartLists => Set<ProjectPartList>();
    public DbSet<PartListManualMark> PartListManualMarks => Set<PartListManualMark>();
    public DbSet<CostCenterEntry> CostCenters => Set<CostCenterEntry>();
    public DbSet<CustomStep> CustomSteps => Set<CustomStep>();
    public DbSet<UserSignature> UserSignatures => Set<UserSignature>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProcessSheet>()
            .HasMany(s => s.Operations)
            .WithOne(o => o.ProcessSheet!)
            .HasForeignKey(o => o.ProcessSheetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<ProcessSheet>()
            .HasOne(s => s.Batch)
            .WithMany(x => x.Sheets)
            .HasForeignKey(s => s.BatchId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<SheetReview>()
            .HasOne(r => r.ProcessSheet)
            .WithMany(s => s.Reviews)
            .HasForeignKey(r => r.ProcessSheetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<SheetReview>()
            .HasOne(r => r.Reviewer)
            .WithMany()
            .HasForeignKey(r => r.ReviewerId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Batch>()
            .HasOne(x => x.CreatedBy)
            .WithMany()
            .HasForeignKey(x => x.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Batch>()
            .HasOne(x => x.CurrentHolder)
            .WithMany()
            .HasForeignKey(x => x.CurrentHolderId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Batch>()
            .HasOne(x => x.CheckedBy)
            .WithMany()
            .HasForeignKey(x => x.CheckedById)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Batch>()
            .HasOne(x => x.RestrictedRecipient)
            .WithMany()
            .HasForeignKey(x => x.RestrictedRecipientId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<BatchTransfer>()
            .HasOne(t => t.Batch)
            .WithMany(x => x.Transfers)
            .HasForeignKey(t => t.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<BatchTransfer>()
            .HasOne(t => t.FromUser).WithMany()
            .HasForeignKey(t => t.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<BatchTransfer>()
            .HasOne(t => t.ToUser).WithMany()
            .HasForeignKey(t => t.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();

        b.Entity<Batch>()
            .HasOne(x => x.Contract)
            .WithMany(c => c.Batches)
            .HasForeignKey(x => x.ContractId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<StampPlacement>()
            .HasOne(p => p.Batch).WithMany(x => x.Placements)
            .HasForeignKey(p => p.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<StampPlacement>()
            .HasOne(p => p.User).WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<UserSignature>()
            .HasOne(s => s.User).WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One Part List per project; deleting a project takes its Part List with it.
        b.Entity<ProjectPartList>()
            .HasOne(x => x.Project).WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectPartList>().HasIndex(x => x.ProjectId).IsUnique();

        // Hand-ticked Part List rows; one tick per row, gone with the project.
        b.Entity<PartListManualMark>()
            .HasOne(x => x.Project).WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<PartListManualMark>().HasIndex(x => new { x.ProjectId, x.RowKey }).IsUnique();

        b.Entity<Contract>()
            .HasOne(c => c.CreatedBy)
            .WithMany()
            .HasForeignKey(c => c.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
