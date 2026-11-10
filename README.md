# Process Sheet System

Generates steel-fabrication **route cards / process sheets** for transmission-tower
parts from the drawing's title-block data. The user enters a handful of values and
picks the fabrication operations; the full sheet (material, inspections,
galvanizing, final steps, cost centers and Op. Nos) is generated automatically.

---

## How it works

**Step 1 - Upload the drawing (optional, recommended)**
Upload the engineering-drawing PDF. The system reads the title-block table with
PdfPig and auto-fills Part/Drawing No., Project, Section, Qty, Cut length and
Material. It finds the correct row by locating the line that has BOTH a section
token (e.g. `L45*5`) AND a material token (e.g. `S235JR`), so the part label
`146 - L45*5` is ignored. (Vector PDFs with embedded text only - scanned images
would need OCR, which is not included.)

**Step 2 - Inputs (from the drawing title block)**
- Part No. / Drawing No., Project Name, Section (e.g. `L50*5`), Qty
- **Cut length** (e.g. `1643`) -> becomes "Cut the part with length = 1643 mm"
- **Material** dropdown (`S355JR / ST.52`, `S235JR / ST.37`, ...). Pre-selected from
  the Drawing No. (contains **H** => S355/ST.52, else S235/ST.37); user can change it.
- **Operations** in order: `Cutting & Drilling & No CNC`, `Cutting`, `Milling`,
  `Hot Deep Galvanized` (add / remove / reorder). Each carries its own cost center
  and the inspection after it (galvanizing -> 5140, others -> 5130).
- Optional: Auto-add galvanizing at the end if you did not place it yourself
  (default on), Contract/Work Order No., Total Qty, etc.

**Review & resize the crop, then Save**
After reading the drawing, the full page renders with a suggested crop box over the
diagram. Drag the box to move it or drag a corner to resize (or "Reset to suggested").
The chosen region (stored as normalized 0..1 coordinates) is rendered at high
resolution and cropped on save, then embedded at the bottom of the sheet — exactly
like the paper route cards. If you skip the review, the suggested box is used.

**Generated automatically (fixed rules)**
- `MATERIAL <grade> / <ST>` + `Angle <expanded section>  L= 12000mm`
  (bar length fixed at 12000mm; `L50*5` => `50*50*5`).
- Incoming inspection `5110 / 010` with the fixed sentence
  "Check the incoming Material and Dimension".
- An `INSP.` row after every operation (cost center `5130`).
- Each operation's fixed text + routing (Cutting & Drilling -> 4730, Milling -> 4212).
- `Hot Deep Galvanized` 4620 + "Contribution up to 85 Micron" (85 fixed),
  then post-galvanizing inspection `5140`.
- `F. Insp.` 5150 and `F. Store` 4411.
- Op. No auto-increments (010, 020, 030, ...) across the inspected operations.

A live preview on the form shows the generated card before saving; the Details page
renders it in the exact paper-form layout with a Print / Save as PDF button.

Three example sheets (109H, 779, 113H with milling) are seeded on first run.

---

## Run it

```bash
cd TowerProcessSheet
dotnet run
```

Open the URL shown (e.g. https://localhost:7180). In Visual Studio open the .csproj
and press F5. A SQLite file `route_cards.db` is created automatically.

> If you ran an earlier version, delete the old *.db file first - the schema changed.

---

## Where the rules live (one place)

`Models/OperationCatalog.cs`:
- `Operations` - pickable fabrication operations (text, cost center, setting, time)
- `MaterialOptions` + `DefaultMaterial` (the H rule)
- `FixedBarLength` (12000), `GalvanizeMicron` (85)
- `ExpandSection` (L50*5 -> 50*50*5)
- `Generate(sheet)` - the single source of truth that builds the whole card
  (mirrored in wwwroot/js/operations-editor.js for the live preview)

Add a new operation by adding one `OperationDef` to `Operations`.

---

## Project layout

```
Models/   ProcessSheet.cs, ProcessOperation.cs, OperationCatalog.cs
Data/     AppDbContext.cs, DbSeeder.cs
Controllers/ ProcessSheetsController.cs, HomeController.cs
Views/ProcessSheets/ Index, _SheetForm, Create, Edit, Details, Delete
wwwroot/  css/site.css, js/operations-editor.js
```

No external CDN - runs fully offline. To use SQL Server: swap the EF package to
`...SqlServer`, change `UseSqlite` to `UseSqlServer`, set the connection string.

## Drawing cropping packages

`Docnet.Core` (PDF -> bitmap) and `SkiaSharp` (crop/encode) power the diagram
embed. They ship native binaries for Windows out of the box. On Linux add
`SkiaSharp.NativeAssets.Linux.NoDependencies`. Cropping is best-effort: if it fails
for any reason the sheet still saves, just without the image.
