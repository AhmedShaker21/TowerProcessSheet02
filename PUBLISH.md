# Publishing — run by double-clicking one file

The app is configured to **open your browser automatically** and serve at
`http://localhost:5080`. You can ship it as a single self-contained `.exe` that
runs on any Windows PC **without installing .NET**.

---

## Easiest way (Windows)

1. Make sure the **.NET 8 SDK** is installed on *your* build machine
   (`dotnet --version` → `8.x`). It does **not** need to be on the target PCs.
2. Double-click **`publish-windows.bat`** (or run it in a terminal).
3. When it finishes, open the folder it prints:
   ```
   bin\Release\net8.0\win-x64\publish\
   ```
4. That folder contains a single **`ProcessSheetSystem.exe`** (CSS/JS are baked
   inside it — no `wwwroot` folder needed). Copy that one file to any
   Windows 10/11 (64-bit) PC.
5. **Double-click `ProcessSheetSystem.exe`.** A small console window opens, then your default browser opens straight to the app. Keep the console window open while
   you use it; close it (or press Ctrl+C) to stop.

The database file `route_cards.db` is created next to the .exe on first run, so the
app's data lives in that same folder.

---

## The same thing from the command line

```bash
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

Output: `bin\Release\net8.0\win-x64\publish\ProcessSheetSystem.exe` — one file.

> `IncludeNativeLibrariesForSelfExtract=true` is important — it bundles the native
> libraries (PDFium for drawing rendering, SkiaSharp for cropping, SQLite) so the
> single file works on a clean PC.

For other systems, change the runtime id: `-r linux-x64` or `-r osx-x64`
(on Linux also add the package `SkiaSharp.NativeAssets.Linux.NoDependencies`).

---

## Make it feel like an app

- **Rename** `ProcessSheetSystem.exe` to anything you like (e.g. `Route Cards.exe`).
- **Pin it / make a shortcut:** right-click the exe → *Send to → Desktop
  (create shortcut)*. You can give the shortcut a custom icon.
- **Auto-start position / port:** the address is set in `Program.cs`
  (`const string AppUrl = "http://localhost:5080";`). Change the port there if 5080
  is taken.

## Let other PCs on the LAN open it

By default it listens on `localhost` (this PC only). To let other machines on the
factory network reach it, change in `Program.cs`:

```csharp
const string AppUrl = "http://0.0.0.0:5080";
```

Then others open `http://<this-pc-ip>:5080`. You may need to allow the app through
Windows Firewall the first time. (Auto-open still works on the host PC.)

---

## Troubleshooting

- **"Windows protected your PC" SmartScreen** on first run → *More info → Run
  anyway* (it's unsigned; that's expected for an internal tool).
- **Port 5080 in use** → change the port in `Program.cs` and re-publish.
- **Browser didn't open** → the console prints the URL; just open it manually.
- **Drawing preview/crop error** → already handled; on Windows the native libs are
  bundled, so it works on a clean PC.
