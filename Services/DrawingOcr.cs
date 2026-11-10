using System.Text.RegularExpressions;
using SkiaSharp;
using Tesseract;

namespace TowerProcessSheet.Services;

/// <summary>
/// Reads a drawing field from the page IMAGE when the PDF has no text there (a title-block table that
/// was pasted in as a picture, or a scanned page). The marked rectangle is rendered at high resolution,
/// cleaned up (borders trimmed, black/white) and handed to Tesseract.
/// Needs tessdata\eng.traineddata next to the exe; without it (or if the engine can't start) every call
/// simply returns nothing and the app behaves exactly as before.
/// </summary>
public static class DrawingOcr
{
    private static readonly object Gate = new();
    private static TesseractEngine? _engine;
    private static bool _initTried;

    /// <summary>Why the OCR engine could not start, when it couldn't (null while it works).</summary>
    public static string? InitError { get; private set; }

    private static string _lastTrace = "";

    private static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  OCR: " + text + Environment.NewLine); }
        catch { }
    }

    /// <summary>One line telling whether OCR works and what it last saw — shown next to "nothing was read" messages.</summary>
    public static string Status
    {
        get
        {
            lock (Gate)
            {
                if (GetEngine() == null) return "OCR is NOT available: " + InitError;
                return "OCR is ready" + (_lastTrace.Length > 0 ? " (last read: " + _lastTrace + ")" : "");
            }
        }
    }

    private const string Digits = "0123456789.";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    // Narrowing the alphabet to what the field can really contain removes most misreads
    // (e.g. a "*" read as "S", a border line read as "|" or "1").
    private static string? Whitelist(string key) => key switch
    {
        "qty" or "cutLength" => Digits,
        "section" => Upper + "xX*.-/",
        "material" => Upper + "/. ",
        "partNo" or "drawingNo" => Upper + "abcdefghijklmnopqrstuvwxyz-_/. ",
        _ => null,
    };

    /// <summary>True when the OCR engine is installed and starts.</summary>
    public static bool IsAvailable
    {
        get { lock (Gate) return GetEngine() != null; }
    }

    // Call only while holding Gate.
    private static TesseractEngine? GetEngine()
    {
        if (_engine != null) return _engine;
        if (_initTried) return null;
        _initTried = true;
        try
        {
            var baseDir = AppContext.BaseDirectory;
            // A single-file exe has no assembly folder, so tell Tesseract where its native x64 folder is.
            // (Looked up by name so a different spelling of the class in another package version can't break the build.)
            var env = typeof(TesseractEngine).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name is "TesseractEnviornment" or "TesseractEnvironment");
            env?.GetProperty("CustomSearchPath")?.SetValue(null, baseDir);

            if (!File.Exists(Path.Combine(baseDir, "x64", "tesseract50.dll")))
            {
                InitError = "the OCR native files (x64\\tesseract50.dll) were not found next to the exe: " + baseDir;
                Log(InitError);
                return null;
            }

            var dataDir = Path.Combine(baseDir, "tessdata");
            if (!File.Exists(Path.Combine(dataDir, "eng.traineddata")))
            {
                InitError = "tessdata\\eng.traineddata was not found next to the exe.";
                Log(InitError);
                return null;
            }
            _engine = new TesseractEngine(dataDir, "eng", EngineMode.LstmOnly);
        }
        catch (Exception ex)
        {
            InitError = ex.GetType().Name + ": " + ex.Message;
            _engine = null;
            Log("could not start - " + ex);
        }
        if (_engine != null) Log("engine started");
        return _engine;
    }

    /// <summary>Text read from the image inside each named rectangle (only fields where something was read).</summary>
    public static Dictionary<string, string> ReadRegions(byte[] pdfBytes, Dictionary<string, DrawingRegionExtractor.NormRect> fields)
    {
        var result = new Dictionary<string, string>();
        if (fields.Count == 0) return result;
        lock (Gate)
        {
            var engine = GetEngine();
            if (engine == null) return result;

            var names = fields.Keys.ToList();
            var crops = DrawingImageExtractor.CropManyNormalized(pdfBytes,
                names.Select(n => new DrawingImageExtractor.NormRect(fields[n].X, fields[n].Y, fields[n].W, fields[n].H)).ToList());

            for (int i = 0; i < names.Count; i++)
            {
                if (crops[i] == null) continue;
                try
                {
                    var text = ReadOne(engine, crops[i]!, names[i]);
                    if (text.Length > 0) result[names[i]] = text;
                }
                catch (Exception ex)
                {
                    // one unreadable field must not stop the others
                    _lastTrace = names[i] + " failed: " + ex.GetType().Name + ": " + ex.Message;
                    Log(_lastTrace);
                }
            }
        }
        return result;
    }

    private static string ReadOne(TesseractEngine engine, byte[] cropPng, string key)
    {
        using var bmp = SKBitmap.Decode(cropPng);
        if (bmp == null || bmp.Width < 6 || bmp.Height < 6) return "";

        // Trim the cell border that usually sits right at the edge of a marked box.
        int inset = Math.Max(3, (int)(Math.Min(bmp.Width, bmp.Height) * 0.06));
        if (bmp.Width - 2 * inset < 8 || bmp.Height - 2 * inset < 8) inset = 0;
        using var sub = new SKBitmap();
        if (!bmp.ExtractSubset(sub, SKRectI.Create(inset, inset, bmp.Width - 2 * inset, bmp.Height - 2 * inset))) return "";

        // Tesseract reads best when the letters are about 30 px tall.
        using var big = sub.Resize(new SKImageInfo(sub.Width * 2, sub.Height * 2), SKFilterQuality.High);
        if (big == null) return "";
        var px = big.Pixels;

        // Black / white with an automatic (Otsu) threshold, on a white margin.
        var hist = new int[256];
        var lum = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            lum[i] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
            hist[lum[i]]++;
        }
        int threshold = OtsuThreshold(hist, px.Length);

        const int pad = 30;
        int w = big.Width + 2 * pad, h = big.Height + 2 * pad;
        var outPx = new SKColor[w * h];
        Array.Fill(outPx, SKColors.White);
        for (int y = 0; y < big.Height; y++)
            for (int x = 0; x < big.Width; x++)
                if (lum[y * big.Width + x] <= threshold) outPx[(y + pad) * w + (x + pad)] = SKColors.Black;
        using var clean = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        clean.Pixels = outPx;
        using var image = SKImage.FromBitmap(clean);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        using var pix = Pix.LoadFromMemory(data.ToArray());
        engine.SetVariable("tessedit_char_whitelist", Whitelist(key) ?? "");
        var mode = (double)w / h >= 4 ? PageSegMode.SingleLine : PageSegMode.SingleBlock;
        using var page = engine.Process(pix, mode);
        float conf = page.GetMeanConfidence();
        var raw = Clean(page.GetText());
        _lastTrace = key + " = '" + raw + "' (confidence " + conf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")";
        if (conf < 0.55f) return "";   // linework or noise, not text
        return raw;
    }

    private static int OtsuThreshold(int[] hist, int total)
    {
        double sum = 0;
        for (int t = 0; t < 256; t++) sum += t * (double)hist[t];
        double sumB = 0, best = -1;
        int wB = 0, threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }

    private static string Clean(string? text)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        text = text.Trim(' ', '|', '_', '[', ']', '{', '}', '(', ')', '~', '`', '\'', '"', '=');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Any(char.IsLetterOrDigit) ? text : "";
    }
}