using OpenCvSharp;
using System.Text.Json;
using Tesseract;

class Program
{
    const string StatsFile = "ocr_stats.json";
    const string LogFile   = "ocr_log.csv";

    // Reuse Tesseract engine to avoid repeated initialisation overhead.
    // Lazy<T> ensures thread-safe single initialisation.
    static readonly Lazy<TesseractEngine> _tessEngineLazy = new(CreateTessEngine);

    static async Task Main(string[] args)
    {
        string baseUrl    = args.Length > 0 ? args[0] : "http://localhost:5000";
        int    rounds     = args.Length > 1 ? int.Parse(args[1]) : 20;
        string captchaUrl = $"{baseUrl}/captcha";
        string answerUrl  = $"{baseUrl}/answer";

        Console.WriteLine("=====================================================");
        Console.WriteLine(" OCR CAPTCHA — Color-Extraction + Segment Strategy");
        Console.WriteLine("=====================================================");
        Console.WriteLine($"[INFO]  Server   : {baseUrl}");
        Console.WriteLine($"[INFO]  Rounds   : {rounds}");

        var stats = LoadStats();
        Console.WriteLine($"[INFO]  History  : {stats.TotalRuns} rounds done before, accuracy = {stats.Accuracy:F1}%");
        Console.WriteLine("-----------------------------------------------------");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        const int MaxRetry = 3;

        for (int i = 1; i <= rounds; i++)
        {
            Console.WriteLine($"\n[Round {i}/{rounds}]");

            // ── Download CAPTCHA ──────────────────────────────────────────
            byte[]? imageBytes = null;
            for (int attempt = 1; attempt <= MaxRetry; attempt++)
            {
                try
                {
                    imageBytes = await client.GetByteArrayAsync(captchaUrl);
                    await File.WriteAllBytesAsync("captcha.png", imageBytes);
                    Console.Write($"  Downloaded : {imageBytes.Length} bytes");
                    if (attempt > 1) Console.Write($" (attempt {attempt})");
                    Console.Write("  |  ");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [WARN] Download attempt {attempt}/{MaxRetry}: {ex.Message}");
                    if (attempt < MaxRetry) await Task.Delay(500 * attempt);
                }
            }
            if (imageBytes is null)
            {
                Console.WriteLine("  [ERROR] All download attempts failed, skipping round.");
                continue;
            }

            // ── Get ground truth ──────────────────────────────────────────
            string groundTruth = "";
            try
            {
                var json = await client.GetStringAsync(answerUrl);
                groundTruth = JsonDocument.Parse(json).RootElement
                    .GetProperty("code").GetString() ?? "";
                Console.WriteLine($"Answer = {groundTruth}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Answer: {ex.Message}");
                continue;
            }

            // ── Recognise ────────────────────────────────────────────────
            string ocrResult = RecognizeCaptcha("captcha.png");
            bool   hit       = ocrResult == groundTruth;

            stats.Total++;
            if (hit) stats.Correct++;
            stats.TotalRuns++;

            Console.WriteLine($"  OCR=\"{ocrResult}\"  Answer={groundTruth}  {(hit ? "✓" : "✗")}  acc={stats.Accuracy:F1}%");
            SaveStats(stats);
            AppendLog(groundTruth, ocrResult, stats.Accuracy);
        }

        Console.WriteLine("\n=====================================================");
        Console.WriteLine($"  Total rounds : {stats.TotalRuns}");
        Console.WriteLine($"  Accuracy     : {stats.Correct}/{stats.Total} = {stats.Accuracy:F1}%");
        Console.WriteLine($"  Stats file   : {Path.GetFullPath(StatsFile)}");
        Console.WriteLine($"  Log file     : {Path.GetFullPath(LogFile)}");
        Console.WriteLine("=====================================================");

        if (_tessEngineLazy.IsValueCreated) _tessEngineLazy.Value.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core recognition pipeline
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recognises a 3-digit CAPTCHA using colour-based text extraction and
    /// position-aware per-character segmentation + deskew.
    ///
    /// Strategy overview:
    ///   1. Extract blue-channel pixels  → eliminates all gray noise/lines.
    ///   2. Scale 3× (nearest-neighbour) → preserves crisp binary edges.
    ///   3. Crop three overlapping regions based on the known generator layout.
    ///   4. Deskew each region using minAreaRect of the most central contour.
    ///   5. Tesseract PSM.SingleChar on each deskewed crop.
    /// </summary>
    static string RecognizeCaptcha(string imagePath)
    {
        using var blueMask = ExtractBlueText(imagePath);   // white = text

        const int Scale  = 3;
        int       scaledW = blueMask.Width  * Scale;
        int       scaledH = blueMask.Height * Scale;

        using var scaled = new Mat();
        Cv2.Resize(blueMask, scaled, new OpenCvSharp.Size(scaledW, scaledH),
                   interpolation: InterpolationFlags.Nearest);

        // Tesseract convention: black text on white background
        using var forOcr = new Mat();
        Cv2.BitwiseNot(scaled, forOcr);

        // CAPTCHA generator layout (original 180 × 100 image):
        //   posX starts at 15, step = 28-38 px (mean ≈ 33), fontSize = 48-62 px
        //   Rotation pivot: (posX + fontSize/2, 50) ; angle ∈ [±15°, ±40°]
        //   At max rotation (40°) the horizontal footprint of each char ≈ 62 px.
        //
        //   Char-0 rotation centre ≈  42 px  →  footprint ≈  [5,  80]
        //   Char-1 rotation centre ≈  75 px  →  footprint ≈ [38, 115]
        //   Char-2 rotation centre ≈ 108 px  →  footprint ≈ [68, 148]
        //
        // Each crop is intentionally wide so the target character is always
        // fully captured; the DeskewChar method focuses on the most centrally
        // located contour to ignore partial bleed from adjacent characters.
        var charRegions = new (int x1, int x2)[]
        {
            (5   * Scale, 80  * Scale),                          // Digit 0
            (38  * Scale, 115 * Scale),                          // Digit 1
            (68  * Scale, Math.Min(scaledW, 148 * Scale)),       // Digit 2
        };

        var digits = new char[3];
        for (int idx = 0; idx < 3; idx++)
        {
            (int x1, int x2) = charRegions[idx];
            x1 = Math.Max(0, x1);
            x2 = Math.Min(scaledW, x2);
            if (x2 <= x1) { digits[idx] = '?'; continue; }

            using var crop     = forOcr[new OpenCvSharp.Rect(x1, 0, x2 - x1, scaledH)];
            using var deskewed = DeskewChar(crop);
            string    raw      = RunTesseract(deskewed, PageSegMode.SingleChar);
            string    digit    = new string(raw.Where(char.IsDigit).ToArray());
            digits[idx]        = digit.Length > 0 ? digit[0] : '?';
        }

        return new string(digits);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Image processing helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Isolates dark-blue text pixels from the CAPTCHA image.
    ///
    /// Text colour (SkiaSharp RGB → OpenCV BGR):
    ///   ch[0]=B ∈ [90, 174],  ch[1]=G ∈ [0, 49],  ch[2]=R ∈ [0, 49]
    /// Noise / background:  R ≈ G ≈ B  (gray or near-white) → excluded
    ///
    /// Condition:  B > 70  AND  (B − R) > 35  AND  (B − G) > 35
    /// Unsigned subtraction naturally clamps to 0 when B < R or B < G,
    /// which correctly rejects gray pixels without an explicit sign check.
    ///
    /// Returns a binary mask: white (255) = text pixel, black (0) = background.
    /// </summary>
    static Mat ExtractBlueText(string imagePath)
    {
        using var src = Cv2.ImRead(imagePath, ImreadModes.Color); // BGR
        Mat[] ch = Cv2.Split(src);  // ch[0]=B  ch[1]=G  ch[2]=R
        try
        {
            using var bMinusR = new Mat(); Cv2.Subtract(ch[0], ch[2], bMinusR);
            using var bMinusG = new Mat(); Cv2.Subtract(ch[0], ch[1], bMinusG);

            using var mBR = new Mat(); Cv2.Threshold(bMinusR, mBR, 35, 255, ThresholdTypes.Binary);
            using var mBG = new Mat(); Cv2.Threshold(bMinusG, mBG, 35, 255, ThresholdTypes.Binary);
            using var mB  = new Mat(); Cv2.Threshold(ch[0],   mB,  70, 255, ThresholdTypes.Binary);

            var mask = new Mat();
            using var tmp = new Mat();
            Cv2.BitwiseAnd(mBR, mBG, tmp);
            Cv2.BitwiseAnd(tmp, mB, mask);

            // Morphological close: fills small intra-stroke gaps left by colour filtering
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

            return mask;
        }
        finally
        {
            foreach (var c in ch) c.Dispose();
        }
    }

    /// <summary>
    /// Deskews a character crop by estimating the dominant text angle via
    /// minAreaRect of the most centrally-located contour(s).
    ///
    /// Input / output: black text on white background (Tesseract convention).
    /// Uses horizontal-centre proximity to select the target character and
    /// avoid being biased by partial bleed-in from adjacent characters.
    /// </summary>
    static Mat DeskewChar(Mat blackOnWhite)
    {
        // FindContours needs white objects on black
        using var inv = new Mat();
        Cv2.BitwiseNot(blackOnWhite, inv);

        Cv2.FindContours(inv, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return blackOnWhite.Clone();

        // 0.5 % of the crop area: rejects isolated noise dots (typically 1-4 px²)
        // while always keeping digit strokes (hundreds of pixels at 3× scale).
        float minAreaThresh = blackOnWhite.Width * blackOnWhite.Height * 0.005f;
        float cropCenterX   = blackOnWhite.Width / 2f;

        // Take up to 2 contours that are (a) large enough and (b) closest
        // to the horizontal centre of the crop — that is the target digit.
        var pts = contours
            .Where(c => Cv2.ContourArea(c) > minAreaThresh)
            .OrderBy(c =>
            {
                var m  = Cv2.Moments(c);
                float cx = m.M00 > 1e-6 ? (float)(m.M10 / m.M00) : 0f; // guard against degenerate moment
                return Math.Abs(cx - cropCenterX);
            })
            .Take(2)
            .SelectMany(c => c)
            .ToArray();

        if (pts.Length < 5) return blackOnWhite.Clone();

        var   rect  = Cv2.MinAreaRect(pts);
        float angle = rect.Angle;

        // minAreaRect returns angle ∈ (-90°, 0°].  After adjusting angles < -45°
        // by +90° the full range maps to (-45°, +45°], where sign indicates
        // whether the box leans left (<0) or right (>0).
        if (angle < -45f) angle += 90f;

        if (Math.Abs(angle) < 3f) return blackOnWhite.Clone(); // negligible — skip

        // Apply -angle to cancel the detected tilt
        var center = new Point2f(blackOnWhite.Width / 2f, blackOnWhite.Height / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, -angle, 1.0);
        var deskewed = new Mat();
        Cv2.WarpAffine(blackOnWhite, deskewed, rotMat, blackOnWhite.Size(),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255)); // white border
        return deskewed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tesseract helpers
    // ─────────────────────────────────────────────────────────────────────────

    static string RunTesseract(Mat img, PageSegMode psm = PageSegMode.SingleChar)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ocrchar_{Guid.NewGuid():N}.png");
        try
        {
            Cv2.ImWrite(tmp, img);
            return RunTesseract(tmp, psm);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    static string RunTesseract(string imagePath, PageSegMode psm = PageSegMode.SingleChar)
    {
        try
        {
            var engine = GetTessEngine();
            engine.DefaultPageSegMode = psm;
            using var img  = Pix.LoadFromFile(imagePath);
            using var page = engine.Process(img);
            return page.GetText().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    static TesseractEngine GetTessEngine() => _tessEngineLazy.Value;

    static TesseractEngine CreateTessEngine()
    {
        string tessData = Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
                          ?? Path.Combine(AppContext.BaseDirectory, "tessdata");

        if (!Directory.Exists(tessData))
            throw new DirectoryNotFoundException(
                $"tessdata not found at '{tessData}'. " +
                "Place the tessdata folder next to the executable or set the TESSDATA_PREFIX environment variable.");

        var engine = new TesseractEngine(tessData, "eng", EngineMode.LstmOnly);
        engine.SetVariable("tessedit_char_whitelist", "0123456789");
        return engine;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stats persistence
    // ─────────────────────────────────────────────────────────────────────────

    static OcrStats LoadStats()
    {
        if (!File.Exists(StatsFile)) return new OcrStats();
        try
        {
            return JsonSerializer.Deserialize<OcrStats>(File.ReadAllText(StatsFile))
                   ?? new OcrStats();
        }
        catch { return new OcrStats(); }
    }

    static void SaveStats(OcrStats stats)
    {
        File.WriteAllText(StatsFile,
            JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void AppendLog(string expected, string actual, double runningAccuracy)
    {
        using var writer = new StreamWriter(LogFile, append: true, System.Text.Encoding.UTF8);
        // Write header only when the file is empty (newly created or truncated)
        if (writer.BaseStream.Position == 0)
            writer.WriteLine("Timestamp,Expected,Actual,Match,RunningAccuracy(%)");
        bool match = expected == actual;
        writer.WriteLine(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{CsvEscape(expected)},{CsvEscape(actual)},{(match ? "1" : "0")},{runningAccuracy:F1}");
    }

    static string CsvEscape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

class OcrStats
{
    public int    TotalRuns { get; set; } = 0;
    public int    Correct   { get; set; } = 0;
    public int    Total     { get; set; } = 0;
    public double Accuracy  => Total > 0 ? Correct * 100.0 / Total : 0;
}
