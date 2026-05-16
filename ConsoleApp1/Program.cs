using IronOcr;
using OpenCvSharp;
using System.Text.Json;

class Program
{
    // File lưu thống kê accuracy tích lũy qua nhiều lần chạy
    const string StatsFile = "ocr_stats.json";

    static async Task Main(string[] args)
    {
        string baseUrl    = args.Length > 0 ? args[0] : "http://localhost:5000";
        int    rounds     = args.Length > 1 ? int.Parse(args[1]) : 20;
        string captchaUrl = $"{baseUrl}/captcha";
        string answerUrl  = $"{baseUrl}/answer";

        Console.WriteLine("=====================================================");
        Console.WriteLine(" OCR CAPTCHA — Self-Training Accuracy Loop (IronOCR)");
        Console.WriteLine("=====================================================");
        Console.WriteLine($"[INFO]  Server   : {baseUrl}");
        Console.WriteLine($"[INFO]  Rounds   : {rounds}");

        // Load stats tích lũy từ file (nếu có)
        var stats = LoadStats();
        Console.WriteLine($"[INFO]  History  : {stats.TotalRuns} rounds done before, best threshold so far = {stats.BestThreshold}");
        Console.WriteLine("-----------------------------------------------------");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        int[] thresholds = { 100, 120, 140, 160, 180 };

        for (int i = 1; i <= rounds; i++)
        {
            Console.WriteLine($"\n[Round {i}/{rounds}]");

            // ── Download CAPTCHA ──────────────────────────────────────────
            byte[] imageBytes;
            try
            {
                imageBytes = await client.GetByteArrayAsync(captchaUrl);
                await File.WriteAllBytesAsync("captcha.png", imageBytes);
                Console.Write($"  Downloaded : {imageBytes.Length} bytes  |  ");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR] Download: {ex.Message}");
                continue;
            }

            // ── Lấy ground truth từ /answer ───────────────────────────────
            string groundTruth = "";
            try
            {
                var json = await client.GetStringAsync(answerUrl);
                groundTruth = JsonDocument.Parse(json).RootElement
                    .GetProperty("code").GetString() ?? "";
                Console.WriteLine($"Truth = {groundTruth}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Answer: {ex.Message}");
                continue;
            }

            // ── Thử từng threshold, ghi kết quả vào stats ────────────────
            foreach (int threshold in thresholds)
            {
                string processedPath = $"captcha_t{threshold}.png";
                PreprocessImage("captcha.png", processedPath, threshold);

                string ocrText = RunIronOcr(processedPath);
                string digits  = new string(ocrText.Where(char.IsDigit).ToArray());
                bool   hit     = digits == groundTruth;

                // Cập nhật stats tích lũy
                if (!stats.ThresholdStats.ContainsKey(threshold))
                    stats.ThresholdStats[threshold] = new ThresholdStat();
                stats.ThresholdStats[threshold].Total++;
                if (hit) stats.ThresholdStats[threshold].Correct++;

                Console.WriteLine($"  t={threshold,3}: \"{digits}\" {(hit ? "✓" : $"✗ ({groundTruth})")}  acc={stats.ThresholdStats[threshold].Accuracy:F1}%");
            }

            stats.TotalRuns++;

            // Cập nhật best threshold dựa trên toàn bộ lịch sử
            stats.BestThreshold = stats.ThresholdStats
                .OrderByDescending(x => x.Value.Accuracy)
                .First().Key;

            // Lưu stats sau mỗi round để không mất dữ liệu
            SaveStats(stats);
            Console.ReadKey();
        }

        // ── Tổng kết ─────────────────────────────────────────────────────
        Console.WriteLine("\n=====================================================");
        Console.WriteLine(" ACCURACY SUMMARY  (tích lũy tất cả lịch sử)");
        Console.WriteLine("=====================================================");
        Console.WriteLine($"  Total rounds trained : {stats.TotalRuns}");
        Console.WriteLine();
        Console.WriteLine("  Per-threshold accuracy:");
        foreach (var kv in stats.ThresholdStats.OrderByDescending(x => x.Value.Accuracy))
        {
            var stat = kv.Value;
            string bar = new string('█', (int)(stat.Accuracy / 5));
            Console.WriteLine($"    t={kv.Key,3}: {stat.Correct,4}/{stat.Total,-4}  {stat.Accuracy,5:F1}%  {bar}");
        }
        Console.WriteLine($"\n  ★  Best threshold : {stats.BestThreshold}");
        Console.WriteLine($"  Stats saved to    : {Path.GetFullPath(StatsFile)}");
        Console.WriteLine("=====================================================");
    }

    static void PreprocessImage(string inputPath, string outputPath, int threshold = 150)
    {
        const int ScaleFactor = 4;

        // Đọc ảnh gốc bằng OpenCV
        using var src = Cv2.ImRead(inputPath, ImreadModes.Color);

        // 1. Phóng to ScaleFactor× để Tesseract nhận diện tốt hơn
        using var scaled = new Mat();
        Cv2.Resize(src, scaled, new OpenCvSharp.Size(src.Width * ScaleFactor, src.Height * ScaleFactor),
                   interpolation: InterpolationFlags.Cubic);

        // 2. Chuyển sang ảnh xám
        using var gray = new Mat();
        Cv2.CvtColor(scaled, gray, ColorConversionCodes.BGR2GRAY);

        // 3. Khử nhiễu bằng FastNlMeansDenoising
        using var denoised = new Mat();
        Cv2.FastNlMeansDenoising(gray, denoised, h: 10, templateWindowSize: 7, searchWindowSize: 21);

        // 4. Nhị phân hóa (trắng đen) bằng ngưỡng cố định
        using var binary = new Mat();
        Cv2.Threshold(denoised, binary, threshold, 255, ThresholdTypes.Binary);

        // 5. Lưu file ảnh trắng đen
        Cv2.ImWrite(outputPath, binary);
    }

    static string RunIronOcr(string imagePath)
    {
        try
        {
            var ocr = new IronTesseract();
            ocr.Configuration.WhiteListCharacters   = "0123456789";
            ocr.Configuration.TesseractVersion      = TesseractVersion.Tesseract5;
            ocr.Configuration.EngineMode            = TesseractEngineMode.LstmOnly;
            ocr.Configuration.PageSegmentationMode  = TesseractPageSegmentationMode.SingleLine;
            ocr.Configuration.ReadBarCodes          = false;

            using var input = new OcrInput();
            input.LoadImage(imagePath);
            input.EnhanceResolution(300);
            input.Deskew();

            return ocr.Read(input).Text.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    static OcrStats LoadStats()
    {
        if (!File.Exists(StatsFile)) return new OcrStats();
        try
        {
            var json = File.ReadAllText(StatsFile);
            return JsonSerializer.Deserialize<OcrStats>(json) ?? new OcrStats();
        }
        catch { return new OcrStats(); }
    }

    static void SaveStats(OcrStats stats)
    {
        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StatsFile, json);
    }
}

class OcrStats
{
    public int                          TotalRuns       { get; set; } = 0;
    public int                          BestThreshold   { get; set; } = 150;
    public Dictionary<int, ThresholdStat> ThresholdStats { get; set; } = new();
}

class ThresholdStat
{
    public int Correct { get; set; } = 0;
    public int Total   { get; set; } = 0;
    public double Accuracy => Total > 0 ? Correct * 100.0 / Total : 0;
}
