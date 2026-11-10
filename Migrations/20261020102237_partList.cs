using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TowerProcessSheet.Migrations
{
    /// <inheritdoc />
    public partial class partList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostCenters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostCenters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Setting = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TimeAllowed = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AddInspection = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomSteps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProgramsFinalizeHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<int>(type: "int", nullable: false),
                    BatchName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContractNotificationNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkOrderNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActiveTo = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgramsFinalizeHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedCalibrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RectsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameField = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedCalibrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SignatureSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CheckedByNormX = table.Column<double>(type: "float", nullable: false),
                    CheckedByNormY = table.Column<double>(type: "float", nullable: false),
                    CheckedByWidthPct = table.Column<double>(type: "float", nullable: false),
                    CheckedByAnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    CheckedByAnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    CompliedByNormX = table.Column<double>(type: "float", nullable: false),
                    CompliedByNormY = table.Column<double>(type: "float", nullable: false),
                    CompliedByWidthPct = table.Column<double>(type: "float", nullable: false),
                    CompliedByAnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    CompliedByAnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedNormX = table.Column<double>(type: "float", nullable: false),
                    ApprovedNormY = table.Column<double>(type: "float", nullable: false),
                    ApprovedWidthPct = table.Column<double>(type: "float", nullable: false),
                    ApprovedAnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedAnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    WrittenByNormX = table.Column<double>(type: "float", nullable: false),
                    WrittenByNormY = table.Column<double>(type: "float", nullable: false),
                    WrittenByWidthPct = table.Column<double>(type: "float", nullable: false),
                    WrittenByAnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    WrittenByAnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Department = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    StampCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Sector = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DocControlColor = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    AutoStampNormX = table.Column<double>(type: "float", nullable: false),
                    AutoStampNormY = table.Column<double>(type: "float", nullable: false),
                    AutoStampAnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    AutoStampAnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    AutoStampWidthPct = table.Column<double>(type: "float", nullable: false),
                    AutoStampHeightPct = table.Column<double>(type: "float", nullable: false),
                    AutoStampFontScale = table.Column<double>(type: "float", nullable: false),
                    CompliedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CheckedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DocIssue = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsAdmin = table.Column<bool>(type: "bit", nullable: false),
                    IsPlanner = table.Column<bool>(type: "bit", nullable: false),
                    IsCoordinator = table.Column<bool>(type: "bit", nullable: false),
                    IsSectorManager = table.Column<bool>(type: "bit", nullable: false),
                    IsAngleWorkshop = table.Column<bool>(type: "bit", nullable: false),
                    IsPlateWorkshop = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenInboxAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDisplay = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StampImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    StampMime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SignatureImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SignatureMime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StampFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractNo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkOrderNo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ProjectName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QtyMultiplier = table.Column<int>(type: "int", nullable: false),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contracts_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSignatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Image = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Mime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSignatures_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Batches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProjectName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RestrictedRecipientId = table.Column<int>(type: "int", nullable: true),
                    ResearchSessionToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResearchIntendedOwnerId = table.Column<int>(type: "int", nullable: true),
                    ResearchSent = table.Column<bool>(type: "bit", nullable: false),
                    ContractId = table.Column<int>(type: "int", nullable: true),
                    ConvertRectsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConvertNameField = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    PartListShared = table.Column<bool>(type: "bit", nullable: false),
                    ContractNo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    WorkOrderNo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    QtyMultiplier = table.Column<int>(type: "int", nullable: false),
                    AssembledOn = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    QualityInsp = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SheetNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    WrittenBy = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    NoOfSheets = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProcessSheetIssue = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DrawingIssue = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DocRef = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CheckedById = table.Column<int>(type: "int", nullable: true),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentHolderId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Batches_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Batches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Batches_Users_CheckedById",
                        column: x => x.CheckedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Batches_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Batches_Users_CurrentHolderId",
                        column: x => x.CurrentHolderId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Batches_Users_ResearchIntendedOwnerId",
                        column: x => x.ResearchIntendedOwnerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Batches_Users_RestrictedRecipientId",
                        column: x => x.RestrictedRecipientId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PartListManualMarks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    MarkedByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    MarkedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartListManualMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartListManualMarks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectPartLists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Pdf = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UploadedByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectPartLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectPartLists_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BatchTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<int>(type: "int", nullable: false),
                    FromUserId = table.Column<int>(type: "int", nullable: true),
                    ToUserId = table.Column<int>(type: "int", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BatchTransfers_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BatchTransfers_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BatchTransfers_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessSheets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<int>(type: "int", nullable: true),
                    ContractNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PartNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DrawingNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ProjectName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WorkOrderNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ContractNotificationNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProgramsFinalizedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResearchGroupKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsUnclassified = table.Column<bool>(type: "bit", nullable: false),
                    IsScannedImage = table.Column<bool>(type: "bit", nullable: false),
                    ProgramsIssue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProgramsDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentToQualityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResentForReviewAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Section = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Qty = table.Column<int>(type: "int", nullable: false),
                    CutLength = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Material = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IncludeGalvanizing = table.Column<bool>(type: "bit", nullable: false),
                    TotalQty = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AssembledOn = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    QualityInsp = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CompiledBy = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CheckedBy = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SheetNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SheetDate = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    NoOfSheets = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WrittenBy = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Approved = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ProcessSheetIssue = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DrawingIssue = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DocRef = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DiagramImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    DiagramMime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DiagramScalePct = table.Column<int>(type: "int", nullable: false),
                    FullDrawingImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    FullDrawingMime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StampedDrawingImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    OriginalPdf = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ConvertedToProcess = table.Column<bool>(type: "bit", nullable: false),
                    SentToQualityUserId = table.Column<int>(type: "int", nullable: true),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    CompliedByUserId = table.Column<int>(type: "int", nullable: true),
                    StampNormX = table.Column<double>(type: "float", nullable: false),
                    StampNormY = table.Column<double>(type: "float", nullable: false),
                    StampAnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    StampAnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    StampWidthPct = table.Column<double>(type: "float", nullable: false),
                    StampHeightPct = table.Column<double>(type: "float", nullable: false),
                    StampFontScale = table.Column<double>(type: "float", nullable: false),
                    StampSector = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StampDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StampIssue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StampCopyNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomMaterialCostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomIncomingInspCostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomFinalInspCostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomFinalStoreCostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessSheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessSheets_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StampPlacements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    NormX = table.Column<double>(type: "float", nullable: false),
                    NormY = table.Column<double>(type: "float", nullable: false),
                    AnchorRight = table.Column<bool>(type: "bit", nullable: false),
                    AnchorBottom = table.Column<bool>(type: "bit", nullable: false),
                    WidthPct = table.Column<double>(type: "float", nullable: false),
                    AnchorText = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OffsetXPx = table.Column<double>(type: "float", nullable: false),
                    OffsetYPx = table.Column<double>(type: "float", nullable: false),
                    ProcessSheetId = table.Column<int>(type: "int", nullable: true),
                    Image = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Mime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StampPlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StampPlacements_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StampPlacements_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CustomTimeAllowed = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomCostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomSetting = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomDescription = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CustomInspCostCenter = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProcessSheetId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessOperations_ProcessSheets_ProcessSheetId",
                        column: x => x.ProcessSheetId,
                        principalTable: "ProcessSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SheetReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProcessSheetId = table.Column<int>(type: "int", nullable: false),
                    ReviewerId = table.Column<int>(type: "int", nullable: false),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StampImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    StampMime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DocControlColor = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: true),
                    AnnotationImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    AnnotationMime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewerName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ReviewerCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DocSector = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DocIssue = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SheetReviews_ProcessSheets_ProcessSheetId",
                        column: x => x.ProcessSheetId,
                        principalTable: "ProcessSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SheetReviews_Users_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_CheckedById",
                table: "Batches",
                column: "CheckedById");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_ContractId",
                table: "Batches",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_CreatedById",
                table: "Batches",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_CurrentHolderId",
                table: "Batches",
                column: "CurrentHolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_ProjectId",
                table: "Batches",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_ResearchIntendedOwnerId",
                table: "Batches",
                column: "ResearchIntendedOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_RestrictedRecipientId",
                table: "Batches",
                column: "RestrictedRecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_BatchTransfers_BatchId",
                table: "BatchTransfers",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BatchTransfers_FromUserId",
                table: "BatchTransfers",
                column: "FromUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BatchTransfers_ToUserId",
                table: "BatchTransfers",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_CreatedById",
                table: "Contracts",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_PartListManualMarks_ProjectId_RowKey",
                table: "PartListManualMarks",
                columns: new[] { "ProjectId", "RowKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessOperations_ProcessSheetId",
                table: "ProcessOperations",
                column: "ProcessSheetId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessSheets_BatchId",
                table: "ProcessSheets",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectPartLists_ProjectId",
                table: "ProjectPartLists",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedById",
                table: "Projects",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SheetReviews_ProcessSheetId",
                table: "SheetReviews",
                column: "ProcessSheetId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetReviews_ReviewerId",
                table: "SheetReviews",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_StampPlacements_BatchId",
                table: "StampPlacements",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_StampPlacements_UserId",
                table: "StampPlacements",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSignatures_UserId",
                table: "UserSignatures",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchTransfers");

            migrationBuilder.DropTable(
                name: "CostCenters");

            migrationBuilder.DropTable(
                name: "CustomSteps");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "PartListManualMarks");

            migrationBuilder.DropTable(
                name: "ProcessOperations");

            migrationBuilder.DropTable(
                name: "ProgramsFinalizeHistories");

            migrationBuilder.DropTable(
                name: "ProjectPartLists");

            migrationBuilder.DropTable(
                name: "SavedCalibrations");

            migrationBuilder.DropTable(
                name: "SheetReviews");

            migrationBuilder.DropTable(
                name: "SignatureSettings");

            migrationBuilder.DropTable(
                name: "StampPlacements");

            migrationBuilder.DropTable(
                name: "UserSignatures");

            migrationBuilder.DropTable(
                name: "ProcessSheets");

            migrationBuilder.DropTable(
                name: "Batches");

            migrationBuilder.DropTable(
                name: "Contracts");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
