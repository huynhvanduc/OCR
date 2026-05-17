using OpenCvSharp;
using System.Text.Json;
using Tesseract;

class Program
{
    const string StatsFile  = "ocr_stats.json";
    const string LogFile    = "ocr_log.txt";
    const string CorrectDir = "correct_captchas";
    const string WrongDir   = "wrong_captchas";
    const string DenoiseDir = "zzz";
    const int    DefaultDpi = 200;

    static readonly Lazy<TesseractEngine> _tessEngineLazy = new(CreateTessEngine);

    static async Task Main(string[] args)
    {
        string baseUrl    = args.Length > 0 ? args[0] : "http://localhost:5000";
        int    rounds     = args.Length > 1 ? int.Parse(args[1]) : 2000;
        string captchaUrl = $"{baseUrl}/captcha";
        string answerUrl  = $"{baseUrl}/answer";

        ResetSession();
        Directory.CreateDirectory(DenoiseDir);

        var stats = LoadStats();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        const int MaxRetry = 3;

        for (int i = 1; i <= rounds; i++)
        {
            byte[]? imageBytes = null;
            for (int attempt = 1; attempt <= MaxRetry; attempt++)
            {
                try
                {
                    imageBytes = await client.GetByteArrayAsync(captchaUrl);
                    await File.WriteAllBytesAsync("captcha.png", imageBytes);
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt < MaxRetry) await Task.Delay(500 * attempt);
                }
            }
            if (imageBytes is null)
            {
                continue;
            }

            string groundTruth = "";
            try
            {
                var json = await client.GetStringAsync(answerUrl);
                groundTruth = JsonDocument.Parse(json).RootElement
                    .GetProperty("code").GetString() ?? "";
            }
            catch (Exception ex)
            {
                continue;
            }

            var (ocrResult, processedImage) = RecognizeCaptcha("captcha.png");
            using (processedImage)
            {
                string denoiseFile = Path.Combine(DenoiseDir,
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{groundTruth}.png");
                Cv2.ImWrite(denoiseFile, processedImage);
            }
            bool hit = ocrResult == groundTruth;

            stats.Total++;
            if (hit) stats.Correct++;
            stats.TotalRuns++;

            if (hit)
            {
                Console.WriteLine($"[{i}]:  OCR=\"{ocrResult}\"  Answer={groundTruth}  ✓  acc={stats.Accuracy:F1}%");
            }


            SaveStats(stats);
            AppendLog(groundTruth, ocrResult, stats.Accuracy);

            if (hit)
            {
                Directory.CreateDirectory(CorrectDir);
                string destFile = Path.Combine(CorrectDir,
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{groundTruth}.png");
                // File.Copy("captcha.png", destFile, overwrite: true);
            }
            else
            {
                Directory.CreateDirectory(WrongDir);
                string destFile = Path.Combine(WrongDir,
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_ocr{ocrResult}_ans{groundTruth}.png");
                //File.Copy("captcha.png", destFile, overwrite: true);
            }
        }

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

        if (Directory.Exists(DenoiseDir))
            Directory.Delete(DenoiseDir, recursive: true);
        Directory.CreateDirectory(DenoiseDir);

        if (File.Exists(StatsFile))
            File.Delete(StatsFile);

        if (File.Exists(LogFile))
            File.Delete(LogFile);

    }

    static (string result, Mat processedImage) RecognizeCaptcha(string imagePath)
    {
        using var blueMask = ExtractBlueText(imagePath);   // white = text

        using var dilKernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
        using var dilated = new Mat();
        Cv2.Dilate(blueMask, dilated, dilKernel);

        using var deWaved = UndoWaveDistortion(dilated);

        const int Scale   = 4;
        int       scaledW = deWaved.Width  * Scale;
        int       scaledH = deWaved.Height * Scale;

        using var scaled = new Mat();
        Cv2.Resize(deWaved, scaled, new OpenCvSharp.Size(scaledW, scaledH),
                   interpolation: InterpolationFlags.Nearest);

        var forOcr = new Mat();
        Cv2.BitwiseNot(scaled, forOcr);

        string? fullResult = TryFullImageOcr(forOcr);
        if (fullResult != null) return (fullResult, forOcr);

        return (RecognizePerChar(forOcr, scaledW, scaledH), forOcr);
    }

    static string? TryFullImageOcr(Mat forOcr)
    {
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

            using var padded = new Mat();
            Cv2.CopyMakeBorder(crop, padded, 20, 20, 10, 10,
                               BorderTypes.Constant, new Scalar(255));

            digits[idx] = TryRecognizeChar(padded);
        }

        return new string(digits);
    }

    static char TryRecognizeChar(Mat paddedCrop)
    {
        using var deskewed = DeskewChar(paddedCrop);
        using var thickened1 = ThickenStrokes(deskewed);
        foreach (var psm in new[] { PageSegMode.SingleChar, PageSegMode.SingleLine, PageSegMode.SparseText })
        {
            string raw   = RunTesseract(thickened1, psm);
            string digit = new string(raw.Where(char.IsDigit).ToArray());
            if (digit.Length > 0) return digit[0];
        }

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

    static Mat ThickenStrokes(Mat blackOnWhite)
    {
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new OpenCvSharp.Size(2, 2));
        var result = new Mat();
        Cv2.Erode(blackOnWhite, result, kernel);
        return result;
    }

    static Mat RotateMat(Mat src, float angleDeg)
    {
        var center = new Point2f(src.Width / 2f, src.Height / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var result = new Mat();
        Cv2.WarpAffine(src, result, rotMat, src.Size(),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255));
        return result;
    }

    static Mat ExtractBlueText(string imagePath)
    {
        using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
        Mat[] ch = Cv2.Split(src);
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

    static Mat UndoWaveDistortion(Mat distorted)
    {
        int W = distorted.Width, H = distorted.Height;

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

    static Mat DeskewChar(Mat blackOnWhite)
    {
        using var inv = new Mat();
        Cv2.BitwiseNot(blackOnWhite, inv);

        Cv2.FindContours(inv, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return blackOnWhite.Clone();

        float minAreaThresh = blackOnWhite.Width * blackOnWhite.Height * 0.005f;
        float cropCenterX   = blackOnWhite.Width / 2f;

        var pts = contours
            .Where(c => Cv2.ContourArea(c) > minAreaThresh)
            .OrderBy(c =>
            {
                var m  = Cv2.Moments(c);
                float cx = m.M00 > 1e-6 ? (float)(m.M10 / m.M00) : 0f;
                return Math.Abs(cx - cropCenterX);
            })
            .Take(2)
            .SelectMany(c => c)
            .ToArray();

        if (pts.Length < 5) return blackOnWhite.Clone();

        var   rect  = Cv2.MinAreaRect(pts);
        float angle = rect.Angle;

        if (angle < -45f) angle += 90f;

        if (Math.Abs(angle) < 3f) return blackOnWhite.Clone();

        var center = new Point2f(blackOnWhite.Width / 2f, blackOnWhite.Height / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, -angle, 1.0);
        var deskewed = new Mat();
        Cv2.WarpAffine(blackOnWhite, deskewed, rotMat, blackOnWhite.Size(),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255));
        return deskewed;
    }

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
            img.XRes = DefaultDpi;
            img.YRes = DefaultDpi;
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

        var engine = new TesseractEngine(tessData, "digits", EngineMode.LstmOnly);
        engine.SetVariable("tessedit_char_whitelist", "0123456789");
        engine.SetVariable("debug_file",
            System.OperatingSystem.IsWindows() ? "nul" : "/dev/null");
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
