using OpenCvSharp;
using System.Text.Json;
using Tesseract;

class Program
{
    const string StatsFile  = "ocr_stats.json";
    const string LogFile    = "ocr_log.txt";
    const string CorrectDir = "correct_captchas";
    const string WrongDir   = "wrong_captchas";

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
        ResetSession();

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

            if (hit)
            {
                Directory.CreateDirectory(CorrectDir);
                string destFile = Path.Combine(CorrectDir,
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{groundTruth}.png");
                File.Copy("captcha.png", destFile, overwrite: true);
                Console.WriteLine($"  [SAVED] {destFile}");
            }
            else
            {
                Directory.CreateDirectory(WrongDir);
                string destFile = Path.Combine(WrongDir,
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_ocr{ocrResult}_ans{groundTruth}.png");
                File.Copy("captcha.png", destFile, overwrite: true);
                Console.WriteLine($"  [SAVED WRONG] {destFile}");
            }
        }

        Console.WriteLine("\n=====================================================");
        Console.WriteLine($"  Total rounds : {stats.TotalRuns}");
        Console.WriteLine($"  Accuracy     : {stats.Correct}/{stats.Total} = {stats.Accuracy:F1}%");
        Console.WriteLine($"  Stats file   : {Path.GetFullPath(StatsFile)}");
        Console.WriteLine($"  Log file     : {Path.GetFullPath(LogFile)}");
        Console.WriteLine("=====================================================");

        if (_tessEngineLazy.IsValueCreated) _tessEngineLazy.Value.Dispose();
    }

    static void ResetSession()
    {
        if (Directory.Exists(CorrectDir))
            Directory.Delete(CorrectDir, recursive: true);
        Directory.CreateDirectory(CorrectDir);

        if (Directory.Exists(WrongDir))
            Directory.Delete(WrongDir, recursive: true);
        Directory.CreateDirectory(WrongDir);

        if (File.Exists(StatsFile))
            File.Delete(StatsFile);

        if (File.Exists(LogFile))
            File.Delete(LogFile);

        Console.WriteLine("[INFO]  Session reset: stats, log and saved images cleared.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core recognition pipeline
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recognises a 3-digit CAPTCHA.
    ///
    /// Strategy overview:
    ///   1. Extract blue-channel pixels      → eliminates all gray noise/lines.
    ///   2. Undo wave distortion             → reverses the known fixed wave
    ///      parameters from the generator, restoring clean digit shapes.
    ///   3. Scale 3× (nearest-neighbour)     → preserves crisp binary edges.
    ///   4. Strategy A – full-image OCR      → try PSM.SingleLine / SingleWord
    ///      on the whole image; return immediately if 3 digits are found.
    ///   5. Strategy B – per-character crops → crop three overlapping regions,
    ///      add padding, deskew, then try PSM.SingleChar → SingleLine → Raw
    ///      in order, accepting the first response that yields a digit.
    /// </summary>
    static string RecognizeCaptcha(string imagePath)
    {
        using var blueMask = ExtractBlueText(imagePath);   // white = text

        // Dilate the binary mask before wave-undistortion to fill small
        // intra-stroke gaps introduced by anti-aliasing and colour thresholding.
        using var dilKernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
        using var dilated = new Mat();
        Cv2.Dilate(blueMask, dilated, dilKernel);

        // Reverse the known wave distortion applied by the generator so that
        // digit shapes are clean before scaling and OCR.
        using var deWaved = UndoWaveDistortion(dilated);

        const int Scale   = 4;
        int       scaledW = deWaved.Width  * Scale;
        int       scaledH = deWaved.Height * Scale;

        using var scaled = new Mat();
        Cv2.Resize(deWaved, scaled, new OpenCvSharp.Size(scaledW, scaledH),
                   interpolation: InterpolationFlags.Nearest);

        // Tesseract convention: black text on white background
        using var forOcr = new Mat();
        Cv2.BitwiseNot(scaled, forOcr);

        // ── Strategy A: full-image OCR ───────────────────────────────────────
        // Works well when character rotation is moderate; very fast when it
        // succeeds because it avoids per-character segmentation.
        string? fullResult = TryFullImageOcr(forOcr);
        if (fullResult != null) return fullResult;

        // ── Strategy B: per-character crops with deskew ──────────────────────
        // CAPTCHA generator layout (original 180 × 100 image):
        //   posX starts at 15, step = 28-38 px (mean ≈ 33), fontSize = 48-62 px
        //   Rotation pivot: (posX + fontSize/2, 50) ; angle ∈ [±15°, ±40°]
        //
        //   Char-0 rotation centre ≈  42 px  →  footprint ≈  [5,  80]
        //   Char-1 rotation centre ≈  75 px  →  footprint ≈ [38, 115]
        //   Char-2 rotation centre ≈ 108 px  →  footprint ≈ [68, 148]
        return RecognizePerChar(forOcr, scaledW, scaledH);
    }

    /// <summary>
    /// Attempts to read all three digits in one Tesseract call on the full
    /// (wave-corrected, scaled) image.  Returns null when fewer than 3 digits
    /// are found so the caller can fall back to per-character segmentation.
    /// </summary>
    static string? TryFullImageOcr(Mat forOcr)
    {
        // Add generous white padding so Tesseract doesn't clip edge strokes.
        using var padded = new Mat();
        Cv2.CopyMakeBorder(forOcr, padded, 25, 25, 30, 30,
                           BorderTypes.Constant, new Scalar(255));

        foreach (var psm in new[] { PageSegMode.SingleLine, PageSegMode.SingleWord, PageSegMode.SparseText })
        {
            string raw    = RunTesseract(padded, psm);
            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length == 3) return digits;
        }
        return null;
    }

    /// <summary>
    /// Recognises each digit individually using overlapping fixed-width crops
    /// and a rotation-search cascade to handle heavy per-character tilt.
    /// </summary>
    static string RecognizePerChar(Mat forOcr, int scaledW, int scaledH)
    {
        const int Scale = 4;
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

            using var crop = forOcr[new OpenCvSharp.Rect(x1, 0, x2 - x1, scaledH)];

            // White padding gives Tesseract clear context around the digit.
            using var padded = new Mat();
            Cv2.CopyMakeBorder(crop, padded, 20, 20, 10, 10,
                               BorderTypes.Constant, new Scalar(255));

            digits[idx] = TryRecognizeChar(padded);
        }

        return new string(digits);
    }

    /// <summary>
    /// Tries to read a single digit from <paramref name="paddedCrop"/>
    /// (black text on white, padded crop of the 4× scaled image).
    ///
    /// Strategy:
    ///   1. Deskew via minAreaRect (auto-detected angle) + Tesseract cascade.
    ///   2. Brute-force rotation search (±5°–45°) as fallback — accounts for
    ///      the ±15°–40° per-character tilt in the CAPTCHA generator.
    ///
    /// Stroke thickening (erode white background) is applied before each
    /// Tesseract call to improve recognition of thin anti-aliased strokes.
    /// </summary>
    static char TryRecognizeChar(Mat paddedCrop)
    {
        // ── Try 1: deskew-based (auto-detected angle) ─────────────────────
        using var deskewed = DeskewChar(paddedCrop);
        using var thickened1 = ThickenStrokes(deskewed);
        foreach (var psm in new[] { PageSegMode.SingleChar, PageSegMode.SingleLine, PageSegMode.SparseText })
        {
            string raw   = RunTesseract(thickened1, psm);
            string digit = new string(raw.Where(char.IsDigit).ToArray());
            if (digit.Length > 0) return digit[0];
        }

        // ── Try 2: brute-force rotation search ────────────────────────────
        // Characters are rotated ±15°–40°; search ±5°–45° in order of most
        // likely tilt (large angles first, then smaller adjustments).
        float[] searchAngles = { -35f, 35f, -25f, 25f, -15f, 15f, -45f, 45f, -5f, 5f };
        foreach (float angle in searchAngles)
        {
            using var rotated   = RotateMat(paddedCrop, angle);
            using var thickened2 = ThickenStrokes(rotated);
            foreach (var psm in new[] { PageSegMode.SingleChar, PageSegMode.SingleLine })
            {
                string raw   = RunTesseract(thickened2, psm);
                string digit = new string(raw.Where(char.IsDigit).ToArray());
                if (digit.Length > 0) return digit[0];
            }
        }

        return '?';
    }

    /// <summary>Thickens black strokes on a white background by eroding white.</summary>
    static Mat ThickenStrokes(Mat blackOnWhite)
    {
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new OpenCvSharp.Size(2, 2));
        var result = new Mat();
        Cv2.Erode(blackOnWhite, result, kernel);
        return result;
    }

    /// <summary>Rotates <paramref name="src"/> by <paramref name="angleDeg"/> degrees
    /// around its centre, filling the border with white (255).</summary>
    static Mat RotateMat(Mat src, float angleDeg)
    {
        var center = new Point2f(src.Width / 2f, src.Height / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var result = new Mat();
        Cv2.WarpAffine(src, result, rotMat, src.Size(),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255));
        return result;
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
    /// Condition:  B > 40  AND  (B − R) > 18  AND  (B − G) > 18
    /// Thresholds are intentionally loose so that anti-aliased edge pixels
    /// (blended with the white background) are still captured.
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

            using var mBR = new Mat(); Cv2.Threshold(bMinusR, mBR, 18, 255, ThresholdTypes.Binary);
            using var mBG = new Mat(); Cv2.Threshold(bMinusG, mBG, 18, 255, ThresholdTypes.Binary);
            using var mB  = new Mat(); Cv2.Threshold(ch[0],   mB,  40, 255, ThresholdTypes.Binary);

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
    /// Reverses the two-pass wave distortion applied by the CAPTCHA generator.
    ///
    /// Forward transform (generator code):
    ///   Pass 1 – horizontal wave: pass1[x + offsetX(y), y] = src[x, y]
    ///            where offsetX(y) = (int)(4 · sin(2π · y / 35))
    ///   Pass 2 – vertical wave:   dst[x, y + offsetY(x)] = pass1[x, y]
    ///            where offsetY(x) = (int)(3 · sin(2π · x / 45))
    ///
    /// Inverse (applied here):
    ///   Step 1 undo pass 2: pass1_rec[col, row] = distorted[col, row + offsetY(col)]
    ///   Step 2 undo pass 1: src_rec  [col, row] = pass1_rec [col + offsetX(row), row]
    ///
    /// Because the amplitudes and frequencies are fixed constants embedded in
    /// the generator, the reversal is exact (aside from pixels that were shifted
    /// out of the image boundary, which remain black/background).
    /// </summary>
    static Mat UndoWaveDistortion(Mat distorted)
    {
        int W = distorted.Width, H = distorted.Height;

        // Step 1: undo vertical wave (pass 2)
        // Forward: dst[col, row + offsetY(col)] = pass1[col, row]
        // Inverse: pass1_rec[col, row] = dst[col, row + offsetY(col)]
        using var pass1Rec = new Mat(H, W, MatType.CV_8UC1, new Scalar(0));
        for (int col = 0; col < W; col++)
        {
            int offsetY = (int)(3.0 * Math.Sin(2.0 * Math.PI * col / 45.0));
            for (int row = 0; row < H; row++)
            {
                int srcRow = row + offsetY;
                if ((uint)srcRow < (uint)H)
                    pass1Rec.Set<byte>(row, col, distorted.At<byte>(srcRow, col));
            }
        }

        // Step 2: undo horizontal wave (pass 1)
        // Forward: pass1[col + offsetX(row), row] = src[col, row]
        // Inverse: src_rec[col, row] = pass1_rec[col + offsetX(row), row]
        var result = new Mat(H, W, MatType.CV_8UC1, new Scalar(0));
        for (int row = 0; row < H; row++)
        {
            int offsetX = (int)(4.0 * Math.Sin(2.0 * Math.PI * row / 35.0));
            for (int col = 0; col < W; col++)
            {
                int srcCol = col + offsetX;
                if ((uint)srcCol < (uint)W)
                    result.Set<byte>(row, col, pass1Rec.At<byte>(row, srcCol));
            }
        }

        return result;
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

        // CAPTCHA input is numeric-only, so use the digits model for better accuracy.
        var engine = new TesseractEngine(tessData, "digits", EngineMode.LstmOnly);
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
        bool match = expected == actual;
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  Expected: {expected}  |  Actual: {actual}  |  Match: {(match ? "YES" : "NO")}  |  Running Accuracy: {runningAccuracy:F1}%";
        File.AppendAllText(LogFile, line + Environment.NewLine, System.Text.Encoding.UTF8);
    }
}

class OcrStats
{
    public int    TotalRuns { get; set; } = 0;
    public int    Correct   { get; set; } = 0;
    public int    Total     { get; set; } = 0;
    public double Accuracy  => Total > 0 ? Correct * 100.0 / Total : 0;
}
